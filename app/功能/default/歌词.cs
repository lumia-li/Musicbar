﻿﻿﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;

namespace MusicBar;

public partial class MainWindow : Window
{
    private void StartLyricTimer()
    {
        if (_lyricTimer is not null)
        {
            return;
        }

        _lyricTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _lyricTimer.Tick += LyricTimer_Tick;
        _lyricTimer.Start();
    }

    private void StopLyricTimer()
    {
        if (_lyricTimer is null)
        {
            return;
        }

        _lyricTimer.Stop();
        _lyricTimer.Tick -= LyricTimer_Tick;
        _lyricTimer = null;
    }

    private void LoadWidgetPreferences()
    {
        try
        {
            var path = GetWidgetPreferencesPath();
            if (!File.Exists(path))
            {
                return;
            }

            var json = File.ReadAllText(path);
            var preferences = JsonSerializer.Deserialize<WidgetPreferences>(json);
            if (preferences is null)
            {
                return;
            }

            _progressBarDisplayMode = ParseProgressBarDisplayMode(preferences.ProgressBarDisplayMode);

            // 加载圆角偏好（范围限制 0~23）
            if (preferences.CornerRadius >= 0d && preferences.CornerRadius <= 23d)
            {
                _widgetCornerRadius = preferences.CornerRadius;
            }

            if (preferences.Opacity >= 0.2d && preferences.Opacity <= 1d)
            {
                _widgetOpacity = preferences.Opacity;
            }

            _useGradientBackground = preferences.UseGradientBackground;
            _gradientBackgroundMode = ParseGradientBackgroundMode(preferences.GradientBackgroundMode);
        }
        catch
        {
        }
    }

    private void SaveWidgetPreferences()
    {
        try
        {
            var path = GetWidgetPreferencesPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var preferences = new WidgetPreferences
            {
                ProgressBarDisplayMode = _progressBarDisplayMode.ToString(),
                CornerRadius = _widgetCornerRadius,
                Opacity = _widgetOpacity,
                UseGradientBackground = _useGradientBackground,
                GradientBackgroundMode = _gradientBackgroundMode.ToString()
            };
            File.WriteAllText(path, JsonSerializer.Serialize(preferences));
        }
        catch
        {
        }
    }

    private static ProgressBarDisplayMode ParseProgressBarDisplayMode(string? rawValue)
    {
        return Enum.TryParse<ProgressBarDisplayMode>(rawValue, ignoreCase: true, out var mode)
            ? mode
            : ProgressBarDisplayMode.InlineBottomBar;
    }

    private static GradientBackgroundMode ParseGradientBackgroundMode(string? rawValue)
    {
        return Enum.TryParse<GradientBackgroundMode>(rawValue, ignoreCase: true, out var mode)
            ? mode
            : GradientBackgroundMode.Linear;
    }

    private string GetWidgetPreferencesPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "MusicBar", "widget-preferences.json");
    }

    private void SetProgressBarDisplayMode(ProgressBarDisplayMode mode)
    {
        _progressBarDisplayMode = mode;
        ApplyProgressBarDisplayMode();
        SaveWidgetPreferences();
    }

    private void ApplyProgressBarDisplayMode()
    {
        InlineProgressHost.Visibility = _progressBarDisplayMode == ProgressBarDisplayMode.InlineBottomBar
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_progressBarDisplayMode == ProgressBarDisplayMode.Hidden)
        {
            FloatingProgressPopup.IsOpen = false;
        }

        UpdateProgressModeMenuItems();
        UpdatePlaybackProgressUi(_lastPlaybackProgressSnapshot);
    }

    private void UpdateProgressModeMenuItems()
    {
        InlineProgressModeMenuItem.Header = (_progressBarDisplayMode == ProgressBarDisplayMode.InlineBottomBar ? "✓ " : string.Empty) + "固定下边栏";
        FloatingProgressModeMenuItem.Header = (_progressBarDisplayMode == ProgressBarDisplayMode.FloatingBelow ? "✓ " : string.Empty) + "悬浮下方";
        HiddenProgressModeMenuItem.Header = (_progressBarDisplayMode == ProgressBarDisplayMode.Hidden ? "✓ " : string.Empty) + "不显示时间条";
    }

    private void ResetPlaybackProgressUi()
    {
        _lastPlaybackProgressSnapshot = null;
        ClearPendingSeekVisualHold();
        InlineProgressFill.Width = 0d;
        FloatingProgressFill.Width = 0d;
        FloatingProgressCurrentText.Text = "0:00";
        FloatingProgressDurationText.Text = "--:--";
        FloatingProgressPopup.IsOpen = false;
    }

    private void UpdatePlaybackProgressUi(PlaybackProgressSnapshot? playbackProgress)
    {
        if (_isProgressDragging) return;

        var hasRenderableProgress = playbackProgress is not null && playbackProgress.DurationMs > 1000d;
        if (!hasRenderableProgress)
        {
            InlineProgressFill.Width = 0d;
            FloatingProgressFill.Width = 0d;
            FloatingProgressCurrentText.Text = "0:00";
            FloatingProgressDurationText.Text = "--:--";
            RefreshFloatingProgressPopupVisibility(hasRenderableProgress: false);
            return;
        }

        var durationMs = Math.Max(1d, playbackProgress!.DurationMs);
        var positionMs = Clamp(playbackProgress.PositionMs, 0d, durationMs);
        var progressRatio = Clamp(positionMs / durationMs, 0d, 1d);

        UpdateProgressFillWidth(InlineProgressHost, InlineProgressFill, progressRatio);
        UpdateProgressFillWidth(FloatingProgressTrackHost, FloatingProgressFill, progressRatio);
        FloatingProgressCurrentText.Text = FormatPlaybackTime(positionMs);
        FloatingProgressDurationText.Text = FormatPlaybackTime(durationMs);

        RefreshFloatingProgressPopupVisibility(hasRenderableProgress: true);
    }

    private static void UpdateProgressFillWidth(FrameworkElement host, FrameworkElement fill, double progressRatio)
    {
        var hostWidth = host.ActualWidth;
        if (hostWidth <= 0d)
        {
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            hostWidth = host.DesiredSize.Width;
        }

        fill.Width = Math.Max(0d, hostWidth * progressRatio);
    }

    private void RefreshFloatingProgressPopupVisibility(bool hasRenderableProgress)
    {
        var shouldShow = _progressBarDisplayMode == ProgressBarDisplayMode.FloatingBelow
            && hasRenderableProgress
            && !_isContextMenuOpen
            && !PlayerPickerPopup.IsOpen
            && IsVisible
            && IsLoaded;

        if (shouldShow && !FloatingProgressPopup.IsOpen)
        {
            CenterFloatingProgressPopup();
        }

        FloatingProgressPopup.IsOpen = shouldShow;
    }

    private void CenterFloatingProgressPopup()
    {
        var popupWidth = Math.Max(220d, WidgetBorder.ActualWidth - 28d);
        FloatingProgressPopupPanel.Width = popupWidth;
        FloatingProgressPopupPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        UpdateFloatingProgressPlacementCallback();
        RefreshOpenFloatingProgressPopupPlacement();
    }

    private void UpdateFloatingProgressPlacementCallback()
    {
        var mode = GetFloatingProgressPlacementMode();
        FloatingProgressPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            MenuPlacement.GetPlayerPickerPlacement(popupSize, targetSize, offset, mode);
    }

    private PlayerPickerPlacementMode GetFloatingProgressPlacementMode()
    {
        if (!_isDocked)
        {
            return PlayerPickerPlacementMode.Below;
        }

        return GetTaskbarPlacement().Edge switch
        {
            AppBarEdge.Bottom => PlayerPickerPlacementMode.Above,
            AppBarEdge.Top => PlayerPickerPlacementMode.Below,
            AppBarEdge.Left => PlayerPickerPlacementMode.Right,
            AppBarEdge.Right => PlayerPickerPlacementMode.Left,
            _ => PlayerPickerPlacementMode.Below
        };
    }

    private void RefreshOpenFloatingProgressPopupPlacement()
    {
        FloatingProgressPopup.HorizontalOffset = 0d;
        FloatingProgressPopup.VerticalOffset = 0d;
        if (!FloatingProgressPopup.IsOpen)
        {
            return;
        }

        FloatingProgressPopup.HorizontalOffset = 1d;
        FloatingProgressPopup.HorizontalOffset = 0d;
    }

    private void RefreshFloatingProgressPopupPlacement()
    {
        CenterFloatingProgressPopup();
    }

    private void Progress_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_session is null) return;
        _isProgressDragging = true;
        var host = (FrameworkElement)sender;
        host.CaptureMouse();
        SeekProgressFromMouse(host, e);
    }

    private void Progress_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isProgressDragging) return;
        var host = (FrameworkElement)sender;
        SeekProgressFromMouse(host, e);
    }

    private void Progress_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isProgressDragging) return;
        var host = (FrameworkElement)sender;
        host.ReleaseMouseCapture();
        _isProgressDragging = false;
        SeekProgressFromMouse(host, e);
    }

    private void Progress_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isProgressDragging) return;
        var host = (FrameworkElement)sender;
        host.ReleaseMouseCapture();
        _isProgressDragging = false;
    }

    private async void SeekProgressFromMouse(FrameworkElement host, MouseEventArgs e)
    {
        var pos = e.GetPosition(host);
        var ratio = Math.Clamp(pos.X / host.ActualWidth, 0d, 1d);
        var progress = _lastPlaybackProgressSnapshot;
        if (progress is null || progress.DurationMs <= 0d) return;

        var targetMs = ratio * progress.DurationMs;

        InlineProgressFill.Width = InlineProgressHost.ActualWidth * ratio;
        FloatingProgressFill.Width = FloatingProgressTrackHost.ActualWidth * ratio;
        FloatingProgressCurrentText.Text = FormatPlaybackTime(targetMs);

        if (_isProgressDragging) return;

        try
        {
            await _session!.TryChangePlaybackPositionAsync(进度跳转.MillisecondsToPlaybackPositionTicks(targetMs));
            HoldSeekVisualPosition(targetMs);
        }
        catch
        {
        }
    }

    private static string FormatPlaybackTime(double milliseconds)
    {
        var safeMilliseconds = Math.Max(0d, milliseconds);
        var time = TimeSpan.FromMilliseconds(safeMilliseconds);
        return time.TotalHours >= 1d
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    private async void LyricTimer_Tick(object? sender, EventArgs e)
    {
        var playbackProgress = TryGetPlaybackProgressSnapshot();
        _lastPlaybackProgressSnapshot = playbackProgress;
        UpdatePlaybackProgressUi(playbackProgress);
        UpdateSynchronizedLyricFrame(playbackProgress);

        if (_currentLyricLines.Count > 0 || string.IsNullOrWhiteSpace(_currentTrackSignature))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastVisibleLyricProbeUtc < VisibleLyricProbeInterval)
        {
            return;
        }

        _lastVisibleLyricProbeUtc = now;
        var visibleLyric = await TryGetVisiblePlayerLyricAsync();
        if (!string.IsNullOrWhiteSpace(visibleLyric) && !string.Equals(_visiblePlayerLyric, visibleLyric, StringComparison.Ordinal))
        {
            _visiblePlayerLyric = visibleLyric;
            ShowLyricText(visibleLyric, playedWidth: MeasureLyricTextWidth(visibleLyric), resetScroll: true);
        }
    }

    private void RefreshLyricsForTrack(string trackSignature, string? title, string? artist)
    {
        if (string.Equals(_loadedLyricTrackSignature, trackSignature, StringComparison.Ordinal))
        {
            // Same track. If a previous attempt produced no lines (e.g. transient network
            // failure), allow a single in-flight fetch attempt – TryBeginLyricFetch dedupes.
            if (_currentLyricLines.Count == 0 && !string.IsNullOrWhiteSpace(title))
            {
                _ = FetchLyricsForTrackAsync(trackSignature, title, artist);
            }
            return;
        }

        _loadedLyricTrackSignature = trackSignature;
        _lastLyricLineKey = string.Empty;
        _visiblePlayerLyric = string.Empty;
        ResetLyricProgressClock();
        _currentLyricLines = TryLoadLocalLyrics(title, artist);

        if (_currentLyricLines.Count == 0)
        {
            HideLyricLine();
            _ = FetchLyricsForTrackAsync(trackSignature, title, artist);
        }
    }

    private void UpdateSynchronizedLyricFrame(PlaybackProgressSnapshot? playbackProgress)
    {
        if (_currentLyricLines.Count == 0)
        {
            UpdateLyricScrollFrame();
            return;
        }

        var progressMs = playbackProgress?.PositionMs;
        if (!progressMs.HasValue)
        {
            UpdateLyricScrollFrame();
            return;
        }

        var currentLine = FindDisplayLyricLine(_currentLyricLines, progressMs.Value);
        if (currentLine is null)
        {
            HideLyricLine();
            return;
        }

        var lineKey = $"{currentLine.StartMs.ToString(CultureInfo.InvariantCulture)}-{currentLine.Text}";
        var lineChanged = !string.Equals(_lastLyricLineKey, lineKey, StringComparison.Ordinal);

        var playbackStatus = playbackProgress?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        var isPlaying = playbackProgress?.IsPlaying == true;

        System.Diagnostics.Debug.WriteLine($"[Lyric] Status={playbackStatus}, isPlaying={isPlaying}, lineChanged={lineChanged}, lastKey='{_lastLyricLineKey}', newKey='{lineKey}'");

        _lastLyricLineKey = lineKey;

        var playedWidth = CalculatePlayedLyricWidth(currentLine, progressMs.Value);

        // When paused and line hasn't changed, only update played width without touching scroll
        if (!isPlaying && !lineChanged)
        {
            System.Diagnostics.Debug.WriteLine("[Lyric] PAUSED + NO LINE CHANGE - only updating played width");
            LyricPlayedClip.Rect = new Rect(0, 0, Math.Max(0d, playedWidth), LyricLineHost.Height);
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[Lyric] Calling ShowLyricText with resetScroll={lineChanged}");
        ShowLyricText(currentLine.Text, playedWidth, resetScroll: lineChanged);
    }

    private void ShowLyricText(string text, double playedWidth, bool resetScroll)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            HideLyricLine();
            return;
        }

        if (IsCompactNanoDockedLayout())
        {
            HideLyricLine();
            return;
        }

        ArtistText.Visibility = Visibility.Collapsed;
        LyricLineHost.Visibility = Visibility.Visible;
        LyricBaseText.Text = text;
        LyricPlayedText.Text = text;
        LyricTrackCanvas.Width = Math.Max(LyricLineHost.ActualWidth, MeasureLyricTextWidth(text));
        LyricPlayedClip.Rect = new Rect(0, 0, Math.Max(0d, playedWidth), LyricLineHost.Height);

        if (resetScroll)
        {
            _renderedLyricScrollLeft = 0d;
            _targetLyricScrollLeft = 0d;
            LyricTrackTransform.X = 0d;
            _lastLyricFrameUtc = DateTime.UtcNow;
        }

        SyncLyricScrollTarget(Math.Max(0d, playedWidth));
        UpdateLyricScrollFrame();
    }

    private void HideLyricLine()
    {
        LyricLineHost.Visibility = Visibility.Collapsed;
        ArtistText.Visibility = IsCompactNanoDockedLayout() ? Visibility.Collapsed : Visibility.Visible;
        LyricBaseText.Text = string.Empty;
        LyricPlayedText.Text = string.Empty;
        LyricPlayedClip.Rect = new Rect(0, 0, 0, LyricLineHost.Height);
        _lastLyricLineKey = string.Empty;
        _renderedLyricScrollLeft = 0d;
        _targetLyricScrollLeft = 0d;
        LyricTrackTransform.X = 0d;
    }

    private void ClearLyricState()
    {
        _currentLyricLines = Array.Empty<LyricLine>();
        _loadedLyricTrackSignature = string.Empty;
        _visiblePlayerLyric = string.Empty;
        ResetLyricProgressClock();
        HideLyricLine();
        ResetPlaybackProgressUi();
    }

    private void ResetLyricProgressClock()
    {
        _hasLyricProgressAnchor = false;
        _lyricProgressAnchorMs = 0d;
        _lyricProgressAnchorUtc = DateTime.UtcNow;
        _lastObservedTimelinePositionMs = -1d;
        _lastObservedPlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        _resumePlaybackGraceUntilUtc = DateTime.MinValue;
        _virtualLyricClockMs = -1d;
        _virtualLyricClockTickUtc = DateTime.UtcNow;
        _virtualLyricClockLastStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        _lastStableTimelineDurationMs = -1d;
        _lastSeenTimelineUpdateAt = DateTimeOffset.MinValue;
        ClearPendingSeekVisualHold();
    }

    private void HoldSeekVisualPosition(double targetMs)
    {
        _pendingSeekVisualPositionMs = Math.Max(0d, targetMs);
        _pendingSeekVisualHoldUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
    }

    private void ClearPendingSeekVisualHold()
    {
        _pendingSeekVisualPositionMs = -1d;
        _pendingSeekVisualHoldUntilUtc = DateTime.MinValue;
    }

    private double GetAnchoredLyricProgressMs(DateTime now)
    {
        if (!_hasLyricProgressAnchor)
        {
            return Math.Max(0d, _lastObservedTimelinePositionMs);
        }

        var anchoredPositionMs = _lyricProgressAnchorMs;
        if (_lastObservedPlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            anchoredPositionMs += Math.Max(0d, (now - _lyricProgressAnchorUtc).TotalMilliseconds);
        }

        if (_lastObservedTimelinePositionMs >= 0d)
        {
            anchoredPositionMs = Math.Max(anchoredPositionMs, _lastObservedTimelinePositionMs);
        }

        return Math.Max(0d, anchoredPositionMs);
    }

    private double? TryGetCurrentLyricProgressMs()
    {
        return TryGetPlaybackProgressSnapshot()?.PositionMs;
    }

    private PlaybackProgressSnapshot? TryGetPlaybackProgressSnapshot()
    {
        if (_session is null)
        {
            return null;
        }

        try
        {
            var timeline = _session.GetTimelineProperties();
            var playbackStatus = _session.GetPlaybackInfo().PlaybackStatus;
            var now = DateTime.UtcNow;

            // GSMTC's timeline.Position is the player's position at the moment
            // of LastUpdatedTime, not "right now". Many players (Spotify, QQ
            // Music, etc.) only emit a fresh timeline on transport events
            // (play / pause / seek). To get the real playhead position we add
            // the elapsed wall-clock time since LastUpdatedTime when playing.
            var rawObservedPositionMs = GetLivePositionMs(timeline, playbackStatus);

            // A change in LastUpdatedTime means the player just told us its
            // authoritative position — this is exactly when seeks happen, no
            // matter how small the resulting jump is.
            var freshTimelineUpdate = timeline.LastUpdatedTime != _lastSeenTimelineUpdateAt
                && _lastSeenTimelineUpdateAt != DateTimeOffset.MinValue;
            _lastSeenTimelineUpdateAt = timeline.LastUpdatedTime;

            var anchoredProgressMs = GetAnchoredLyricProgressMs(now);
            if (rawObservedPositionMs <= 1500d && anchoredProgressMs > rawObservedPositionMs + 1500d)
            {
                rawObservedPositionMs = anchoredProgressMs;
            }

            // Independent virtual lyric clock. The clock advances locally only
            // while we ourselves believe playback is active, regardless of what
            // glitched timeline values the player happens to report. This is
            // the single source of truth for lyric progress and prevents
            // pause / resume from ever snapping the lyrics back to the start.
            var previousClockStatus = _virtualLyricClockLastStatus;
            if (_virtualLyricClockMs < 0d)
            {
                _virtualLyricClockMs = rawObservedPositionMs;
                _virtualLyricClockTickUtc = now;
                _virtualLyricClockLastStatus = playbackStatus;
            }
            else
            {
                if (_virtualLyricClockLastStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    _virtualLyricClockMs += Math.Max(0d, (now - _virtualLyricClockTickUtc).TotalMilliseconds);
                }

                _virtualLyricClockTickUtc = now;
                _virtualLyricClockLastStatus = playbackStatus;
            }

            // Re-sync the virtual clock against the player only when the
            // mismatch clearly looks like a real user-initiated seek:
            //   * forward seek: raw is well ahead of the clock,
            //   * backward seek: raw is well behind AND not suspiciously close
            //     to zero (which is the typical glitch pattern).
            var diffMs = rawObservedPositionMs - _virtualLyricClockMs;
            if (freshTimelineUpdate && Math.Abs(diffMs) > 250d)
            {
                // Player just emitted a fresh timeline (typically a user seek
                // or a transport state change). Trust it immediately — this is
                // the only reliable way to honour small backward seeks without
                // letting the local clock drag the bar back to the old spot.
                _virtualLyricClockMs = rawObservedPositionMs;
            }
            else if (diffMs > 1500d)
            {
                _virtualLyricClockMs = rawObservedPositionMs;
            }
            else if (diffMs < -1500d
                && rawObservedPositionMs > 1500d
                && playbackStatus is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
            {
                _virtualLyricClockMs = rawObservedPositionMs;
            }
            else if (previousClockStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
                && playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                && rawObservedPositionMs > 1500d
                && Math.Abs(diffMs) < 1500d)
            {
                // Resume edge: if the player's timeline looks healthy (not the
                // glitched near-zero pattern) and is close to our clock, snap
                // the clock to it. This eliminates the small per-pause drift
                // caused by GSMTC event delivery latency, so the lyrics keep
                // up with the music even after many pause / resume cycles.
                _virtualLyricClockMs = rawObservedPositionMs;
            }
            else if (playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                && rawObservedPositionMs > 1500d
                && diffMs > 250d
                && diffMs < 1500d)
            {
                // Steady-state: the player is ahead of us by a small but
                // noticeable amount. Snap forward so we don't fall behind the
                // music. We never snap backward here, to avoid being tricked
                // by the player briefly reporting a stale or slightly behind
                // timeline value.
                _virtualLyricClockMs = rawObservedPositionMs;
            }

            // Keep legacy fields in sync so existing fallbacks still behave.
            _hasLyricProgressAnchor = true;
            _lyricProgressAnchorMs = _virtualLyricClockMs;
            _lyricProgressAnchorUtc = now;
            if (进度跳转显示保持.ShouldUseHeldSeekPosition(
                    _pendingSeekVisualPositionMs,
                    _virtualLyricClockMs,
                    _pendingSeekVisualHoldUntilUtc,
                    now))
            {
                _virtualLyricClockMs = _pendingSeekVisualPositionMs;
            }
            else
            {
                ClearPendingSeekVisualHold();
            }

            _lastObservedTimelinePositionMs = _virtualLyricClockMs;
            _lastObservedPlaybackStatus = playbackStatus;
            var durationMs = GetStableTimelineDurationMs(timeline, _virtualLyricClockMs);

            return new PlaybackProgressSnapshot(_virtualLyricClockMs, durationMs, playbackStatus);
        }
        catch
        {
            return null;
        }
    }

    private static double GetLivePositionMs(
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        var positionMs = Math.Max(0d, timeline.Position.TotalMilliseconds);
        if (status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            return positionMs;
        }

        var lastUpdated = timeline.LastUpdatedTime;
        if (lastUpdated == default)
        {
            return positionMs;
        }

        var elapsedMs = (DateTimeOffset.UtcNow - lastUpdated).TotalMilliseconds;
        if (elapsedMs <= 0d)
        {
            return positionMs;
        }

        // Don't extrapolate forever — if we haven't heard from the player for
        // a long time, the compensation gets unreliable (clock skew, paused
        // session reporting "Playing" briefly, etc.). Cap at 5 seconds.
        elapsedMs = Math.Min(elapsedMs, 5000d);

        var maxMs = Math.Max(timeline.EndTime.TotalMilliseconds, timeline.MaxSeekTime.TotalMilliseconds);
        var live = positionMs + elapsedMs;
        return maxMs > 0d ? Math.Min(live, maxMs) : live;
    }

    private double GetStableTimelineDurationMs(GlobalSystemMediaTransportControlsSessionTimelineProperties timeline, double currentProgressMs)
    {
        var endTimeMs = Math.Max(0d, timeline.EndTime.TotalMilliseconds);
        var maxSeekTimeMs = Math.Max(0d, timeline.MaxSeekTime.TotalMilliseconds);
        var endRangeMs = Math.Max(0d, (timeline.EndTime - timeline.StartTime).TotalMilliseconds);
        var seekRangeMs = Math.Max(0d, (timeline.MaxSeekTime - timeline.MinSeekTime).TotalMilliseconds);
        var rawDurationMs = Math.Max(Math.Max(endTimeMs, maxSeekTimeMs), Math.Max(endRangeMs, seekRangeMs));

        if (rawDurationMs > 1000d)
        {
            _lastStableTimelineDurationMs = Math.Max(rawDurationMs, currentProgressMs);
        }
        else if (_lastStableTimelineDurationMs > 0d)
        {
            _lastStableTimelineDurationMs = Math.Max(_lastStableTimelineDurationMs, currentProgressMs);
        }
        else if (rawDurationMs > 0d)
        {
            _lastStableTimelineDurationMs = Math.Max(rawDurationMs, currentProgressMs);
        }

        return _lastStableTimelineDurationMs > 0d ? _lastStableTimelineDurationMs : rawDurationMs;
    }

    private void SyncLyricScrollTarget(double scrollAnchorX)
    {
        var viewportWidth = LyricLineHost.ActualWidth;
        if (viewportWidth <= 0d)
        {
            viewportWidth = Math.Max(0d, ActualWidth - 174d);
        }

        var totalWidth = Math.Max(LyricTrackCanvas.Width, MeasureLyricTextWidth(LyricBaseText.Text));
        var maxScrollLeft = Math.Max(0d, totalWidth - viewportWidth);
        if (maxScrollLeft <= 0d)
        {
            _targetLyricScrollLeft = 0d;
            return;
        }

        var visibleAnchorX = scrollAnchorX - _renderedLyricScrollLeft;
        var triggerX = viewportWidth * LyricScrollTriggerRatio;
        var anchorX = viewportWidth * LyricScrollAnchorRatio;

        if (scrollAnchorX < _renderedLyricScrollLeft || visibleAnchorX > triggerX)
        {
            _targetLyricScrollLeft = Clamp(scrollAnchorX - anchorX, 0d, maxScrollLeft);
            return;
        }

        _targetLyricScrollLeft = Clamp(_targetLyricScrollLeft, 0d, maxScrollLeft);
    }

    private void UpdateLyricScrollFrame()
    {
        var now = DateTime.UtcNow;
        var deltaSeconds = Math.Max(0.001d, (now - _lastLyricFrameUtc).TotalSeconds);
        _lastLyricFrameUtc = now;

        var diff = _targetLyricScrollLeft - _renderedLyricScrollLeft;
        if (Math.Abs(diff) < 0.5d)
        {
            _renderedLyricScrollLeft = _targetLyricScrollLeft;
        }
        else
        {
            var lerpRatio = 1d - Math.Exp(-LyricScrollLerpSpeed * deltaSeconds);
            _renderedLyricScrollLeft += diff * lerpRatio;
        }

        LyricTrackTransform.X = -_renderedLyricScrollLeft;
    }

    private double CalculatePlayedLyricWidth(LyricLine line, double progressMs)
    {
        var totalWidth = MeasureLyricTextWidth(line.Text);
        if (line.IsLrc)
        {
            if (progressMs <= line.StartMs)
            {
                return 0d;
            }

            if (line.DurationMs <= 0d)
            {
                return totalWidth;
            }

            var lineProgress = Clamp((progressMs - line.StartMs) / line.DurationMs, 0d, 1d);
            return totalWidth * lineProgress;
        }

        var result = 0d;
        foreach (var lyricChar in line.Chars)
        {
            var charWidth = MeasureLyricTextWidth(lyricChar.Text);
            var charStartTime = line.StartMs + lyricChar.StartMs;
            var charEndTime = charStartTime + lyricChar.DurationMs;

            if (progressMs <= charStartTime)
            {
                break;
            }

            if (progressMs >= charEndTime)
            {
                result += charWidth;
                continue;
            }

            var charProgress = lyricChar.DurationMs <= 0d ? 1d : (progressMs - charStartTime) / lyricChar.DurationMs;
            result += charWidth * Clamp(charProgress, 0d, 1d);
            break;
        }

        return Clamp(result, 0d, totalWidth);
    }

    private double MeasureLyricTextWidth(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0d;
        }

        LyricBaseText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (string.Equals(LyricBaseText.Text, text, StringComparison.Ordinal) && LyricBaseText.DesiredSize.Width > 0d)
        {
            return LyricBaseText.DesiredSize.Width;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(LyricBaseText.FontFamily, LyricBaseText.FontStyle, LyricBaseText.FontWeight, LyricBaseText.FontStretch),
            LyricBaseText.FontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static LyricLine? FindDisplayLyricLine(IReadOnlyList<LyricLine> lines, double progressMs)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        if (progressMs <= lines[0].StartMs)
        {
            return lines[0];
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var lineEnd = i + 1 < lines.Count ? lines[i + 1].StartMs : line.StartMs + line.DurationMs;
            if (progressMs >= line.StartMs && progressMs < lineEnd)
            {
                return line;
            }
        }

        return lines[^1];
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Min(Math.Max(value, min), max);
    }

    private IReadOnlyList<LyricLine> TryLoadLocalLyrics(string? title, string? artist)
    {
        foreach (var file in EnumerateCandidateLyricFiles(title, artist))
        {
            try
            {
                var content = File.ReadAllText(file);
                var lines = ParseLyricContent(content);
                if (lines.Count > 0)
                {
                    return lines;
                }
            }
            catch
            {
            }
        }

        return Array.Empty<LyricLine>();
    }

    private IEnumerable<string> EnumerateCandidateLyricFiles(string? title, string? artist)
    {
        var baseNames = BuildLyricBaseNames(title, artist);
        var directories = EnumerateLyricSearchDirectories();

        foreach (var directory in directories)
        {
            foreach (var baseName in baseNames)
            {
                foreach (var extension in new[] { ".lrc", ".krc" })
                {
                    var path = Path.Combine(directory, baseName + extension);
                    if (File.Exists(path))
                    {
                        yield return path;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateLyricSearchDirectories()
    {
        var appDirectory = AppContext.BaseDirectory;
        var currentDirectory = Environment.CurrentDirectory;
        var musicDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var directory in new[]
        {
            appDirectory,
            currentDirectory,
            Path.Combine(appDirectory, "Lyrics"),
            Path.Combine(currentDirectory, "Lyrics"),
            Path.Combine(musicDirectory, "Lyrics"),
            Path.Combine(localAppData, "MusicBar", "Lyrics")
        })
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                yield return directory;
            }
        }
    }

    private static IEnumerable<string> BuildLyricBaseNames(string? title, string? artist)
    {
        var sanitizedTitle = SanitizeFileName(title);
        var sanitizedArtist = SanitizeFileName(artist);
        var names = new List<string>();

        if (!string.IsNullOrWhiteSpace(sanitizedTitle))
        {
            names.Add(sanitizedTitle);
        }

        if (!string.IsNullOrWhiteSpace(sanitizedTitle) && !string.IsNullOrWhiteSpace(sanitizedArtist))
        {
            names.Add($"{sanitizedArtist} - {sanitizedTitle}");
            names.Add($"{sanitizedTitle} - {sanitizedArtist}");
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Trim().Length);
        foreach (var c in value.Trim())
        {
            builder.Append(invalidChars.Contains(c) ? '_' : c);
        }

        return builder.ToString();
    }

    private static IReadOnlyList<LyricLine> ParseLyricContent(string content)
    {
        var krcLines = ParseKrcContent(content);
        if (krcLines.Count > 0)
        {
            return krcLines;
        }

        return ParseLrcContent(content);
    }

    private static IReadOnlyList<LyricLine> ParseKrcContent(string content)
    {
        var result = new List<LyricLine>();
        foreach (var rawLine in content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var lineMatch = KrcLineRegex.Match(rawLine.Trim());
            if (!lineMatch.Success)
            {
                continue;
            }

            var startMs = ParseDoubleInvariant(lineMatch.Groups["start"].Value);
            var durationMs = ParseDoubleInvariant(lineMatch.Groups["duration"].Value);
            var contentText = lineMatch.Groups["content"].Value;
            var chars = new List<LyricChar>();

            foreach (Match wordMatch in KrcWordRegex.Matches(contentText))
            {
                var wordStartMs = ParseDoubleInvariant(wordMatch.Groups["start"].Value);
                var wordDurationMs = ParseDoubleInvariant(wordMatch.Groups["duration"].Value);
                var wordText = wordMatch.Groups["text"].Value;
                var wordChars = wordText.EnumerateRunes().Select(r => r.ToString()).ToList();
                if (wordChars.Count == 0)
                {
                    continue;
                }

                var charDuration = wordDurationMs / wordChars.Count;
                var charStart = wordStartMs;
                foreach (var wordChar in wordChars)
                {
                    chars.Add(new LyricChar(charStart, charDuration, wordChar));
                    charStart += charDuration;
                }
            }

            chars = TrimWhitespaceLyricChars(chars);
            if (startMs >= 0d && durationMs > 0d && chars.Count > 0)
            {
                result.Add(new LyricLine(startMs, durationMs, IsLrc: false, chars));
            }
        }

        return result.OrderBy(line => line.StartMs).ToList();
    }

    private static IReadOnlyList<LyricLine> ParseLrcContent(string content)
    {
        var entries = new List<(double StartMs, string Text)>();
        foreach (var rawLine in content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var matches = LrcTimestampRegex.Matches(line);
            if (matches.Count == 0)
            {
                continue;
            }

            var text = LrcTimestampRegex.Replace(line, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (Match match in matches)
            {
                var startMs = ParseLrcTime(match.Groups["time"].Value);
                if (startMs >= 0d)
                {
                    entries.Add((startMs, text));
                }
            }
        }

        var ordered = entries.OrderBy(entry => entry.StartMs).ToList();
        var result = new List<LyricLine>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            var endMs = i + 1 < ordered.Count ? ordered[i + 1].StartMs : entry.StartMs + 10000d;
            var chars = entry.Text.EnumerateRunes()
                .Select(r => new LyricChar(-1d, -1d, r.ToString()))
                .ToList();
            if (chars.Count > 0)
            {
                result.Add(new LyricLine(entry.StartMs, Math.Max(100d, endMs - entry.StartMs), IsLrc: true, chars));
            }
        }

        return result;
    }

    private static List<LyricChar> TrimWhitespaceLyricChars(List<LyricChar> chars)
    {
        var start = 0;
        var end = chars.Count - 1;

        while (start <= end && string.IsNullOrWhiteSpace(chars[start].Text))
        {
            start++;
        }

        while (end >= start && string.IsNullOrWhiteSpace(chars[end].Text))
        {
            end--;
        }

        return start == 0 && end == chars.Count - 1
            ? chars
            : chars.GetRange(start, Math.Max(0, end - start + 1));
    }

    private static double ParseLrcTime(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return -1d;
        }

        if (!double.TryParse(parts[1].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return -1d;
        }

        return (minutes * 60d + seconds) * 1000d;
    }

    private static double ParseDoubleInvariant(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : -1d;
    }

    private static string NormalizeMediaArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return string.Empty;
        }

        var normalized = artist.Trim();
        if (IsChromiumWindowString(normalized))
        {
            return string.Empty;
        }

        return normalized;
    }

    private static string NormalizeMediaTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var normalized = title.Trim();
        if (IsChromiumWindowString(normalized))
        {
            return string.Empty;
        }

        return normalized;
    }

    private static bool IsChromiumWindowString(string value)
    {
        return value.Equals("Chrome Legacy Window", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Legacy Window", StringComparison.OrdinalIgnoreCase)
            || value.Contains("WidgetWin", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase)
            || value.Equals("YouTube", StringComparison.OrdinalIgnoreCase);
    }

    private async Task FetchLyricsForTrackAsync(string trackSignature, string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title) || !TryBeginLyricFetch(trackSignature))
        {
            return;
        }

        try
        {
            var lyricHint = string.IsNullOrWhiteSpace(artist) ? await TryGetVisiblePlayerLyricAsync() : string.Empty;
            var lyrics = await TryDownloadLyricsAsync(title, artist, lyricHint);
            if (string.IsNullOrWhiteSpace(lyrics) || !string.Equals(_loadedLyricTrackSignature, trackSignature, StringComparison.Ordinal))
            {
                return;
            }

            SaveLyricsToCache(title, artist, lyrics);
            var parsedLines = ParseLyricContent(lyrics);
            if (parsedLines.Count == 0)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (!string.Equals(_loadedLyricTrackSignature, trackSignature, StringComparison.Ordinal))
                {
                    return;
                }

                _currentLyricLines = parsedLines;
                _lastLyricLineKey = string.Empty;
                _visiblePlayerLyric = string.Empty;
            });
        }
        catch
        {
        }
        finally
        {
            EndLyricFetch(trackSignature);
        }
    }

    private bool TryBeginLyricFetch(string trackSignature)
    {
        lock (_lyricFetchesInProgress)
        {
            return _lyricFetchesInProgress.Add(trackSignature);
        }
    }

    private void EndLyricFetch(string trackSignature)
    {
        lock (_lyricFetchesInProgress)
        {
            _lyricFetchesInProgress.Remove(trackSignature);
        }
    }

    private static async Task<string> TryDownloadLyricsAsync(string title, string? artist, string? lyricHint)
    {
        var lrclibLyrics = await TryDownloadLyricsFromLrclibAsync(title, artist, lyricHint);
        if (!string.IsNullOrWhiteSpace(lrclibLyrics))
        {
            return lrclibLyrics;
        }

        var songId = await TryFindNeteaseSongIdAsync(title, artist);
        if (songId is null)
        {
            return string.Empty;
        }

        var lyricUrl = $"https://music.163.com/api/song/lyric?id={songId.Value.ToString(CultureInfo.InvariantCulture)}&lv=1&kv=1&tv=-1";
        using var lyricResponse = await LyricHttpClient.GetAsync(lyricUrl);
        if (!lyricResponse.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        await using var lyricStream = await lyricResponse.Content.ReadAsStreamAsync();
        using var lyricDocument = await JsonDocument.ParseAsync(lyricStream);
        if (!lyricDocument.RootElement.TryGetProperty("lrc", out var lrcElement)
            || !lrcElement.TryGetProperty("lyric", out var lyricElement))
        {
            return string.Empty;
        }

        var lyric = lyricElement.GetString() ?? string.Empty;
        return ParseLrcContent(lyric).Count > 0 ? lyric : string.Empty;
    }

    private static async Task<string> TryDownloadLyricsFromLrclibAsync(string title, string? artist, string? lyricHint)
    {
        foreach (var query in BuildLyricSearchQueries(title, artist))
        {
            var requestUrl = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}";
            using var response = await LyricHttpClient.GetAsync(requestUrl);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var bestLyrics = string.Empty;
            var bestScore = double.MinValue;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("instrumental", out var instrumentalElement)
                    && instrumentalElement.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                var candidateTitle = item.TryGetProperty("trackName", out var trackNameElement) ? trackNameElement.GetString() ?? string.Empty : string.Empty;
                var candidateArtist = item.TryGetProperty("artistName", out var artistNameElement) ? artistNameElement.GetString() ?? string.Empty : string.Empty;
                var syncedLyrics = item.TryGetProperty("syncedLyrics", out var syncedLyricsElement) ? syncedLyricsElement.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(syncedLyrics) || ParseLrcContent(syncedLyrics).Count == 0)
                {
                    continue;
                }

                var score = ScoreLyricSearchCandidate(title, artist, candidateTitle, candidateArtist, lyricHint, syncedLyrics);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestLyrics = syncedLyrics;
                }
            }

            if (!string.IsNullOrWhiteSpace(bestLyrics))
            {
                return bestLyrics;
            }
        }

        return string.Empty;
    }

    private static async Task<long?> TryFindNeteaseSongIdAsync(string title, string? artist)
    {
        foreach (var query in BuildLyricSearchQueries(title, artist))
        {
            var songId = await TryFindNeteaseSongIdByQueryAsync(query, title, artist);
            if (songId is not null)
            {
                return songId;
            }
        }

        return null;
    }

    private static async Task<long?> TryFindNeteaseSongIdByQueryAsync(string query, string title, string? artist)
    {
        var searchUrl = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(query)}&type=1&limit=8&offset=0";
        using var response = await LyricHttpClient.GetAsync(searchUrl);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        if (!document.RootElement.TryGetProperty("result", out var resultElement)
            || !resultElement.TryGetProperty("songs", out var songsElement)
            || songsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        long? fallbackId = null;
        foreach (var songElement in songsElement.EnumerateArray())
        {
            if (!songElement.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
            {
                continue;
            }

            fallbackId ??= id;
            var songName = songElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            var artistNames = ReadNeteaseArtistNames(songElement);
            if (IsLikelySameSong(title, artist, songName, artistNames))
            {
                return id;
            }
        }

        return fallbackId;
    }

    private static IEnumerable<string> BuildLyricSearchQueries(string title, string? artist)
    {
        var cleanTitle = CleanLyricSearchText(title);
        var cleanArtist = CleanLyricSearchText(artist);
        var queries = new List<string>();

        if (!string.IsNullOrWhiteSpace(cleanTitle) && !string.IsNullOrWhiteSpace(cleanArtist))
        {
            queries.Add($"{cleanTitle} {cleanArtist}");
        }

        if (!string.IsNullOrWhiteSpace(cleanTitle))
        {
            queries.Add(cleanTitle);
        }

        if (!string.Equals(cleanTitle, title, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(title))
        {
            queries.Add(title);
        }

        return queries.Where(query => !string.IsNullOrWhiteSpace(query)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string CleanLyricSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return BracketedTitlePartRegex.Replace(value, string.Empty)
            .Replace("feat.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("ft.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string ReadNeteaseArtistNames(JsonElement songElement)
    {
        if (!songElement.TryGetProperty("artists", out var artistsElement) || artistsElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(" ", artistsElement.EnumerateArray()
            .Select(artistElement => artistElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    private static bool IsLikelySameSong(string title, string? artist, string candidateTitle, string candidateArtists)
    {
        return ScoreLyricSearchCandidate(title, artist, candidateTitle, candidateArtists, lyricHint: null, syncedLyrics: null) >= 60d;
    }

    private static double ScoreLyricSearchCandidate(string title, string? artist, string candidateTitle, string candidateArtists, string? lyricHint, string? syncedLyrics)
    {
        var normalizedTitle = NormalizeLyricMatchText(CleanLyricSearchText(title));
        var normalizedCandidateTitle = NormalizeLyricMatchText(CleanLyricSearchText(candidateTitle));
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedCandidateTitle))
        {
            return 0d;
        }

        var titleMatches = normalizedTitle.Contains(normalizedCandidateTitle, StringComparison.Ordinal)
            || normalizedCandidateTitle.Contains(normalizedTitle, StringComparison.Ordinal);
        if (!titleMatches)
        {
            return 0d;
        }

        var score = normalizedTitle.Equals(normalizedCandidateTitle, StringComparison.Ordinal) ? 70d : 45d;
        var normalizedArtist = NormalizeLyricMatchText(artist);
        var normalizedCandidateArtists = NormalizeLyricMatchText(candidateArtists);
        if (!string.IsNullOrWhiteSpace(normalizedArtist)
            && !string.IsNullOrWhiteSpace(normalizedCandidateArtists)
            && (normalizedCandidateArtists.Contains(normalizedArtist, StringComparison.Ordinal)
                || normalizedArtist.Contains(normalizedCandidateArtists, StringComparison.Ordinal)))
        {
            score += 35d;
        }

        var normalizedLyricHint = NormalizeLyricMatchText(lyricHint);
        var normalizedSyncedLyrics = NormalizeLyricMatchText(syncedLyrics);
        if (!string.IsNullOrWhiteSpace(normalizedLyricHint) && !string.IsNullOrWhiteSpace(normalizedSyncedLyrics))
        {
            if (normalizedSyncedLyrics.Contains(normalizedLyricHint, StringComparison.Ordinal))
            {
                score += 55d;
            }
            else if (normalizedLyricHint.Length >= 8)
            {
                score -= 20d;
            }
        }

        return score;
    }

    private static IntPtr FindVisibleWindowByClassNames(params string[] classNames)
    {
        foreach (var className in classNames)
        {
            if (string.IsNullOrWhiteSpace(className))
            {
                continue;
            }

            var hwnd = FindWindow(className, null);
            if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd))
            {
                return hwnd;
            }
        }

        return IntPtr.Zero;
    }

    private static string NormalizeLyricMatchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || IsCjk(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static void SaveLyricsToCache(string title, string? artist, string lyrics)
    {
        var cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBar", "Lyrics");
        Directory.CreateDirectory(cacheDirectory);

        var fileName = BuildLyricBaseNames(title, artist).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        File.WriteAllText(Path.Combine(cacheDirectory, fileName + ".lrc"), lyrics, Encoding.UTF8);
    }

    private static HttpClient CreateLyricHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 MusicBar/1.0");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return client;
    }

    private async Task<string> TryGetVisiblePlayerLyricAsync()
    {
        var target = ResolveJumpTargetPlayer();
        if (!target.HasValue || !ShouldProbeVisiblePlayerLyrics(target.Value))
        {
            return string.Empty;
        }

        var targetWindow = ResolveVisibleLyricTargetWindow(target.Value);
        if (targetWindow == IntPtr.Zero)
        {
            return string.Empty;
        }

        var currentTitle = SongTitleText.Text;
        var currentArtist = ArtistText.Text;
        var (success, lyric) = await TryRunUiAutomationAsync(() =>
        {
            var root = AutomationElement.FromHandle(targetWindow);
            var best = string.Empty;
            var bestScore = double.MinValue;
            var windowRect = root.Current.BoundingRectangle;
            foreach (var candidate in EnumerateAutomationTreeLimited(root, maxDepth: 10, maxNodes: 2200))
            {
                var currentName = candidate.Current.Name ?? string.Empty;
                if (!LooksLikeLyricLine(currentName, currentTitle, currentArtist))
                {
                    continue;
                }

                var score = ScoreVisibleLyricCandidate(candidate, currentName, windowRect);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = currentName.Trim();
                }
            }

            return best;
        }, timeoutMs: 360, defaultResult: string.Empty);

        return success ? lyric : string.Empty;
    }

    private static bool ShouldProbeVisiblePlayerLyrics(PlayerControlTarget target)
    {
        return target != PlayerControlTarget.KuGouMusic;
    }

    private IntPtr ResolveVisibleLyricTargetWindow(PlayerControlTarget target)
    {
        if (target == PlayerControlTarget.Auto)
        {
            return IntPtr.Zero;
        }

        if (target == PlayerControlTarget.KuGouMusic)
        {
            return FindPreferredKuGouWindow();
        }

        return PlayerWindowProcessNames.TryGetValue(target, out var processNames)
            ? FindPreferredWindow(processNames)
            : IntPtr.Zero;
    }

    private static bool LooksLikeLyricLine(string text, string currentTitle, string currentArtist)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim();
        if (normalized.Length < 2 || normalized.Length > 80)
        {
            return false;
        }

        if (normalized.Contains("播放", StringComparison.Ordinal)
            || normalized.Contains("暂停", StringComparison.Ordinal)
            || normalized.Contains("上一首", StringComparison.Ordinal)
            || normalized.Contains("下一首", StringComparison.Ordinal)
            || normalized.Contains("收藏", StringComparison.Ordinal)
            || normalized.Contains("喜欢", StringComparison.Ordinal))
        {
            return false;
        }

        var title = currentTitle.Trim();
        var artist = currentArtist.Trim();
        if ((!string.IsNullOrWhiteSpace(title) && normalized.Contains(title, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(artist) && normalized.Contains(artist, StringComparison.OrdinalIgnoreCase))
            || normalized.Contains(" - ", StringComparison.Ordinal)
            || normalized.Contains(" — ", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Any(c => char.IsLetterOrDigit(c) || IsCjk(c));
    }

    private static double ScoreVisibleLyricCandidate(AutomationElement element, string text, Rect windowRect)
    {
        var score = 0d;
        var rect = element.Current.BoundingRectangle;
        if (!rect.IsEmpty)
        {
            score += Math.Min(120d, rect.Width / 4d);
            score += Math.Min(80d, rect.Height * 2d);
            var targetTop = windowRect.IsEmpty ? 320d : windowRect.Top + windowRect.Height * 0.42d;
            score -= Math.Abs(rect.Top - targetTop) / 5d;
        }

        if (text.Any(IsCjk))
        {
            score += 20d;
        }

        if (text.Any(char.IsLetter))
        {
            score += 12d;
        }

        if (text.Length >= 8 && text.Length <= 45)
        {
            score += 25d;
        }

        return score;
    }

    private static bool IsCjk(char c)
    {
        return c >= '\u4e00' && c <= '\u9fff';
    }
}

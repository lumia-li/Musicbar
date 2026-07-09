using System;
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
using Windows.Media;
using Windows.Media.Control;

namespace MusicBar;

public partial class MainWindow : Window
{
    private void SetPlayPauseIcon(bool isPlaying)
    {
        PlayIconViewbox.Visibility = isPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseIconViewbox.Visibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
        SetMainSpectrumPlaybackState(isPlaying);
    }

    private void UpdateActivePlayerLogo()
    {
        if (_selectedPlayerTarget != PlayerControlTarget.Auto)
        {
            SetActivePlayerLogo(null);
            return;
        }

        SetActivePlayerLogo(ResolveActivePlayerTarget());
    }

    private void SetActivePlayerLogo(PlayerControlTarget? target)
    {
        if (!target.HasValue || target.Value == PlayerControlTarget.Auto)
        {
            ActivePlayerLogoImage.Source = null;
            ActivePlayerLogoButton.ToolTip = null;
            ApplyDockedVisualState();
            return;
        }

        // Hide logo for Douyin (identified as SodaMusic but without SodaMusic process running)
        if (target.Value == PlayerControlTarget.SodaMusic
            && _session is not null)
        {
            var sourceAppId = TryGetSourceAppId(_session).ToLowerInvariant();
            if (sourceAppId.Contains("douyin") || sourceAppId.Contains("snssdk"))
            {
                ActivePlayerLogoImage.Source = null;
                ActivePlayerLogoButton.ToolTip = null;
                ApplyDockedVisualState();
                return;
            }
        }

        ActivePlayerLogoImage.Source = GetPlayerLogo(target.Value);
        ActivePlayerLogoButton.ToolTip = GetPlayerTargetDisplayName(target.Value);
        ApplyDockedVisualState();
    }

    private PlayerControlTarget? ResolveActivePlayerTarget()
    {
        if (_session is not null)
        {
            var sourceAppId = TryGetSourceAppId(_session).ToLowerInvariant();
            var resolvedTarget = ResolvePlayerTargetFromSourceAppId(sourceAppId);
            if (resolvedTarget.HasValue)
            {
                if (resolvedTarget.Value == PlayerControlTarget.SodaMusic
                    && !IsAnyProcessRunning("SodaMusic"))
                {
                    return null;
                }

                return resolvedTarget.Value;
            }

            if (!IsKnownNonSodaSourceAppId(sourceAppId) && IsAnyProcessRunning("SodaMusic"))
            {
                return PlayerControlTarget.SodaMusic;
            }

            return null;
        }

        if ((IsCurrentTrackSignatureFromSource("KuGou") || _selectedPlayerTarget == PlayerControlTarget.KuGouMusic)
            && IsAnyProcessRunning(KuGouProcessNames))
        {
            return PlayerControlTarget.KuGouMusic;
        }

        return null;
    }

    private static PlayerControlTarget? ResolvePlayerTargetFromSourceAppId(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return null;
        }

        if (sourceAppId.Contains("qqmusic"))
        {
            return PlayerControlTarget.QQMusic;
        }

        if (sourceAppId.Contains("cloudmusic")
            || sourceAppId.Contains("netease")
            || sourceAppId.Contains("music.163"))
        {
            return PlayerControlTarget.NeteaseCloudMusic;
        }

        if (sourceAppId.Contains("spotify"))
        {
            return PlayerControlTarget.Spotify;
        }

        if (IsMoeKoeMusicSourceAppId(sourceAppId))
        {
            return PlayerControlTarget.MoeKoeMusic;
        }

        if (IsYouTubeMusicSourceAppId(sourceAppId))
        {
            return PlayerControlTarget.YouTubeMusic;
        }

        if (IsKuGouSourceAppId(sourceAppId))
        {
            return PlayerControlTarget.KuGouMusic;
        }

        if (IsSodaSourceAppId(sourceAppId))
        {
            return PlayerControlTarget.SodaMusic;
        }

        return null;
    }

    private BitmapImage GetPlayerLogo(PlayerControlTarget target)
    {
        if (_playerLogoCache.TryGetValue(target, out var cached))
        {
            return cached;
        }

        var fileName = target switch
        {
            PlayerControlTarget.QQMusic => "qq.png",
            PlayerControlTarget.NeteaseCloudMusic => "netease.png",
            PlayerControlTarget.Spotify => "spotify.png",
            PlayerControlTarget.YouTubeMusic => "youtube.png",
            PlayerControlTarget.KuGouMusic => "kugou.png",
            PlayerControlTarget.SodaMusic => "soda.png",
            PlayerControlTarget.MoeKoeMusic => "moekoe.png",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(fileName))
        {
            // No logo defined for this player target 鈥?return null instead of throwing.
            return null!;
        }

        BitmapImage image;
        try
        {
            image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri($"pack://application:,,,/Assets/Logos/{fileName}", UriKind.Absolute);
            image.EndInit();
            image.Freeze();
        }
        catch
        {
            // Logo file not found or failed to load (e.g. moekoe.png not yet added).
            return null!;
        }

        _playerLogoCache[target] = image;
        return image;
    }

    private void AlbumArtHitArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _isAlbumArtPointerDown = true;
        e.Handled = true;
    }

    private async void AlbumArtHitArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasPointerDown = _isAlbumArtPointerDown;
        _isAlbumArtPointerDown = false;

        if (!wasPointerDown || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        e.Handled = true;
        await TryJumpToCurrentPlayerAsync();
    }

    private void AlbumArtHitArea_MouseEnter(object sender, MouseEventArgs e)
    {
        AlbumArtHoverBadge.Visibility = Visibility.Visible;
        AlbumArtHoverShade.Visibility = Visibility.Visible;

        AlbumArtHoverShade.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = AlbumArtHoverShade.Opacity,
            To = 1d,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });

        AlbumArtHoverBadge.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = AlbumArtHoverBadge.Opacity,
            To = 1d,
            Duration = TimeSpan.FromMilliseconds(90),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });

        AlbumArtHoverBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            From = AlbumArtHoverBadgeScale.ScaleX,
            To = 1d,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        AlbumArtHoverBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            From = AlbumArtHoverBadgeScale.ScaleY,
            To = 1d,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void AlbumArtHitArea_MouseLeave(object sender, MouseEventArgs e)
    {
        var shadeFadeOut = new DoubleAnimation
        {
            From = AlbumArtHoverShade.Opacity,
            To = 0d,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        shadeFadeOut.Completed += (_, _) =>
        {
            if (!AlbumArtHitArea.IsMouseOver)
            {
                AlbumArtHoverShade.Visibility = Visibility.Collapsed;
            }
        };
        AlbumArtHoverShade.BeginAnimation(UIElement.OpacityProperty, shadeFadeOut);

        var fadeOut = new DoubleAnimation
        {
            From = AlbumArtHoverBadge.Opacity,
            To = 0d,
            Duration = TimeSpan.FromMilliseconds(110),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (!AlbumArtHitArea.IsMouseOver)
            {
                AlbumArtHoverBadge.Visibility = Visibility.Collapsed;
            }
        };
        AlbumArtHoverBadge.BeginAnimation(UIElement.OpacityProperty, fadeOut);

        AlbumArtHoverBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            From = AlbumArtHoverBadgeScale.ScaleX,
            To = 0.76d,
            Duration = TimeSpan.FromMilliseconds(110),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        });
        AlbumArtHoverBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            From = AlbumArtHoverBadgeScale.ScaleY,
            To = 0.76d,
            Duration = TimeSpan.FromMilliseconds(110),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        });
    }

    private async Task<bool> TryJumpToCurrentPlayerAsync()
    {
        var target = ResolveJumpTargetPlayer();
        if (!target.HasValue || target.Value == PlayerControlTarget.Auto)
        {
            return false;
        }

        IntPtr targetWindow;
        if (target.Value == PlayerControlTarget.KuGouMusic)
        {
            targetWindow = FindPreferredKuGouWindow();
        }
        else if (!PlayerWindowProcessNames.TryGetValue(target.Value, out var processNames))
        {
            return false;
        }
        else
        {
            targetWindow = FindPreferredWindow(processNames);
        }

        if (targetWindow == IntPtr.Zero)
        {
            return false;
        }

        return await TryActivateTargetWindowAsync(targetWindow);
    }

    private PlayerControlTarget? ResolveJumpTargetPlayer()
    {
        if (_session is not null)
        {
            var sourceAppId = TryGetSourceAppId(_session).ToLowerInvariant();
            var resolvedTarget = ResolvePlayerTargetFromSourceAppId(sourceAppId);
            if (resolvedTarget.HasValue)
            {
                return resolvedTarget.Value;
            }

            if (!IsKnownNonSodaSourceAppId(sourceAppId) && IsAnyProcessRunning("SodaMusic", "Luna"))
            {
                return PlayerControlTarget.SodaMusic;
            }
        }

        if (_selectedPlayerTarget != PlayerControlTarget.Auto)
        {
            return _selectedPlayerTarget;
        }

        if (ShouldUseKuGouAutomationFallback())
        {
            return PlayerControlTarget.KuGouMusic;
        }

        return ResolveActivePlayerTarget();
    }

    private async void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlayerTarget == PlayerControlTarget.KuGouMusic && IsAnyProcessRunning(KuGouProcessNames))
        {
            await TryInvokeKuGouTransportControlAsync("上一首");
            await RefreshFallbackStateAfterControlAsync();
            return;
        }

        if (_session is not null)
        {
            PinCurrentSessionForAutoMode();
            try
            {
                await _session.TrySkipPreviousAsync();
            }
            catch
            {
                await RefreshCurrentSessionAsync();
            }
            return;
        }

        if (ShouldUseKuGouAutomationFallback())
        {
            await TryInvokeKuGouTransportControlAsync("上一首");
            await RefreshFallbackStateAfterControlAsync();
        }
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlayerTarget == PlayerControlTarget.KuGouMusic && IsAnyProcessRunning(KuGouProcessNames))
        {
            var kuGouSnapshot = await TryGetKuGouPlaybackSnapshotAsync();
            var kuGouPrimary = kuGouSnapshot?.IsPlaying == true ? "鏆傚仠" : "鎾斁";
            await TryInvokeKuGouTransportControlAsync(kuGouPrimary, "鎾斁", "鏆傚仠");
            await RefreshFallbackStateAfterControlAsync();
            return;
        }

        if (_session is not null)
        {
            PinCurrentSessionForAutoMode();
            _transportControlSessionGraceUntilUtc = DateTime.UtcNow + TransportControlSessionGraceInterval;
            try
            {
                var status = _session.GetPlaybackInfo().PlaybackStatus;
                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    await _session.TryPauseAsync();
                }
                else
                {
                    await _session.TryPlayAsync();
                }
            }
            catch
            {
                try
                {
                    await _session.TryTogglePlayPauseAsync();
                }
                catch
                {
                    await RefreshCurrentSessionAsync();
                }
            }

            return;
        }

        if (ShouldUseKuGouAutomationFallback())
        {
            var snapshot = await TryGetKuGouPlaybackSnapshotAsync();
            var primaryAction = snapshot?.IsPlaying == true ? "鏆傚仠" : "鎾斁";
            await TryInvokeKuGouTransportControlAsync(primaryAction, "鎾斁", "鏆傚仠");
            await RefreshFallbackStateAfterControlAsync();
        }
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlayerTarget == PlayerControlTarget.KuGouMusic && IsAnyProcessRunning(KuGouProcessNames))
        {
            await TryInvokeKuGouTransportControlAsync("下一首");
            await RefreshFallbackStateAfterControlAsync();
            return;
        }

        if (_session is not null)
        {
            PinCurrentSessionForAutoMode();
            try
            {
                await _session.TrySkipNextAsync();
            }
            catch
            {
                await RefreshCurrentSessionAsync();
            }
            return;
        }

        if (ShouldUseKuGouAutomationFallback())
        {
            await TryInvokeKuGouTransportControlAsync("下一首");
            await RefreshFallbackStateAfterControlAsync();
        }
    }

    private async void DefaultPlaybackModeButton_Click(object sender, RoutedEventArgs e)
    {
        var nextMode = DefaultPlaybackModeResolver.GetNext(_defaultPlaybackMode);
        var applied = await TryApplyDefaultPlaybackModeAsync(nextMode);
        if (applied)
        {
            _defaultPlaybackMode = nextMode;
            UpdateDefaultPlaybackModeVisual();
            return;
        }

        ShowDefaultPlaybackModeUnavailable(nextMode);
    }

    private async Task<bool> TryApplyDefaultPlaybackModeAsync(DefaultPlaybackMode mode)
    {
        if (_selectedPlayerTarget == PlayerControlTarget.KuGouMusic && IsAnyProcessRunning(KuGouProcessNames))
        {
            return await TryApplyKuGouPlaybackModeAsync(mode);
        }

        var fallbackTarget = ResolvePlaybackModeFallbackTarget();
        if (await TryApplyPlaybackModeFallbackAsync(fallbackTarget))
        {
            return true;
        }

        if (_session is null)
        {
            if (ShouldUseKuGouAutomationFallback())
            {
                return await TryApplyKuGouPlaybackModeAsync(mode);
            }

            return false;
        }

        PinCurrentSessionForAutoMode();

        try
        {
            var playbackInfo = _session.GetPlaybackInfo();
            var controls = playbackInfo.Controls;
            var command = DefaultPlaybackModeResolver.ToSessionCommand(mode);

            if (!controls.IsRepeatEnabled && !controls.IsShuffleEnabled)
            {
                return await TryApplyPlaybackModeFallbackAsync(fallbackTarget);
            }

            if (mode is DefaultPlaybackMode.Loop && !controls.IsRepeatEnabled)
            {
                return await TryApplyPlaybackModeFallbackAsync(fallbackTarget);
            }

            if (mode is DefaultPlaybackMode.Shuffle && !controls.IsShuffleEnabled)
            {
                return await TryApplyPlaybackModeFallbackAsync(fallbackTarget);
            }

            var repeatApplied = true;
            if (controls.IsRepeatEnabled)
            {
                repeatApplied = await _session.TryChangeAutoRepeatModeAsync(
                    command.RepeatMode == DefaultSessionRepeatMode.List
                        ? MediaPlaybackAutoRepeatMode.List
                        : MediaPlaybackAutoRepeatMode.None);
            }

            var shuffleApplied = true;
            if (controls.IsShuffleEnabled)
            {
                shuffleApplied = await _session.TryChangeShuffleActiveAsync(command.ShuffleActive);
            }

            return mode switch
            {
                DefaultPlaybackMode.Loop => repeatApplied,
                DefaultPlaybackMode.Shuffle => shuffleApplied,
                _ => repeatApplied && shuffleApplied
            };
        }
        catch
        {
            await RefreshCurrentSessionAsync();
            return await TryApplyPlaybackModeFallbackAsync(fallbackTarget);
        }
    }

    private PlayerControlTarget? ResolvePlaybackModeFallbackTarget()
    {
        if (_selectedPlayerTarget != PlayerControlTarget.Auto)
        {
            return _selectedPlayerTarget;
        }

        return ResolveActivePlayerTarget();
    }

    private async Task<bool> TryApplyPlaybackModeFallbackAsync(PlayerControlTarget? target)
    {
        return target switch
        {
            PlayerControlTarget.MoeKoeMusic => await TryApplyMoeKoePlaybackModeAsync(),
            _ => false
        };
    }

    private async Task<bool> TryApplyMoeKoePlaybackModeAsync()
    {
        return PlayerWindowProcessNames.TryGetValue(PlayerControlTarget.MoeKoeMusic, out var processNames)
               && IsAnyProcessRunning(processNames)
               && await TryApplyShortcutPlaybackModeAsync(DefaultPlaybackFallbackPlayer.MoeKoeMusic);
    }

    private async Task<bool> TryApplyShortcutPlaybackModeAsync(DefaultPlaybackFallbackPlayer player)
    {
        var shortcut = DefaultPlaybackModeResolver.GetShortcutFallback(player);
        if (!shortcut.HasValue)
        {
            return false;
        }

        SendChord(new FavoriteKeyChord(
            Ctrl: shortcut.Value.Ctrl,
            Alt: shortcut.Value.Alt,
            Shift: shortcut.Value.Shift,
            Key: shortcut.Value.Key));
        await Task.Delay(80);
        return true;
    }

    private async Task<bool> TryApplyKuGouPlaybackModeAsync(DefaultPlaybackMode mode)
    {
        var names = mode switch
        {
            DefaultPlaybackMode.Loop => new[] { "循环", "列表循环", "顺序循环" },
            DefaultPlaybackMode.Shuffle => new[] { "随机", "随机播放" },
            _ => new[] { "顺序", "顺序播放", "列表播放" }
        };

        var invoked = await TryInvokeKuGouPlaybackModeControlAsync(names);
        if (invoked)
        {
            await RefreshFallbackStateAfterControlAsync();
        }

        return invoked;
    }

    private void UpdateDefaultPlaybackModeVisual()
    {
        if (DefaultPlaybackModeIcon is not null)
        {
            DefaultPlaybackModeIcon.Source = LoadDefaultPlaybackModeIcon(_defaultPlaybackMode);
        }

        if (DefaultPlaybackModeButton is not null)
        {
            DefaultPlaybackModeButton.ToolTip = DefaultPlaybackModeResolver.GetDisplayName(_defaultPlaybackMode);
        }
    }

    private void ShowDefaultPlaybackModeUnavailable(DefaultPlaybackMode mode)
    {
        ArtistText.Text = $"{DefaultPlaybackModeResolver.GetDisplayName(mode)} 不受当前播放器支持";
    }

    private static BitmapImage? LoadDefaultPlaybackModeIcon(DefaultPlaybackMode mode)
    {
        var assetName = DefaultPlaybackModeResolver.GetIconAssetName(mode);
        var uri = new Uri($"pack://application:,,,/Assets/Nte/{assetName}", UriKind.Absolute);
        if (Application.GetResourceStream(uri) is null)
        {
            return null;
        }

        try
        {
            return new BitmapImage(uri);
        }
        catch
        {
            return null;
        }
    }

    private async void LikeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLikeActionPending)
        {
            return;
        }

        _isLikeActionPending = true;
        LikeButton.IsEnabled = false;

        try
        {
            if (_session is null)
            {
                if (ShouldUseKuGouAutomationFallback())
                {
                    var kuGouTargetLike = !_liked;
                    var kuGouInvoked = await TryInvokeFavoriteActionAsync("kugou", kuGouTargetLike);
                    if (!kuGouInvoked)
                    {
                        ShowLikeUnavailableState();
                        return;
                    }

                    _liked = kuGouTargetLike;
                    if (!string.IsNullOrWhiteSpace(_currentTrackSignature))
                    {
                        _trackLikeState[_currentTrackSignature] = _liked;
                    }
                    ApplyLikeState();
                    return;
                }

                ShowLikeUnavailableState();
                return;
            }

            var sourceAppId = TryGetSourceAppId(_session);
            var targetLike = !_liked;
            var invoked = await TryInvokeFavoriteActionAsync(sourceAppId, targetLike);
            if (!invoked)
            {
                ShowLikeUnavailableState();
                return;
            }

            _liked = targetLike;
            if (!string.IsNullOrWhiteSpace(_currentTrackSignature))
            {
                _trackLikeState[_currentTrackSignature] = _liked;
            }
            ApplyLikeState();
        }
        finally
        {
            _isLikeActionPending = false;
            LikeButton.IsEnabled = true;
        }
    }

    private async void PlayerTargetAutoButton_Click(object sender, RoutedEventArgs e)
    {
        await SelectPlayerControlTargetAsync(PlayerControlTarget.Auto);
    }

    private async void PlayerTargetQqButton_Click(object sender, RoutedEventArgs e)
    {
        await SelectPlayerControlTargetAsync(PlayerControlTarget.QQMusic);
    }

    private async void PlayerTargetNeteaseButton_Click(object sender, RoutedEventArgs e)
    {
        await SelectPlayerControlTargetAsync(PlayerControlTarget.NeteaseCloudMusic);
    }

    private async void PlayerTargetSpotifyButton_Click(object sender, RoutedEventArgs e)
    {
        await SelectPlayerControlTargetAsync(PlayerControlTarget.Spotify);
    }

    private async void PlayerTargetKugouButton_Click(object sender, RoutedEventArgs e)
    {
        await SelectPlayerControlTargetAsync(PlayerControlTarget.KuGouMusic);
    }

    private async void PlayerTargetSodaButton_Click(object sender, RoutedEventArgs e)
    {
        await SelectPlayerControlTargetAsync(PlayerControlTarget.SodaMusic);
    }

    private void NteLogoSelectorPanel_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CollapsePlayerPickerOverlay();
        EnterNteMode();
    }

    private static readonly Color NteModeBackgroundColor = Color.FromRgb(0x1F, 0x20, 0x26);

    private void NteBackButton_Click(object sender, RoutedEventArgs e)
    {
        ExitNteMode();
    }

    private void ExitNteMode()
    {
        _isNteMode = false;
        NteCloseCoverWindow();
        ApplyNteExpandedLayout(false);
        Height = _isDocked ? DockedHeight : DefaultFreeHeight;
        SystemPlayerPage.Visibility = Visibility.Visible;
        NtePlayerPage.Visibility = Visibility.Collapsed;

        _widgetBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        WidgetBackgroundHost.Background = _widgetBackgroundBrush;
        UpdateMainSpectrumPopupVisibility();

        Dispatcher.InvokeAsync(() =>
        {
            var targetColor = _isDocked
                ? DockedInteractiveTransparentColor
                : GetEffectiveBaseBackgroundColor();

            _widgetBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _widgetBackgroundBrush.Color = targetColor;

            if (!_isDocked && _currentPreview is null)
            {
                ApplyWidgetBackground(GetEffectiveBaseBackgroundColor());
            }
        }, DispatcherPriority.Render);
    }

    private void EnterNteMode()
    {
        _isNteMode = true;
        WidgetBorder.BeginAnimation(OpacityProperty, null);
        WidgetBorder.Opacity = 1d;
        SystemPlayerPage.Visibility = Visibility.Collapsed;
        NtePlayerPage.Visibility = Visibility.Visible;
        UpdateMainSpectrumPopupVisibility();

        _widgetBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        ApplyNteModeBackground();

        Dispatcher.InvokeAsync(() =>
        {
            _widgetBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            ApplyNteModeBackground();
        }, DispatcherPriority.Render);

        ClearLyricState();
        ResetPlaybackProgressUi();
        ApplyNteExpandedLayout(false);
        ApplyNteDockedContentLayout();
        InitializeNtePlayer();
    }

    private void ApplyNteModeBackground()
    {
        var color = ApplyWidgetOpacityToColor(NteModeBackgroundColor);
        _widgetBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _widgetBackgroundBrush.Color = color;
        WidgetBackgroundHost.Background = _widgetBackgroundBrush;
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> ResolveTargetSessionAsync(GlobalSystemMediaTransportControlsSessionManager sessionManager, bool forceRebind)
    {
        var allSessions = sessionManager.GetSessions().ToList();
        var useAutoSelection = _selectedPlayerTarget == PlayerControlTarget.Auto;
        var systemCurrentSession = sessionManager.GetCurrentSession();
        var candidateSessions = useAutoSelection
            ? allSessions.Where(session => !IsBrowserMediaSourceAppId(TryGetSourceAppId(session).ToLowerInvariant())).ToList()
            : allSessions.Where(IsSessionMatchSelectedTarget).ToList();

        if (!useAutoSelection
            && _selectedPlayerTarget == PlayerControlTarget.SodaMusic
            && candidateSessions.Count == 0
            && IsAnyProcessRunning("SodaMusic"))
        {
            candidateSessions = allSessions
                .Where(session => !IsKnownNonSodaSourceAppId(TryGetSourceAppId(session).ToLowerInvariant()))
                .ToList();
        }

        if (candidateSessions.Count == 0)
        {
            _lockedSession = null;
            _autoPinnedSession = null;
            _autoPinnedSessionUntilUtc = DateTime.MinValue;
            return null;
        }

        if (useAutoSelection && !forceRebind)
        {
            var pinnedSession = TryGetPinnedAutoSession(candidateSessions);
            if (pinnedSession is not null)
            {
                return pinnedSession;
            }
        }

        if (!forceRebind && _session is not null && candidateSessions.Any(s => s == _session))
        {
            var shouldKeepCurrentSession =
                !useAutoSelection
                || systemCurrentSession is null
                || systemCurrentSession == _session;

            // When the OS has a different playing current session, trust the OS
            // choice and allow re-evaluation instead of staying stuck on the old one.
            if (shouldKeepCurrentSession
                && useAutoSelection
                && systemCurrentSession is not null
                && systemCurrentSession != _session
                && IsPlayingSession(systemCurrentSession))
            {
                shouldKeepCurrentSession = false;
            }

            if (shouldKeepCurrentSession)
            {
                var currentScore = await ScoreSessionAsync(_session, systemCurrentSession);
                if (currentScore >= 40)
                {
                    return _session;
                }
            }
        }

        GlobalSystemMediaTransportControlsSession? bestSession = null;
        var bestScore = int.MinValue;
        foreach (var session in candidateSessions)
        {
            var score = await ScoreSessionAsync(session, systemCurrentSession);
            if (score > bestScore)
            {
                bestScore = score;
                bestSession = session;
            }
        }

        _lockedSession = useAutoSelection ? null : bestSession;
        return bestSession;
    }

    private void PinCurrentSessionForAutoMode()
    {
        if (_selectedPlayerTarget != PlayerControlTarget.Auto || _session is null)
        {
            return;
        }

        _autoPinnedSession = _session;
        _autoPinnedSessionUntilUtc = DateTime.UtcNow.Add(AutoSessionPinDuration);
    }

    private GlobalSystemMediaTransportControlsSession? TryGetPinnedAutoSession(IReadOnlyCollection<GlobalSystemMediaTransportControlsSession> candidateSessions)
    {
        if (DateTime.UtcNow > _autoPinnedSessionUntilUtc || _autoPinnedSession is null)
        {
            _autoPinnedSession = null;
            return null;
        }

        GlobalSystemMediaTransportControlsSession? foundPinned = null;
        foreach (var candidate in candidateSessions)
        {
            if (candidate == _autoPinnedSession)
            {
                foundPinned = candidate;
                break;
            }
        }

        if (foundPinned is null)
        {
            _autoPinnedSession = null;
            return null;
        }

        // If the pinned session is no longer playing but another candidate IS
        // playing, release the pin so we follow the active player.
        if (!IsPlayingSession(foundPinned))
        {
            var hasAnotherPlaying = candidateSessions.Any(s => s != foundPinned && IsPlayingSession(s));
            if (hasAnotherPlaying)
            {
                _autoPinnedSession = null;
                return null;
            }
        }

        return foundPinned;
    }

    private static async Task<int> ScoreSessionAsync(
        GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSession? systemCurrentSession)
    {
        var score = 0;
        var sourceAppId = TryGetSourceAppId(session).ToLowerInvariant();

        if (session == systemCurrentSession)
        {
            score += 12;
        }

        if (IsPlayingSession(session))
        {
            score += 20;
        }

        // NetEase helper/reporter sessions can expose media metadata but provide
        // poor timeline data; prefer the real playback session so the progress
        // bar and lyric clock stay stable.
        if (sourceAppId.Contains("cloudmusicreport", StringComparison.Ordinal))
        {
            score -= 24;
        }

        try
        {
            var timeline = session.GetTimelineProperties();
            var rawDurationMs = GetRawTimelineDurationMs(timeline);
            if (rawDurationMs > 1000d)
            {
                score += 18;
            }
            else
            {
                score -= 10;
            }

            if (timeline.LastUpdatedTime != default)
            {
                score += 2;
            }
        }
        catch
        {
            score -= 6;
        }

        try
        {
            var media = await session.TryGetMediaPropertiesAsync();
            var title = (media.Title ?? string.Empty).Trim();
            var artist = (media.Artist ?? media.AlbumArtist ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(title))
            {
                score += 14;
            }

            if (!string.IsNullOrWhiteSpace(artist))
            {
                score += 8;
            }
        }
        catch
        {
        }

        return score;
    }

    private static double GetRawTimelineDurationMs(GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
    {
        var endTimeMs = Math.Max(0d, timeline.EndTime.TotalMilliseconds);
        var maxSeekTimeMs = Math.Max(0d, timeline.MaxSeekTime.TotalMilliseconds);
        var endRangeMs = Math.Max(0d, (timeline.EndTime - timeline.StartTime).TotalMilliseconds);
        var seekRangeMs = Math.Max(0d, (timeline.MaxSeekTime - timeline.MinSeekTime).TotalMilliseconds);
        return Math.Max(Math.Max(endTimeMs, maxSeekTimeMs), Math.Max(endRangeMs, seekRangeMs));
    }

    private static bool IsPlayingSession(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private bool IsSessionMatchSelectedTarget(GlobalSystemMediaTransportControlsSession session)
    {
        var sourceAppId = TryGetSourceAppId(session).ToLowerInvariant();
        var resolvedTarget = ResolvePlayerTargetFromSourceAppId(sourceAppId);
        return _selectedPlayerTarget switch
        {
            PlayerControlTarget.QQMusic => resolvedTarget == PlayerControlTarget.QQMusic,
            PlayerControlTarget.NeteaseCloudMusic => resolvedTarget == PlayerControlTarget.NeteaseCloudMusic,
            PlayerControlTarget.Spotify => resolvedTarget == PlayerControlTarget.Spotify,
            PlayerControlTarget.YouTubeMusic => resolvedTarget == PlayerControlTarget.YouTubeMusic,
            PlayerControlTarget.KuGouMusic => resolvedTarget == PlayerControlTarget.KuGouMusic,
            PlayerControlTarget.SodaMusic => resolvedTarget == PlayerControlTarget.SodaMusic,
            _ => true
        };
    }

    private static bool IsLikelyPlayerWindowMatch(string? trackTitle, params string[] processNames)
    {
        return IsLikelyPlayerWindowMatch(trackTitle, GetVisibleProcessWindowTitles(processNames));
    }

    private static bool IsLikelyPlayerWindowMatch(string? trackTitle, IReadOnlyCollection<string> visibleWindowTitles)
    {
        if (string.IsNullOrWhiteSpace(trackTitle) || visibleWindowTitles.Count == 0)
        {
            return false;
        }

        var normalizedTitle = NormalizeLyricMatchText(CleanLyricSearchText(trackTitle));
        if (string.IsNullOrWhiteSpace(normalizedTitle) || normalizedTitle.Length < 2)
        {
            return false;
        }

        foreach (var windowTitle in visibleWindowTitles)
        {
            var normalizedWindowTitle = NormalizeLyricMatchText(windowTitle);
            if (normalizedWindowTitle.Contains(normalizedTitle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int? ResolveAppCommandForAutomationNames(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (name.Contains("涓婁竴", StringComparison.Ordinal))
            {
                return APPCOMMAND_MEDIA_PREVIOUSTRACK;
            }

            if (name.Contains("涓嬩竴", StringComparison.Ordinal))
            {
                return APPCOMMAND_MEDIA_NEXTTRACK;
            }

            if (name.Contains("鎾斁", StringComparison.Ordinal)
                || name.Contains("鏆傚仠", StringComparison.Ordinal))
            {
                return APPCOMMAND_MEDIA_PLAY_PAUSE;
            }
        }

        return null;
    }

    private static List<string> GetVisibleProcessWindowTitles(params string[] processNames)
    {
        var titles = new List<string>();
        foreach (var name in processNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero || !IsWindowVisible(process.MainWindowHandle))
                    {
                        continue;
                    }

                    var title = process.MainWindowTitle ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        titles.Add(title);
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return titles;
    }

    private static bool IsKuGouSourceAppId(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }

        return sourceAppId.Contains("kugou")
               || sourceAppId.Contains("kgmusic")
               || sourceAppId.Contains("ginkgo");
    }

    private static bool IsMoeKoeMusicSourceAppId(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }

        return sourceAppId.Contains("moekoe")
               || sourceAppId.Contains("moe koe")
               || sourceAppId.Contains("moekoemusic");
    }

    private static bool IsSodaSourceAppId(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }

        return sourceAppId.Contains("sodamusic")
               || sourceAppId.Contains("soda music")
               || sourceAppId.Contains("sodamusic.exe")
               || sourceAppId.Contains("qishui")
               || sourceAppId.Contains("luna")
               || sourceAppId.Contains("douyin")
               || sourceAppId.Contains("snssdk");
    }

    private static bool IsYouTubeMusicSourceAppId(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }

        return sourceAppId.Contains("youtubemusic")
               || sourceAppId.Contains("youtube music")
               || sourceAppId.Contains("youtube-music")
               || sourceAppId.Contains("ytmusic")
               || sourceAppId.Contains("ytmdesktop");
    }

    private static bool IsBrowserMediaSourceAppId(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }

        return sourceAppId.Contains("chrome")
               || sourceAppId.Contains("msedge")
               || sourceAppId.Contains("brave")
               || sourceAppId.Contains("vivaldi")
               || sourceAppId.Contains("opera");
    }

    private static bool IsKnownNonSodaSourceAppId(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }

        return sourceAppId.Contains("qqmusic")
               || sourceAppId.Contains("cloudmusic")
               || sourceAppId.Contains("netease")
               || sourceAppId.Contains("music.163")
               || sourceAppId.Contains("spotify")
               || IsMoeKoeMusicSourceAppId(sourceAppId)
               || IsYouTubeMusicSourceAppId(sourceAppId)
               || IsKuGouSourceAppId(sourceAppId);
    }

    private bool HasActiveFallbackPlayerDisplayState()
    {
        return ShouldUseKuGouAutomationFallback() && IsCurrentTrackSignatureFromSource("KuGou");
    }

    private bool IsCurrentTrackSignatureFromSource(string sourceTag)
    {
        return !string.IsNullOrWhiteSpace(_currentTrackSignature)
               && _currentTrackSignature.EndsWith($"|{sourceTag}", StringComparison.Ordinal);
    }

    private bool ShouldUseKuGouAutomationFallback()
    {
        return _selectedPlayerTarget == PlayerControlTarget.KuGouMusic
               && IsAnyProcessRunning(KuGouProcessNames);
    }

    private bool IsSameDisplayedFallbackTrack(string trackSignature, string title, string artist, string fallbackArtistText)
    {
        return string.Equals(_currentTrackSignature, trackSignature, StringComparison.Ordinal)
               && string.Equals(SongTitleText.Text, string.IsNullOrWhiteSpace(title) ? "鏈煡姝屾洸" : title, StringComparison.Ordinal)
               && string.Equals(ArtistText.Text, string.IsNullOrWhiteSpace(artist) ? fallbackArtistText : artist, StringComparison.Ordinal);
    }

}

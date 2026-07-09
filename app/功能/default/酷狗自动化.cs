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
using Windows.Media.Control;

namespace MusicBar;

public partial class MainWindow : Window
{
    private async Task RefreshKuGouAutomationUiAsync()
    {
        if (_isKuGouAutomationRefreshPending || !ShouldUseKuGouAutomationFallback())
        {
            return;
        }

        _isKuGouAutomationRefreshPending = true;
        try
        {
            if (_session is null)
            {
                _ = await TryApplyKuGouAutomationSnapshotAsync();
            }
        }
        finally
        {
            _isKuGouAutomationRefreshPending = false;
            await Task.CompletedTask;
        }
    }

    private async Task<bool> TryApplyKuGouAutomationSnapshotAsync()
    {
        return await TryApplyKuGouAutomationSnapshotAsync(forceRefresh: false);
    }

    private async Task<bool> TryApplyKuGouAutomationSnapshotAsync(bool forceRefresh)
    {
        if (!ShouldUseKuGouAutomationFallback())
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (!forceRefresh && now - _lastKuGouSnapshotRefreshUtc < KuGouSnapshotRefreshInterval)
        {
            return false;
        }
        _lastKuGouSnapshotRefreshUtc = now;

        var snapshot = await TryGetKuGouPlaybackSnapshotAsync();
        if (snapshot is null)
        {
            return false;
        }

        var trackSignature = BuildTrackSignature(snapshot.Title, snapshot.Artist, "KuGou");
        var lyricTrackSignature = BuildLyricTrackSignature(snapshot.Title, snapshot.Artist);
        if (IsSameDisplayedFallbackTrack(trackSignature, snapshot.Title, snapshot.Artist, "来自酷狗窗口"))
        {
            SetPlayPauseIcon(snapshot.IsPlaying);
            return true;
        }

        SongTitleText.Text = string.IsNullOrWhiteSpace(snapshot.Title) ? "未知歌曲" : snapshot.Title.Trim();
        ArtistText.Text = string.IsNullOrWhiteSpace(snapshot.Artist) ? "来自酷狗窗口" : snapshot.Artist.Trim();

        RefreshLyricsForTrack(lyricTrackSignature, snapshot.Title, snapshot.Artist);
        _currentTrackSignature = trackSignature;
        _liked = snapshot.IsLiked || (_trackLikeState.TryGetValue(trackSignature, out var rememberedLiked) && rememberedLiked);
        ApplyLikeState();
        SetPlayPauseIcon(snapshot.IsPlaying);
        SetAlbumArt(null);
        return true;
    }

    private void UpdateKuGouWindowTitleHook()
    {
        if (_selectedPlayerTarget != PlayerControlTarget.KuGouMusic)
        {
            StopKuGouWindowTitleHook();
            return;
        }

        var targetWindow = FindPreferredKuGouWindow();
        if (targetWindow == IntPtr.Zero)
        {
            StopKuGouWindowTitleHook();
            return;
        }

        if (_kuGouTitleHook != IntPtr.Zero && _kuGouHookWindow == targetWindow)
        {
            return;
        }

        StopKuGouWindowTitleHook();
        var kuGouProcessId = GetWindowProcessId(targetWindow);
        if (kuGouProcessId == 0)
        {
            return;
        }

        _kuGouTitleHook = SetWinEventHook(
            EVENT_OBJECT_NAMECHANGE,
            EVENT_OBJECT_NAMECHANGE,
            IntPtr.Zero,
            _kuGouTitleChangedHandler,
            kuGouProcessId,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_kuGouTitleHook != IntPtr.Zero)
        {
            _kuGouHookWindow = targetWindow;
        }
    }

    private void StopKuGouWindowTitleHook()
    {
        if (_kuGouTitleHook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(_kuGouTitleHook);
            _kuGouTitleHook = IntPtr.Zero;
        }

        _kuGouHookWindow = IntPtr.Zero;
    }

    private void OnKuGouWindowTitleChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (_selectedPlayerTarget != PlayerControlTarget.KuGouMusic)
        {
            return;
        }

        if (eventType != EVENT_OBJECT_NAMECHANGE || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (idObject != OBJID_WINDOW || idChild != 0)
        {
            return;
        }

        if (_kuGouHookWindow != IntPtr.Zero && hwnd != _kuGouHookWindow)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (_selectedPlayerTarget != PlayerControlTarget.KuGouMusic)
            {
                return;
            }

            await TryApplyKuGouAutomationSnapshotAsync(forceRefresh: true);
            UpdateKuGouWindowTitleHook();
        });
    }

    private static Task<KuGouPlaybackSnapshot?> TryGetKuGouPlaybackSnapshotAsync()
    {
        if (!TryGetPreferredKuGouWindowInfo(out var targetWindow, out var windowTitle))
        {
            return Task.FromResult<KuGouPlaybackSnapshot?>(null);
        }

        if (!TryParseKuGouWindowTitle(windowTitle, out var title, out var artist))
        {
            return Task.FromResult<KuGouPlaybackSnapshot?>(null);
        }

        return Task.FromResult<KuGouPlaybackSnapshot?>(new KuGouPlaybackSnapshot(title, artist, IsPlaying: true, IsLiked: false));
    }

    private static bool TryParseKuGouTrack(string rawText, out string title, out string artist)
    {
        title = string.Empty;
        artist = string.Empty;

        var separator = rawText.IndexOf(" - ", StringComparison.Ordinal);
        if (separator <= 0 || separator >= rawText.Length - 3)
        {
            return false;
        }

        title = rawText[..separator].Trim();
        artist = rawText[(separator + 3)..].Trim();
        return !string.IsNullOrWhiteSpace(title);
    }

    private static bool TryParseKuGouWindowTitle(string rawTitle, out string title, out string artist)
    {
        title = string.Empty;
        artist = string.Empty;

        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return false;
        }

        var normalized = rawTitle.Trim();
        const string suffix = " - 酷狗音乐";
        if (normalized.EndsWith(suffix, StringComparison.Ordinal))
        {
            normalized = normalized[..^suffix.Length].Trim();
        }

        var segments = normalized.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 2)
        {
            artist = segments[0];
            title = segments[1];
            return !string.IsNullOrWhiteSpace(title);
        }

        title = normalized;
        return !string.IsNullOrWhiteSpace(title);
    }

    private async Task RefreshFallbackStateAfterControlAsync()
    {
        await Task.Delay(180);
        await RefreshCurrentSessionAsync(forceRebind: false);
    }

    private async Task<bool> TryInvokeKuGouTransportControlAsync(params string[] automationNames)
    {
        var targetWindow = FindPreferredKuGouWindow();
        if (targetWindow == IntPtr.Zero)
        {
            return false;
        }

        var previousForeground = GetForegroundWindow();
        if (!await TryActivateTargetWindowAsync(targetWindow))
        {
            return false;
        }

        try
        {
            if (await TryInvokeNamedAutomationButtonAsync(targetWindow, automationNames))
            {
                return true;
            }

            var appCommand = ResolveAppCommandForAutomationNames(automationNames);
            if (!appCommand.HasValue)
            {
                return false;
            }

            return await TrySendAppCommandToWindowAsync(targetWindow, appCommand.Value);
        }
        finally
        {
            RestoreForegroundWindow(previousForeground, targetWindow);
        }
    }

    private async Task<bool> TryInvokeKuGouPlaybackModeControlAsync(params string[] automationNames)
    {
        var targetWindow = FindPreferredKuGouWindow();
        if (targetWindow == IntPtr.Zero)
        {
            return false;
        }

        var previousForeground = GetForegroundWindow();
        if (!await TryActivateTargetWindowAsync(targetWindow))
        {
            return false;
        }

        try
        {
            return await TryInvokeNamedAutomationButtonAsync(targetWindow, automationNames);
        }
        finally
        {
            RestoreForegroundWindow(previousForeground, targetWindow);
        }
    }

    private static IntPtr FindPreferredKuGouWindow()
    {
        return TryGetPreferredKuGouWindowInfo(out var bestHandle, out _) ? bestHandle : IntPtr.Zero;
    }

    private static bool TryGetPreferredKuGouWindowInfo(out IntPtr bestHandle, out string windowTitle)
    {
        bestHandle = IntPtr.Zero;
        var bestScore = int.MinValue;
        windowTitle = string.Empty;

        foreach (var name in KuGouProcessNames)
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
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero || !IsWindowVisible(handle))
                    {
                        continue;
                    }

                    var score = ScoreKuGouWindow(process.ProcessName, process.MainWindowTitle, handle);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestHandle = handle;
                        windowTitle = process.MainWindowTitle ?? string.Empty;
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

        return bestHandle != IntPtr.Zero;
    }

    private static int ScoreKuGouWindow(string processName, string? windowTitle, IntPtr handle)
    {
        var score = 0;

        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            score += 12;
        }

        if (!string.IsNullOrWhiteSpace(windowTitle) && windowTitle.Contains("酷狗音乐", StringComparison.OrdinalIgnoreCase))
        {
            score += 36;
        }

        if (!string.IsNullOrWhiteSpace(windowTitle) && windowTitle.Contains(" - ", StringComparison.Ordinal))
        {
            score += 80;
        }

        if (string.Equals(processName, "KuGou", StringComparison.OrdinalIgnoreCase))
        {
            score += 18;
        }

        var className = GetClassNameSafe(handle);
        if (className.Contains("kugou", StringComparison.OrdinalIgnoreCase))
        {
            score += 24;
        }

        return score;
    }

    private async Task<bool> TryInvokeFavoriteActionAsync(string sourceAppId, bool targetLike)
    {
        var rule = ResolveFavoriteRule(sourceAppId);
        if (rule is not null)
        {
            // If no key chords defined, fall through to UI Automation for this player.
            if (rule.LikeChords.Length > 0)
            {
                var targetWindow = FindPreferredWindow(rule);
                if (targetWindow == IntPtr.Zero)
                {
                    return false;
                }

                var chords = targetLike
                    ? rule.LikeChords
                    : (rule.UnlikeChords is { Length: > 0 } ? rule.UnlikeChords : rule.LikeChords);

                foreach (var chord in chords)
                {
                    if (await TrySendChordToWindowAsync(targetWindow, chord))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        return await TryInvokeFavoriteAutomationAsync(sourceAppId, targetLike);
    }

    private async Task<bool> TryInvokeFavoriteAutomationAsync(string sourceAppId, bool targetLike)
    {
        if (!IsKuGouSourceAppId(sourceAppId) && !IsAnyProcessRunning(KuGouProcessNames))
        {
            return false;
        }

        var targetWindow = FindPreferredKuGouWindow();
        if (targetWindow == IntPtr.Zero)
        {
            return false;
        }

        var previousForeground = GetForegroundWindow();
        if (!await TryActivateTargetWindowAsync(targetWindow))
        {
            return false;
        }

        try
        {
            return await TryInvokeFavoriteAutomationButtonAsync(targetWindow, targetLike);
        }
        finally
        {
            RestoreForegroundWindow(previousForeground, targetWindow);
        }
    }

    private static string TryGetSourceAppId(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.SourceAppUserModelId ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static FavoriteRule? ResolveFavoriteRule(string sourceAppId)
    {
        if (!string.IsNullOrWhiteSpace(sourceAppId))
        {
            var lower = sourceAppId.ToLowerInvariant();
            var bySession = FavoriteRules.FirstOrDefault(r => lower.Contains(r.SourceKeyword));
            if (bySession is not null)
            {
                return bySession;
            }

            // If sourceAppId looks like a browser/Chromium-based player, do NOT
            // fallback to other players' rules — it would trigger the wrong action.
            if (IsBrowserMediaSourceAppId(lower))
            {
                return null;
            }
        }

        // Fallback: infer player by running process when session app id is ambiguous.
        if (IsAnyProcessRunning("cloudmusic", "NeteaseCloudMusic", "cloudmusicreport"))
        {
            return FavoriteRules.FirstOrDefault(r => r.SourceKeyword == "cloudmusic");
        }

        if (IsAnyProcessRunning("QQMusic", "QQMusicExternal"))
        {
            return FavoriteRules.FirstOrDefault(r => r.SourceKeyword == "qqmusic");
        }

        if (IsAnyProcessRunning("Spotify"))
        {
            return FavoriteRules.FirstOrDefault(r => r.SourceKeyword == "spotify");
        }

        return null;
    }

    private static bool IsAnyProcessRunning(params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0)
                {
                    return true;
                }
            }
            catch
            {
                // Ignore and continue.
            }
        }

        return false;
    }

    private TaskbarAlignment DetectTaskbarAlignment()
    {
        try
        {
            using var advancedKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            var taskbarAlignment = advancedKey?.GetValue("TaskbarAl");
            if (taskbarAlignment is int alignmentValue)
            {
                return alignmentValue == 0 ? TaskbarAlignment.Left : TaskbarAlignment.Center;
            }

            if (taskbarAlignment is long alignmentValueLong)
            {
                return alignmentValueLong == 0 ? TaskbarAlignment.Left : TaskbarAlignment.Center;
            }
        }
        catch
        {
            // Fallback below.
        }

        return TaskbarAlignment.Center;
    }

    private static async Task<(bool Success, bool Invoked)> TryRunUiAutomationAsync(Func<bool> action, int timeoutMs)
    {
        var completion = new TaskCompletionSource<(bool Success, bool Invoked)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult((true, action()));
            }
            catch
            {
                completion.TrySetResult((false, false));
            }
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var completed = await Task.WhenAny(completion.Task, Task.Delay(timeoutMs));
        if (completed != completion.Task)
        {
            return (false, false);
        }

        return await completion.Task;
    }

    private static async Task<(bool Success, T Result)> TryRunUiAutomationAsync<T>(Func<T> action, int timeoutMs, T defaultResult)
    {
        var completion = new TaskCompletionSource<(bool Success, T Result)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult((true, action()));
            }
            catch
            {
                completion.TrySetResult((false, defaultResult));
            }
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var completed = await Task.WhenAny(completion.Task, Task.Delay(timeoutMs));
        if (completed != completion.Task)
        {
            return (false, defaultResult);
        }

        return await completion.Task;
    }

    private static async Task<bool> WaitForForegroundWindowAsync(IntPtr targetWindow, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (GetForegroundWindow() == targetWindow)
            {
                return true;
            }

            await Task.Delay(40);
        }

        return GetForegroundWindow() == targetWindow;
    }

    private static IEnumerable<AutomationElement> EnumerateAutomationTreeLimited(AutomationElement? root, int maxDepth, int maxNodes)
    {
        if (root is null || maxDepth < 0 || maxNodes <= 0)
        {
            yield break;
        }

        var stack = new Stack<(AutomationElement Element, int Depth)>();
        stack.Push((root, 0));
        var yielded = 0;

        while (stack.Count > 0 && yielded < maxNodes)
        {
            var (element, depth) = stack.Pop();
            yield return element;
            yielded++;

            if (depth >= maxDepth || yielded >= maxNodes)
            {
                continue;
            }

            var children = new List<AutomationElement>();
            try
            {
                var walker = TreeWalker.ControlViewWalker;
                var child = walker.GetFirstChild(element);
                while (child is not null)
                {
                    children.Add(child);
                    child = walker.GetNextSibling(child);
                }
            }
            catch
            {
                continue;
            }

            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push((children[i], depth + 1));
            }
        }
    }

    private static async Task<bool> TryInvokeNamedAutomationButtonAsync(IntPtr targetWindow, params string[] names)
    {
        var (success, invoked) = await TryRunUiAutomationAsync(() =>
        {
            var root = AutomationElement.FromHandle(targetWindow);
            foreach (var candidate in EnumerateAutomationTreeLimited(root, maxDepth: KuGouAutomationMaxDepth, maxNodes: KuGouAutomationMaxNodes))
            {
                var currentName = candidate.Current.Name ?? string.Empty;
                if (!IsAutomationNameMatch(currentName, names))
                {
                    continue;
                }

                if (TryInvokeAutomationElement(candidate))
                {
                    return true;
                }
            }
            return false;
        }, timeoutMs: KuGouAutomationTimeoutMs);

        if (!success)
        {
            return false;
        }

        return invoked;
    }

    private static bool IsAutomationNameMatch(string currentName, IEnumerable<string> names)
    {
        if (string.IsNullOrWhiteSpace(currentName))
        {
            return false;
        }

        var normalizedCurrent = NormalizeLyricMatchText(currentName);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (string.Equals(currentName, name, StringComparison.Ordinal)
                || currentName.Contains(name, StringComparison.Ordinal)
                || name.Contains(currentName, StringComparison.Ordinal))
            {
                return true;
            }

            var normalizedName = NormalizeLyricMatchText(name);
            if (!string.IsNullOrWhiteSpace(normalizedName)
                && (normalizedCurrent.Equals(normalizedName, StringComparison.Ordinal)
                    || normalizedCurrent.Contains(normalizedName, StringComparison.Ordinal)
                    || normalizedName.Contains(normalizedCurrent, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static IntPtr FindPreferredWindow(FavoriteRule rule)
    {
        return FindPreferredWindow(rule.ProcessNames);
    }

    private static IntPtr FindPreferredWindow(params string[] processNames)
    {
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
                    if (process.MainWindowHandle != IntPtr.Zero && IsWindowVisible(process.MainWindowHandle))
                    {
                        return process.MainWindowHandle;
                    }
                }
                catch
                {
                    // Ignore per-process failures and continue searching.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return IntPtr.Zero;
    }

    private async Task<bool> TryActivateTargetWindowAsync(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero)
        {
            return false;
        }

        _suspendTopmostGuardUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);

        var selfHwnd = new WindowInteropHelper(this).Handle;
        if (selfHwnd != IntPtr.Zero)
        {
            _ = SetWindowPos(selfHwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        _ = ShowWindowAsync(targetWindow, SW_RESTORE);
        _ = SetForegroundWindow(targetWindow);
        await Task.Delay(60);

        if (!await WaitForForegroundWindowAsync(targetWindow, 320))
        {
            EnsureTopmost();
            return false;
        }

        return true;
    }

    private void RestoreForegroundWindow(IntPtr previousForeground, IntPtr targetWindow)
    {
        if (previousForeground != IntPtr.Zero && previousForeground != targetWindow)
        {
            _ = SetForegroundWindow(previousForeground);
        }

        EnsureTopmost();
    }

    private async Task<bool> TrySendChordToWindowAsync(IntPtr targetWindow, FavoriteKeyChord chord)
    {
        if (targetWindow == IntPtr.Zero)
        {
            return false;
        }

        var previousForeground = GetForegroundWindow();
        if (!await TryActivateTargetWindowAsync(targetWindow))
        {
            return false;
        }

        try
        {
            SendChord(chord);
            await Task.Delay(40);
            return true;
        }
        finally
        {
            RestoreForegroundWindow(previousForeground, targetWindow);
        }
    }

    private static Task<bool> TrySendAppCommandToWindowAsync(IntPtr targetWindow, int appCommand)
    {
        if (targetWindow == IntPtr.Zero)
        {
            return Task.FromResult(false);
        }

        var lParam = (IntPtr)((appCommand | FAPPCOMMAND_KEY) << 16);
        var result = SendMessage(targetWindow, WM_APPCOMMAND, targetWindow, lParam);
        return Task.FromResult(result != IntPtr.Zero);
    }

    private static async Task<bool> TryInvokeFavoriteAutomationButtonAsync(IntPtr targetWindow, bool targetLike)
    {
        var (success, invoked) = await TryRunUiAutomationAsync(() =>
        {
            var root = AutomationElement.FromHandle(targetWindow);
            var names = targetLike ? KuGouLikeAutomationNames : KuGouUnlikeAutomationNames;

            foreach (var candidate in EnumerateAutomationTreeLimited(root, maxDepth: KuGouAutomationMaxDepth, maxNodes: KuGouAutomationMaxNodes))
            {
                if (!IsFavoriteAutomationNameMatch(candidate.Current.Name, names))
                {
                    continue;
                }

                if (TryInvokeAutomationElement(candidate))
                {
                    return true;
                }
            }
            return false;
        }, timeoutMs: 520);

        if (!success)
        {
            return false;
        }

        return invoked;
    }

    private static bool IsFavoriteAutomationNameMatch(string? currentName, string[] candidates)
    {
        if (string.IsNullOrWhiteSpace(currentName))
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (currentName.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryInvokeAutomationElement(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern))
            {
                ((InvokePattern)invokePattern).Invoke();
                return true;
            }

            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern))
            {
                ((TogglePattern)togglePattern).Toggle();
                return true;
            }

            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionItemPattern))
            {
                ((SelectionItemPattern)selectionItemPattern).Select();
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static void SendChord(FavoriteKeyChord chord)
    {
        if (chord.Ctrl) keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        if (chord.Alt) keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        if (chord.Shift) keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);

        keybd_event(chord.Key, 0, 0, UIntPtr.Zero);
        keybd_event(chord.Key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

        if (chord.Shift) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (chord.Alt) keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (chord.Ctrl) keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private void ShowLikeUnavailableState()
    {
        LikeButton.Foreground = GetResourceBrush("LikeUnavailableBrush");
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(500);
            ApplyLikeState();
        });
    }

    private void ApplyLikeState()
    {
        LikeButton.Foreground = _liked
            ? GetResourceBrush("LikeActiveBrush")
            : GetResourceBrush("IconBrush");
        LikeButton.Opacity = _isLikeActionPending ? 0.72d : 1d;
    }

    private static string BuildTrackSignature(string? title, string? artist, string? album)
    {
        return $"{title ?? string.Empty}|{artist ?? string.Empty}|{album ?? string.Empty}".Trim();
    }

    private static string BuildLyricTrackSignature(string? title, string? artist)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? string.Empty : title.Trim();
        var normalizedArtist = string.IsNullOrWhiteSpace(artist) ? string.Empty : artist.Trim();
        return $"{normalizedTitle}|{normalizedArtist}";
    }

    private async Task SelectPlayerControlTargetAsync(PlayerControlTarget target)
    {
        _selectedPlayerTarget = target;
        _lockedSession = null;

        UpdatePlayerTargetButtonsVisual();
        CollapsePlayerPickerOverlay();
        await RefreshCurrentSessionAsync(forceRebind: true);
        UpdateKuGouWindowTitleHook();
    }

    private void TogglePlayerPickerOverlay()
    {
        var show = !PlayerPickerPopup.IsOpen;
        PlayerPickerPopup.IsOpen = show;
        AnimatePickerArrow(show);
    }

    private void CollapsePlayerPickerOverlay()
    {
        PlayerPickerPopup.IsOpen = false;
        AnimatePickerArrow(false);
    }

    private void UpdatePlayerTargetButtonsVisual()
    {
        ApplyPlayerTargetButtonVisual(PlayerTargetAutoButton, _selectedPlayerTarget == PlayerControlTarget.Auto, Colors.White);
        ApplyPlayerTargetButtonVisual(PlayerTargetQqButton, _selectedPlayerTarget == PlayerControlTarget.QQMusic, (Color)ColorConverter.ConvertFromString("#FFE4C534"));
        ApplyPlayerTargetButtonVisual(PlayerTargetNeteaseButton, _selectedPlayerTarget == PlayerControlTarget.NeteaseCloudMusic, (Color)ColorConverter.ConvertFromString("#FFED3D48"));
        ApplyPlayerTargetButtonVisual(PlayerTargetSpotifyButton, _selectedPlayerTarget == PlayerControlTarget.Spotify, (Color)ColorConverter.ConvertFromString("#FF1ED760"));
        ApplyPlayerTargetButtonVisual(PlayerTargetKugouButton, _selectedPlayerTarget == PlayerControlTarget.KuGouMusic, (Color)ColorConverter.ConvertFromString("#FF4BB7FF"));
        ApplyPlayerTargetButtonVisual(PlayerTargetSodaButton, _selectedPlayerTarget == PlayerControlTarget.SodaMusic, (Color)ColorConverter.ConvertFromString("#FF5AE6FF"));
    }

    private static void ApplyPlayerTargetButtonVisual(Button button, bool isActive, Color glowColor)
    {
        button.BorderThickness = isActive ? new Thickness(1) : new Thickness(0);
        button.BorderBrush = isActive
            ? new SolidColorBrush(Color.FromArgb(0xCC, glowColor.R, glowColor.G, glowColor.B))
            : Brushes.Transparent;
        button.Background = isActive
            ? new SolidColorBrush(Color.FromArgb(0x30, glowColor.R, glowColor.G, glowColor.B))
            : Brushes.Transparent;
        button.Effect = isActive
            ? new DropShadowEffect
            {
                Color = glowColor,
                BlurRadius = 16,
                ShadowDepth = 0,
                Opacity = 0.82
            }
            : null;
        button.Opacity = isActive ? 1d : 0.86d;
    }

}

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
    private const double DockedWidth = 360d;
    private const double CompactNanoDockedWidth = 112d;
    private const double CompactNanoMaxSlotWidth = 520d;
    private const double DockedHeight = 40d;
    private const double RightSplitDockBias = 160d;
    private const double LeftAlignedRightDockInset = 184d;
    private const double DefaultFreeWidth = 430d;
    private const double DefaultFreeHeight = 46d;
    private const double NteExpandedHeight = 190d;
    private const double DefaultFreeTop = 5d;
    private static readonly TimeSpan RestoreAnimationDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan RestoreEmphasisDuration = TimeSpan.FromMilliseconds(220);
    private const double DockedEdgeMargin = 12d;
    private const double SnapPreviewDistance = 48d;
    private const double SnapConfirmDistance = 12d;
    private const double MinOccupiedWidth = 16d;

    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private GlobalSystemMediaTransportControlsSession? _lockedSession;
    private GlobalSystemMediaTransportControlsSession? _autoPinnedSession;
    private DateTime _autoPinnedSessionUntilUtc = DateTime.MinValue;
    private bool _liked;
    private bool _isLikeActionPending;
    private string _currentTrackSignature = string.Empty;
    private readonly Dictionary<string, bool> _trackLikeState = new(StringComparer.Ordinal);
    private readonly Dictionary<PlayerControlTarget, BitmapImage> _playerLogoCache = new();
    private bool _isDragging;
    private bool _isPointerDown;
    private bool _isAlbumArtPointerDown;
    private bool _isNteMode;
    private bool _wasDockedBeforeDrag;
    private bool _isDocked;
    private DockedStyle _currentDockedStyle = DockedStyle.Normal;
    private DockSide _preferredDockSide = DockSide.Right;
    private double _freeLeft;
    private double _freeTop;
    private SnapTarget? _currentPreview;
    private DispatcherTimer? _visibilityGuardTimer;
    private Point _dragStartScreen;
    private const double DragStartThreshold = 6d;
    private DateTime _suspendTopmostGuardUntilUtc = DateTime.MinValue;
    private static readonly TimeSpan AutoSessionPinDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AutoModeSessionPollInterval = TimeSpan.FromMilliseconds(1200);
    private bool _isSessionRefreshInProgress;
    private DateTime _lastSessionRefreshAttemptUtc = DateTime.MinValue;
    private DispatcherTimer? _lyricTimer;
    private IReadOnlyList<LyricLine> _currentLyricLines = Array.Empty<LyricLine>();
    private string _loadedLyricTrackSignature = string.Empty;
    private string _lastLyricLineKey = string.Empty;
    private double _renderedLyricScrollLeft;
    private double _targetLyricScrollLeft;
    private DateTime _lastLyricFrameUtc = DateTime.UtcNow;
    private DateTime _lastVisibleLyricProbeUtc = DateTime.MinValue;
    private string _visiblePlayerLyric = string.Empty;
    private bool _hasLyricProgressAnchor;
    private double _lyricProgressAnchorMs;
    private DateTime _lyricProgressAnchorUtc = DateTime.UtcNow;
    private double _lastObservedTimelinePositionMs = -1d;
    private GlobalSystemMediaTransportControlsSessionPlaybackStatus _lastObservedPlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
    private DateTime _resumePlaybackGraceUntilUtc = DateTime.MinValue;
    private DateTime _transportControlSessionGraceUntilUtc = DateTime.MinValue;
    // Independent virtual lyric clock: it only advances while the underlying
    // session reports Playing, so pause / resume never resets the clock by
    // accident even if the player reports a glitched timeline.
    private double _virtualLyricClockMs = -1d;
    private DateTime _virtualLyricClockTickUtc = DateTime.UtcNow;
    private GlobalSystemMediaTransportControlsSessionPlaybackStatus _virtualLyricClockLastStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
    private bool _isProgressDragging;
    private double _pendingSeekVisualPositionMs = -1d;
    private DateTime _pendingSeekVisualHoldUntilUtc = DateTime.MinValue;
    private ProgressBarDisplayMode _progressBarDisplayMode = ProgressBarDisplayMode.InlineBottomBar;
    private DefaultPlaybackMode _defaultPlaybackMode = DefaultPlaybackMode.Sequential;
    private PlaybackProgressSnapshot? _lastPlaybackProgressSnapshot;
    private double _lastStableTimelineDurationMs = -1d;
    private DateTimeOffset _lastSeenTimelineUpdateAt = DateTimeOffset.MinValue;
    private const double LyricScrollTriggerRatio = 0.9d;
    private const double LyricScrollAnchorRatio = 0.1d;
    private const double LyricScrollLerpSpeed = 5d;
    private static readonly TimeSpan ResumePlaybackGraceInterval = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan TransportControlSessionGraceInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan VisibleLyricProbeInterval = TimeSpan.FromMilliseconds(900);
    private static readonly Regex LrcTimestampRegex = new(@"\[(?<time>\d{1,2}:\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);
    private static readonly Regex KrcLineRegex = new(@"^\[(?<start>\d+),(?<duration>\d+)\](?<content>.*)$", RegexOptions.Compiled);
    private static readonly Regex KrcWordRegex = new(@"<(?<start>\d+),(?<duration>\d+)(?:,\d+)?>(?<text>[^<]*)", RegexOptions.Compiled);
    private static readonly Regex BracketedTitlePartRegex = new(@"[\(\（\[\【].*?[\)\）\]\】]", RegexOptions.Compiled);
    private static readonly HttpClient LyricHttpClient = CreateLyricHttpClient();
    private readonly HashSet<string> _lyricFetchesInProgress = new(StringComparer.Ordinal);

    private static readonly Color LightBackgroundColor = (Color)ColorConverter.ConvertFromString("#F018181A");
    private static readonly Color LightPreviewColor = (Color)ColorConverter.ConvertFromString("#FFEFF3F8");
    private static readonly Color DarkBackgroundColor = (Color)ColorConverter.ConvertFromString("#F018181A");
    private static readonly Color DarkPreviewColor = (Color)ColorConverter.ConvertFromString("#F0202023");
    private static readonly Color LightBorderColor = (Color)ColorConverter.ConvertFromString("#220F172A");
    private static readonly Color DarkBorderColor = (Color)ColorConverter.ConvertFromString("#40FFFFFF");
    private static readonly Color LightPrimaryTextColor = (Color)ColorConverter.ConvertFromString("#FFE8E8EA");
    private static readonly Color LightSecondaryTextColor = (Color)ColorConverter.ConvertFromString("#C8C8C8CC");
    private static readonly Color DarkPrimaryTextColor = (Color)ColorConverter.ConvertFromString("#FFE8E8EA");
    private static readonly Color DarkSecondaryTextColor = (Color)ColorConverter.ConvertFromString("#C8C8C8CC");
    private static readonly Color LightIconColor = (Color)ColorConverter.ConvertFromString("#FFE8E8EA");
    private static readonly Color DarkIconColor = (Color)ColorConverter.ConvertFromString("#FFE8E8EA");
    private static readonly Color LightButtonHoverColor = (Color)ColorConverter.ConvertFromString("#0F0F172A");
    private static readonly Color DarkButtonHoverColor = (Color)ColorConverter.ConvertFromString("#22FFFFFF");
    private static readonly Color LightButtonPressedColor = (Color)ColorConverter.ConvertFromString("#1A0F172A");
    private static readonly Color DarkButtonPressedColor = (Color)ColorConverter.ConvertFromString("#38FFFFFF");
    private static readonly Color LightAlbumPlaceholderColor = (Color)ColorConverter.ConvertFromString("#14000000");
    private static readonly Color DarkAlbumPlaceholderColor = (Color)ColorConverter.ConvertFromString("#22FFFFFF");
    private static readonly Color LightLikeActiveColor = (Color)ColorConverter.ConvertFromString("#FFE04372");
    private static readonly Color DarkLikeActiveColor = (Color)ColorConverter.ConvertFromString("#FFFF6A98");
    private static readonly Color LightLikeUnavailableColor = (Color)ColorConverter.ConvertFromString("#FFB89AA5");
    private static readonly Color DarkLikeUnavailableColor = (Color)ColorConverter.ConvertFromString("#FF8D7A84");
    private static readonly Color LightContextMenuBackgroundColor = (Color)ColorConverter.ConvertFromString("#F8FFFFFF");
    private static readonly Color DarkContextMenuBackgroundColor = (Color)ColorConverter.ConvertFromString("#F018181A");
    private static readonly Color LightContextMenuBorderColor = (Color)ColorConverter.ConvertFromString("#220F172A");
    private static readonly Color DarkContextMenuBorderColor = (Color)ColorConverter.ConvertFromString("#403A3A3D");
    private static readonly Color LightContextMenuTextColor = (Color)ColorConverter.ConvertFromString("#FF17212B");
    private static readonly Color DarkContextMenuTextColor = (Color)ColorConverter.ConvertFromString("#FFE8E8EA");
    private static readonly Color LightContextMenuHoverColor = (Color)ColorConverter.ConvertFromString("#0F0F172A");
    private static readonly Color DarkContextMenuHoverColor = (Color)ColorConverter.ConvertFromString("#263A3A3D");
    // Keep hit-testing when docked: fully transparent (alpha=0) can become click-through in layered WPF windows.
    private static readonly Color DockedInteractiveTransparentColor = (Color)ColorConverter.ConvertFromString("#01000000");
    private static readonly Color PlayerPickerPanelDefaultBackgroundColor = (Color)ColorConverter.ConvertFromString("#F018181A");
    private static readonly Color PlayerPickerPanelDockedBackgroundColor = (Color)ColorConverter.ConvertFromString("#F018181A");
    private static readonly Color DockedContextMenuBackgroundColor = (Color)ColorConverter.ConvertFromString("#F018181A");
    private static readonly Color DockedContextMenuHoverColor = (Color)ColorConverter.ConvertFromString("#263A3A3D");
    private static readonly Color DockedContextMenuTextColor = (Color)ColorConverter.ConvertFromString("#FFE8E8EA");
    private static readonly Color LightProgressTrackColor = (Color)ColorConverter.ConvertFromString("#403A3A3D");
    private static readonly Color DarkProgressTrackColor = (Color)ColorConverter.ConvertFromString("#403A3A3D");
    private static readonly Color LightProgressFillColor = (Color)ColorConverter.ConvertFromString("#FFE8E8EA");
    private static readonly Color DarkProgressFillColor = (Color)ColorConverter.ConvertFromString("#FFE8E8EA");
    private static readonly Color LightFloatingProgressBackgroundColor = (Color)ColorConverter.ConvertFromString("#F018181A");
    private static readonly Color DarkFloatingProgressBackgroundColor = (Color)ColorConverter.ConvertFromString("#F018181A");

    private readonly SolidColorBrush _widgetBackgroundBrush = new();
    private Color _baseBackgroundColor;
    private Color _rawContentBackgroundColor;
    private Color _contentBackgroundColor;
    private Color[] _rawGradientBackgroundColors = Array.Empty<Color>();
    private Color _previewBackgroundColor;
    private bool _isDarkTheme;
    private PlayerControlTarget _selectedPlayerTarget = PlayerControlTarget.Auto;
    private bool _isKuGouAutomationRefreshPending;
    private DateTime _lastKuGouSnapshotRefreshUtc = DateTime.MinValue;
    private static readonly TimeSpan KuGouSnapshotRefreshInterval = TimeSpan.FromMilliseconds(2200);
    private const int KuGouAutomationMaxDepth = 8;
    private const int KuGouAutomationMaxNodes = 1800;
    private const int KuGouAutomationTimeoutMs = 1200;
    private IntPtr _kuGouTitleHook = IntPtr.Zero;
    private IntPtr _kuGouHookWindow = IntPtr.Zero;
    private readonly WinEventDelegate _kuGouTitleChangedHandler;
    private bool _isContextMenuOpen;
    // 圆角半径（0~23），默认15，通过右键菜单滑块调节
    private double _widgetCornerRadius = 15d;
    private double _widgetOpacity = 1d;
    private bool _useGradientBackground;
    private GradientBackgroundMode _gradientBackgroundMode = GradientBackgroundMode.Linear;

    private const byte VK_CONTROL = 0x11;
    private const byte VK_MENU = 0x12;
    private const byte VK_SHIFT = 0x10;
    private const uint WM_APPCOMMAND = 0x0319;
    private const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
    private const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
    private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
    private const int FAPPCOMMAND_KEY = 0;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int SW_RESTORE = 9;

    private sealed record FavoriteKeyChord(bool Ctrl, bool Alt, bool Shift, byte Key);
    private enum FavoriteActionKind
    {
        Toggle,
        AddOnly
    }

    private sealed record FavoriteRule(
        string SourceKeyword,
        string[] ProcessNames,
        FavoriteKeyChord[] LikeChords,
        FavoriteKeyChord[]? UnlikeChords,
        FavoriteActionKind ActionKind);

    private sealed record KuGouPlaybackSnapshot(string Title, string Artist, bool IsPlaying, bool IsLiked);
    private sealed record LyricChar(double StartMs, double DurationMs, string Text);
    private sealed record LyricLine(double StartMs, double DurationMs, bool IsLrc, IReadOnlyList<LyricChar> Chars)
    {
        public string Text { get; } = string.Concat(Chars.Select(c => c.Text));
    }

    private enum ProgressBarDisplayMode
    {
        InlineBottomBar,
        FloatingBelow,
        Hidden
    }

    private enum GradientBackgroundMode
    {
        Linear,
        Radial,
        Angle
    }

    private sealed record PlaybackProgressSnapshot(
        double PositionMs,
        double DurationMs,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus)
    {
        public bool IsPlaying => PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    }

    private sealed class WidgetPreferences
    {
        public string ProgressBarDisplayMode { get; set; } = string.Empty;
        public double CornerRadius { get; set; } = 15d;
        public double Opacity { get; set; } = 1d;
        public bool UseGradientBackground { get; set; }
        public string GradientBackgroundMode { get; set; } = "Linear";
    }

    private static readonly FavoriteRule[] FavoriteRules =
    {
        // QQ 音乐常见收藏快捷键（可在 QQ 音乐里自定义）。
        new("qqmusic", new[] { "QQMusic", "QQMusicExternal" }, new[]
        {
            new FavoriteKeyChord(Ctrl: true, Alt: true, Shift: false, Key: (byte)'V')
        }, new[]
        {
            new FavoriteKeyChord(Ctrl: true, Alt: true, Shift: false, Key: (byte)'V'),
            new FavoriteKeyChord(Ctrl: true, Alt: false, Shift: false, Key: (byte)'D')
        }, FavoriteActionKind.AddOnly),
        // 网易云音乐收藏：按用户确认固定使用 Ctrl+L。
        new("cloudmusic", new[] { "cloudmusic", "NeteaseCloudMusic", "cloudmusicreport" }, new[]
        {
            new FavoriteKeyChord(Ctrl: true, Alt: false, Shift: false, Key: (byte)'L')
        }, new[]
        {
            new FavoriteKeyChord(Ctrl: true, Alt: false, Shift: false, Key: (byte)'L'),
            new FavoriteKeyChord(Ctrl: true, Alt: false, Shift: false, Key: (byte)'D')
        }, FavoriteActionKind.AddOnly),
        new("netease", new[] { "cloudmusic", "NeteaseCloudMusic", "cloudmusicreport" }, new[]
        {
            new FavoriteKeyChord(Ctrl: true, Alt: false, Shift: false, Key: (byte)'L')
        }, new[]
        {
            new FavoriteKeyChord(Ctrl: true, Alt: false, Shift: false, Key: (byte)'L'),
            new FavoriteKeyChord(Ctrl: true, Alt: false, Shift: false, Key: (byte)'D')
        }, FavoriteActionKind.AddOnly),
        new("music.163", new[] { "cloudmusic", "NeteaseCloudMusic", "cloudmusicreport" }, new[]
        {
            new FavoriteKeyChord(Ctrl: true, Alt: false, Shift: false, Key: (byte)'L')
        }, new[]
        {
            new FavoriteKeyChord(Ctrl: true, Alt: false, Shift: false, Key: (byte)'L'),
            new FavoriteKeyChord(Ctrl: true, Alt: false, Shift: false, Key: (byte)'D')
        }, FavoriteActionKind.AddOnly),
        new("neteasemusic", new[] { "cloudmusic", "NeteaseCloudMusic", "cloudmusicreport" }, new[]
        {
            new FavoriteKeyChord(Ctrl: true, Alt: false, Shift: false, Key: (byte)'L')
        }, new[]
        {
            new FavoriteKeyChord(Ctrl: true, Alt: false, Shift: false, Key: (byte)'L'),
            new FavoriteKeyChord(Ctrl: true, Alt: false, Shift: false, Key: (byte)'D')
        }, FavoriteActionKind.AddOnly),
        // Spotify 官方桌面快捷键里有 Like/Dislike Song（Alt+Shift+B）。
        new("spotify", new[] { "Spotify" }, new[]
        {
            new FavoriteKeyChord(Ctrl: false, Alt: true, Shift: true, Key: (byte)'B')
        }, null, FavoriteActionKind.Toggle),
        // 汽水音乐：无官方固定收藏快捷键，用 UI Automation 方式（空 ProcessNames 触发 fallback 走 automation）。
        // 主要作用是防止 sourceAppId 包含 luna/soda 时误匹配到其他播放器的规则。
        new("sodamusic", new[] { "SodaMusic" }, Array.Empty<FavoriteKeyChord>(), null, FavoriteActionKind.AddOnly),
        new("luna", new[] { "SodaMusic" }, Array.Empty<FavoriteKeyChord>(), null, FavoriteActionKind.AddOnly),
        new("qishui", new[] { "SodaMusic" }, Array.Empty<FavoriteKeyChord>(), null, FavoriteActionKind.AddOnly),
        // MoeKoe Music：无官方固定收藏快捷键，用 UI Automation 方式。
        new("moekoe", new[] { "MoeKoe", "MoeKoeMusic", "MoeKoe Music" }, Array.Empty<FavoriteKeyChord>(), null, FavoriteActionKind.AddOnly)
    };

    private static readonly string[] KuGouProcessNames =
    {
        "KuGou",
        "KGMusic",
        "ginkgo",
        "KGDaemon"
    };

    private static readonly string[] KuGouLikeAutomationNames =
    {
        "收藏",
        "喜欢",
        "红心",
        "我喜欢",
        "加入收藏"
    };

    private static readonly Dictionary<PlayerControlTarget, string[]> PlayerWindowProcessNames = new()
    {
        [PlayerControlTarget.QQMusic] = new[] { "QQMusic", "QQMusicExternal" },
        [PlayerControlTarget.NeteaseCloudMusic] = new[] { "cloudmusic", "NeteaseCloudMusic", "cloudmusicreport" },
        [PlayerControlTarget.Spotify] = new[] { "Spotify" },
        [PlayerControlTarget.YouTubeMusic] = new[] { "YouTube Music", "YouTubeMusic", "ytmdesktop", "chrome", "msedge", "brave", "vivaldi", "opera" },
        [PlayerControlTarget.KuGouMusic] = KuGouProcessNames,
        [PlayerControlTarget.SodaMusic] = new[] { "SodaMusic" },
        [PlayerControlTarget.MoeKoeMusic] = new[] { "MoeKoe", "MoeKoeMusic", "MoeKoe Music" }
    };

    private static readonly string[] KuGouUnlikeAutomationNames =
    {
        "已收藏",
        "取消收藏",
        "取消喜欢",
        "已喜欢"
    };

    private sealed record TaskbarSlot(Rect Rect, DockSide Side);
    private sealed record SnapTarget(TaskbarSlot Slot, Rect TargetBounds, AppBarEdge Edge, DockedStyle Style, double Distance, bool IsConfirm);
    private enum DockedStyle
    {
        Nano,
        Normal
    }

    private enum DockSide
    {
        Left,
        Right
    }

    private enum TaskbarAlignment
    {
        Left,
        Center
    }

    private enum PlayerControlTarget
    {
        Auto,
        QQMusic,
        NeteaseCloudMusic,
        Spotify,
        YouTubeMusic,
        KuGouMusic,
        SodaMusic,
        MoeKoeMusic
    }

    public MainWindow()
    {
        InitializeComponent();
        _kuGouTitleChangedHandler = OnKuGouWindowTitleChanged;
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
        ApplyTheme(isDarkTheme: DetectSystemDarkTheme(), force: true);
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        WidgetBackgroundHost.Background = _widgetBackgroundBrush;
        WidgetBorder.Opacity = 1d;
        SourcePickerToggleScale.ScaleY = 1d;
        LoadWidgetPreferences();
        ApplyWidgetCornerRadius();
        ApplyWidgetOpacity();
        ApplyProgressBarDisplayMode();
        SetIdleText();
        ApplyLikeState();
        UpdateDefaultPlaybackModeVisual();
        UpdatePlayerTargetButtonsVisual();
        StartLyricTimer();
        InitializeNtePlayer();

        Width = DefaultFreeWidth;
        Height = DefaultFreeHeight;
        Left = GetDefaultFreeLeft();
        Top = DefaultFreeTop;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyTheme(DetectSystemDarkTheme(), force: true);
        
        Width = DefaultFreeWidth;
        Height = DefaultFreeHeight;
        Left = GetDefaultFreeLeft();
        Top = DefaultFreeTop;
        _freeLeft = Left;
        _freeTop = Top;
        
        StartVisibilityGuard();
        await InitializeMediaSessionAsync();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        PositionNearTaskbar();
        EnsureTopmost();
        LocationChanged += (_, _) =>
        {
            if (PlayerPickerPopup.IsOpen)
            {
                RefreshPlayerPickerPopupPlacement();
            }

            if (FloatingProgressPopup.IsOpen)
            {
                RefreshFloatingProgressPopupPlacement();
            }

            if (_nteCoverWindow is { IsVisible: true })
            {
                _nteCoverWindow.PositionBesideOwner(this);
            }
        };
        SizeChanged += (_, _) =>
        {
            if (PlayerPickerPopup.IsOpen)
            {
                RefreshPlayerPickerPopupPlacement();
            }

            if (FloatingProgressPopup.IsOpen)
            {
                CenterFloatingProgressPopup();
            }

            UpdatePlaybackProgressUi(_lastPlaybackProgressSnapshot);
        };
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        StopVisibilityGuard();
        StopLyricTimer();
        DetachSessionHandlers(_session);
        StopKuGouWindowTitleHook();
        TeardownNtePlayer();

        if (_sessionManager is not null)
        {
            _sessionManager.SessionsChanged -= SessionManager_SessionsChanged;
            _sessionManager.CurrentSessionChanged -= SessionManager_CurrentSessionChanged;
        }
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            {
                ApplyTheme(DetectSystemDarkTheme());
            }

            if (_isDocked)
            {
                RefreshDockedTargetForCurrentTaskbarState();
            }
        });
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        // DragMove has its own native move loop; avoid forced cancel here.
    }

    private void MainWindow_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // DragMove completion will return to BeginDragging() finally block.
    }

    private void StartVisibilityGuard()
    {
        if (_visibilityGuardTimer is not null)
        {
            return;
        }

        _visibilityGuardTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _visibilityGuardTimer.Tick += VisibilityGuardTimer_Tick;
        _visibilityGuardTimer.Start();
    }

    private void StopVisibilityGuard()
    {
        if (_visibilityGuardTimer is null)
        {
            return;
        }

        _visibilityGuardTimer.Stop();
        _visibilityGuardTimer.Tick -= VisibilityGuardTimer_Tick;
        _visibilityGuardTimer = null;
    }

    private void VisibilityGuardTimer_Tick(object? sender, EventArgs e)
    {
        if (_isDragging)
        {
            return;
        }

        EnsureVisibleOnAnyScreen();
        if (_isDocked)
        {
            RefreshDockedTargetForCurrentTaskbarState();
        }

        if (DateTime.UtcNow >= _suspendTopmostGuardUntilUtc)
        {
            EnsureTopmost();
        }

        TryScheduleAutoModeSessionRefresh();
    }

    private async Task InitializeMediaSessionAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _sessionManager.SessionsChanged += SessionManager_SessionsChanged;
            _sessionManager.CurrentSessionChanged += SessionManager_CurrentSessionChanged;

            await RefreshCurrentSessionAsync();
        }
        catch
        {
            SetErrorText("无法访问系统媒体会话");
        }
    }

    private async void SessionManager_SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        await Dispatcher.InvokeAsync(async () => await RefreshCurrentSessionAsync(forceRebind: false));
    }

    private async void SessionManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        await Dispatcher.InvokeAsync(async () => await RefreshCurrentSessionAsync(forceRebind: false));
    }

    private void TryScheduleAutoModeSessionRefresh()
    {
        if (_sessionManager is null)
        {
            return;
        }

        var shouldRefreshAutoSession = _selectedPlayerTarget == PlayerControlTarget.Auto;
        var shouldRefreshFallbackState = _session is null
            && (ShouldUseKuGouAutomationFallback()
                || HasActiveFallbackPlayerDisplayState());
        if (!shouldRefreshAutoSession && !shouldRefreshFallbackState)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_isSessionRefreshInProgress || now - _lastSessionRefreshAttemptUtc < AutoModeSessionPollInterval)
        {
            return;
        }

        _lastSessionRefreshAttemptUtc = now;
        _ = RefreshSessionFromGuardAsync();
    }

    private async Task RefreshSessionFromGuardAsync()
    {
        if (_sessionManager is null || _isSessionRefreshInProgress)
        {
            return;
        }

        _isSessionRefreshInProgress = true;
        try
        {
            await RefreshCurrentSessionAsync(forceRebind: false);
        }
        catch
        {
        }
        finally
        {
            _isSessionRefreshInProgress = false;
        }
    }

}

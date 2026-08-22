using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MusicBar;

public partial class MainWindow : Window
{
    // 长按时长：按住任意可隐藏控件超过该时长进入隐藏编辑模式
    private static readonly TimeSpan HideModePressDuration = TimeSpan.FromMilliseconds(600);
    private const double HideBadgeSize = 14d;

    private bool _isHideEditModeActive;
    private DispatcherTimer? _hideModePressTimer;
    private Point _hideModePressStartScreen;
    // 本次长按的意图：false＝进入按钮隐藏编辑模式，true＝切换播放/暂停
    private bool _longPressTogglesPlayback;
    private UIElement? _hideModeCaptureElement;
    private HideModeBadgeAdorner? _hideModeBadgeAdorner;

    private sealed record HideableControl(string Key, string DisplayName, FrameworkElement Element);

    private List<HideableControl> GetHideableControls()
    {
        return new List<HideableControl>
        {
            new("sourcePicker", "音源切换", SourcePickerToggleButton),
            new("albumArt", "专辑封面", AlbumArtHitArea),
            new("prev", "上一首", PrevButton),
            new("playPause", "播放 / 暂停", PlayPauseButton),
            new("next", "下一首", NextButton),
            new("playbackMode", "播放模式", DefaultPlaybackModeButton),
            new("like", "喜欢", LikeButton)
        };
    }

    // ── 长按检测（挂在根 Grid 的 Preview 事件上） ─────────────────────────

    private void Root_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isHideEditModeActive)
        {
            // 点击红色 x 徽章时交给徽章自身处理；点击其他任何位置仅退出编辑模式，
            // 并吞掉本次按下，避免误触发按钮功能或窗口拖拽。
            if (!IsSourceInsideHideBadge(e.OriginalSource as DependencyObject))
            {
                ExitHideEditMode();
                e.Handled = true;
            }

            return;
        }

        // 双击会走 ClickCount==2 的切歌/恢复位置分支，取消长按计时避免误触发播放暂停。
        if (e.ClickCount >= 2)
        {
            StopHideModePressTimer();
            return;
        }

        if (_hideModePressTimer is not null)
        {
            return;
        }

        if (FindHideableControlFromSource(e.OriginalSource as DependencyObject) is not null)
        {
            _longPressTogglesPlayback = false;
            _hideModePressStartScreen = PointToScreen(e.GetPosition(this));
            StartHideModePressTimer();
            return;
        }

        // 简洁类布局（含带频谱）下没有按钮，长按中间区域切换播放/暂停。
        if (IsCompactDisplayLayout && !IsCompactNanoDockedLayout() && IsInCompactCenterZone(e))
        {
            _longPressTogglesPlayback = true;
            _hideModePressStartScreen = PointToScreen(e.GetPosition(this));
            StartHideModePressTimer();
        }
    }

    private bool IsInCompactCenterZone(MouseButtonEventArgs e)
    {
        var position = e.GetPosition(RootSurface);
        var ratio = position.X / Math.Max(1d, ActualWidth);
        return ratio >= 0.3d && ratio <= 0.7d;
    }

    private void Root_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_hideModePressTimer is null || !_hideModePressTimer.IsEnabled)
        {
            return;
        }

        var current = PointToScreen(e.GetPosition(this));
        var dx = current.X - _hideModePressStartScreen.X;
        var dy = current.Y - _hideModePressStartScreen.Y;
        if ((dx * dx) + (dy * dy) > DragStartThreshold * DragStartThreshold)
        {
            StopHideModePressTimer();
        }
    }

    private void Root_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopHideModePressTimer();
    }

    private HideableControl? FindHideableControlFromSource(DependencyObject? source)
    {
        foreach (var control in GetHideableControls())
        {
            if (control.Element.Visibility != Visibility.Visible)
            {
                continue;
            }

            if (IsPointInsideElement(control.Element, source))
            {
                return control;
            }
        }

        return null;
    }

    private void StartHideModePressTimer()
    {
        StopHideModePressTimer();
        _hideModePressTimer = new DispatcherTimer
        {
            Interval = HideModePressDuration
        };
        _hideModePressTimer.Tick += HideModePressTimer_Tick;
        _hideModePressTimer.Start();
    }

    private void StopHideModePressTimer()
    {
        if (_hideModePressTimer is null)
        {
            return;
        }

        _hideModePressTimer.Stop();
        _hideModePressTimer.Tick -= HideModePressTimer_Tick;
        _hideModePressTimer = null;
    }

    private void HideModePressTimer_Tick(object? sender, EventArgs e)
    {
        StopHideModePressTimer();
        if (_longPressTogglesPlayback)
        {
            PlayPauseButton_Click(this, new RoutedEventArgs());
            return;
        }

        EnterHideEditMode();
    }

    // ── 隐藏编辑模式 ──────────────────────────────────────────────────────

    private void EnterHideEditMode()
    {
        if (_isHideEditModeActive)
        {
            return;
        }

        _isHideEditModeActive = true;

        // 捕获鼠标到根 Grid，让被按住的按钮收不到 MouseUp，
        // 从而吞掉这次长按本应触发的 Click / Toggle。
        if (RootSurface.CaptureMouse())
        {
            _hideModeCaptureElement = RootSurface;
        }

        // 音源切换按钮是 ClickMode=Press，MouseDown 时已弹出选择面板，这里撤销。
        if (SourcePickerToggleButton.IsChecked == true)
        {
            SourcePickerToggleButton.IsChecked = false;
            CollapsePlayerPickerOverlay();
        }

        ShowHideBadges();
    }

    private void ExitHideEditMode()
    {
        if (!_isHideEditModeActive)
        {
            return;
        }

        _isHideEditModeActive = false;
        RemoveHideBadges();
    }

    /// <summary>在 Root_MouseLeftButtonUp 中调用：释放长按触发时设置的捕获。</summary>
    private void ReleaseHideModeMouseCapture()
    {
        if (_hideModeCaptureElement is null)
        {
            return;
        }

        if (_hideModeCaptureElement.IsMouseCaptured)
        {
            _hideModeCaptureElement.ReleaseMouseCapture();
        }

        _hideModeCaptureElement = null;
    }

    private void ShowHideBadges()
    {
        RemoveHideBadges();
        var layer = AdornerLayer.GetAdornerLayer(WidgetBorder);
        if (layer is null)
        {
            return;
        }

        var adorner = new HideModeBadgeAdorner(this, WidgetBorder);
        layer.Add(adorner);
        _hideModeBadgeAdorner = adorner;
    }

    private void RemoveHideBadges()
    {
        if (_hideModeBadgeAdorner is null)
        {
            return;
        }

        AdornerLayer.GetAdornerLayer(WidgetBorder)?.Remove(_hideModeBadgeAdorner);
        _hideModeBadgeAdorner = null;
    }

    private void RefreshHideBadges()
    {
        if (_isHideEditModeActive)
        {
            ShowHideBadges();
        }
    }

    private static bool IsSourceInsideHideBadge(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is HideModeBadgeAdorner)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    // ── 隐藏 / 恢复 ───────────────────────────────────────────────────────

    private void HideControlByKey(string key)
    {
        var control = GetHideableControls().FirstOrDefault(c => c.Key == key);
        if (control is null || !_hiddenButtons.Add(key))
        {
            return;
        }

        control.Element.Visibility = Visibility.Collapsed;
        SaveWidgetPreferences();
        RefreshHideBadges();
    }

    private void RestoreControlByKey(string key)
    {
        if (!_hiddenButtons.Remove(key))
        {
            return;
        }

        SaveWidgetPreferences();
        ApplyDockedContentLayout();

        // 恢复菜单保持打开（StaysOpenOnClick）时同步刷新网格，
        // 让已恢复的项立即从列表消失，给出明确反馈。
        if (_isContextMenuOpen)
        {
            PopulateRestoreHiddenButtonsMenu();
        }
    }

    // ── 右键菜单：恢复隐藏按钮（网格列表） ────────────────────────────────

    private void PopulateRestoreHiddenButtonsMenu()
    {
        RestoreHiddenButtonsPanel.Children.Clear();

        var hiddenControls = GetHideableControls()
            .Where(c => _hiddenButtons.Contains(c.Key))
            .ToList();
        RestoreHiddenButtonsMenuItem.Visibility = hiddenControls.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        foreach (var control in hiddenControls)
        {
            var icon = CreateHideableControlIcon(control.Key, 16d);
            if (icon is null)
            {
                continue;
            }

            var button = new Button
            {
                Style = (Style)FindResource("RestoreHiddenButtonStyle"),
                ToolTip = control.DisplayName,
                Content = icon
            };
            button.Click += (_, _) => RestoreControlByKey(control.Key);
            RestoreHiddenButtonsPanel.Children.Add(button);
        }
    }

    private FrameworkElement? CreateHideableControlIcon(string key, double size)
    {
        switch (key)
        {
            case "prev":
                return CreateGeometryIcon((Geometry)FindResource("PrevIconGeometry"), size);
            case "next":
                return CreateGeometryIcon((Geometry)FindResource("NextIconGeometry"), size);
            case "playPause":
                return CreateGeometryIcon((Geometry)FindResource("PlayIconGeometry"), size);
            case "like":
                return CreateGeometryIcon((Geometry)FindResource("HeartIconGeometry"), size);
            case "sourcePicker":
                return CreateGeometryIcon(
                    Geometry.Parse("M854.016 739.328l-313.344-309.248-313.344 309.248q-14.336 14.336-32.768 21.504t-37.376 7.168-36.864-7.168-32.256-21.504q-29.696-28.672-29.696-68.608t29.696-68.608l376.832-373.76q14.336-14.336 34.304-22.528t40.448-9.216 39.424 5.12 31.232 20.48l382.976 379.904q28.672 28.672 28.672 68.608t-28.672 68.608q-14.336 14.336-32.768 21.504t-37.376 7.168-36.864-7.168-32.256-21.504"),
                    size);
            case "albumArt":
                return CreateGeometryIcon(
                    Geometry.Parse("M512 153.6v428.885333a193.024 193.024 0 0 0-93.866667-24.917333 182.101333 182.101333 0 1 0 0 364.032 184.832 184.832 0 0 0 187.733334-182.101333V284.501333h96.768a91.136 91.136 0 0 0 90.965333-91.136A90.965333 90.965333 0 0 0 702.634667 102.4H563.2a51.2 51.2 0 0 0-51.2 51.2z"),
                    size);
            case "playbackMode":
                var source = LoadDefaultPlaybackModeIcon(_defaultPlaybackMode)
                             ?? new BitmapImage(new Uri("pack://application:,,,/Assets/Playback/列表.png", UriKind.Absolute));
                return new Image
                {
                    Source = source,
                    Width = size + 2d,
                    Height = size + 2d,
                    Stretch = Stretch.Uniform
                };
            default:
                return null;
        }
    }

    private Viewbox CreateGeometryIcon(Geometry geometry, double size)
    {
        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = new System.Windows.Shapes.Path
            {
                Data = geometry,
                Stretch = Stretch.Uniform,
                Fill = (Brush)FindResource("ContextMenuTextBrush")
            }
        };
    }

    // ── 红色 x 徽章 Adorner ───────────────────────────────────────────────

    private sealed class HideModeBadgeAdorner : Adorner
    {
        private readonly MainWindow _ownerWindow;
        private readonly List<(Grid Badge, FrameworkElement Target)> _badges = new();

        public HideModeBadgeAdorner(MainWindow ownerWindow, UIElement adornedElement)
            : base(adornedElement)
        {
            _ownerWindow = ownerWindow;

            foreach (var control in ownerWindow.GetHideableControls())
            {
                if (control.Element.Visibility == Visibility.Collapsed)
                {
                    continue;
                }

                var badge = CreateBadge(control.Key);
                _badges.Add((badge, control.Element));
                AddVisualChild(badge);
            }
        }

        protected override int VisualChildrenCount => _badges.Count;

        protected override Visual GetVisualChild(int index)
        {
            return _badges[index].Badge;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (var (badge, target) in _badges)
            {
                if (target.Visibility != Visibility.Visible || target.ActualWidth <= 0d)
                {
                    badge.Arrange(new Rect(0d, 0d, 0d, 0d));
                    continue;
                }

                var transform = target.TransformToVisual(AdornedElement);
                var topRight = transform.Transform(new Point(target.ActualWidth, 0d));
                badge.Arrange(new Rect(
                    topRight.X - HideBadgeSize * 0.42d,
                    topRight.Y - HideBadgeSize * 0.46d,
                    HideBadgeSize,
                    HideBadgeSize));
            }

            return finalSize;
        }

        private Grid CreateBadge(string key)
        {
            var grid = new Grid
            {
                Width = HideBadgeSize,
                Height = HideBadgeSize,
                Cursor = Cursors.Hand,
                Tag = key
            };

            var circle = new System.Windows.Shapes.Ellipse
            {
                Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x4C, 0x55)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1d
            };

            var cross = new TextBlock
            {
                Text = "×",
                Foreground = Brushes.White,
                FontSize = 10d,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0d, -1.5d, 0d, 0d)
            };

            grid.Children.Add(circle);
            grid.Children.Add(cross);
            grid.MouseLeftButtonDown += Badge_MouseLeftButtonDown;
            return grid;
        }

        private void Badge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement { Tag: string key })
            {
                _ownerWindow.HideControlByKey(key);
            }
        }
    }
}

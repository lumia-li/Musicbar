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
    private static string GetPlayerTargetDisplayName(PlayerControlTarget target)
    {
        return target switch
        {
            PlayerControlTarget.QQMusic => "QQ 音乐",
            PlayerControlTarget.NeteaseCloudMusic => "网易云音乐",
            PlayerControlTarget.Spotify => "Spotify",
            PlayerControlTarget.YouTubeMusic => "YouTube Music",
            PlayerControlTarget.KuGouMusic => "酷狗音乐",
            PlayerControlTarget.SodaMusic => "汽水音乐",
            PlayerControlTarget.MoeKoeMusic => "MoeKoe Music",
            _ => "默认"
        };
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        if (PlayerPickerPopup.IsOpen
            && !IsPointInsideElement(PlayerPickerPanel, source)
            && !IsPointInsideElement(SourcePickerToggleButton, source))
        {
            CollapsePlayerPickerOverlay();
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (IsPointInsideInteractiveControl(source))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            RestoreToDefaultPositionAnimated();
            e.Handled = true;
            return;
        }

        _isPointerDown = true;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        if (sender is UIElement dragSurface && dragSurface.CaptureMouse())
        {
            _dragCaptureElement = dragSurface;
        }
        e.Handled = true;
    }

    private void SourcePickerToggleButton_Checked(object sender, RoutedEventArgs e)
    {
        AnimatePickerArrow(expanded: true);
    }

    private void SourcePickerToggleButton_Unchecked(object sender, RoutedEventArgs e)
    {
        AnimatePickerArrow(expanded: false);
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPointerDown || _isDragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // MouseUp may happen outside our window; clear stale drag intent.
            _isPointerDown = false;
            ReleasePendingDragCapture();
            return;
        }

        var current = PointToScreen(e.GetPosition(this));
        var dx = current.X - _dragStartScreen.X;
        var dy = current.Y - _dragStartScreen.Y;
        if ((dx * dx) + (dy * dy) < DragStartThreshold * DragStartThreshold)
        {
            return;
        }

        BeginDragging();
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPointerDown = false;
        ReleasePendingDragCapture();

        if (!_isDragging)
        {
            return;
        }

        EndDragging();
        e.Handled = true;
    }

    private void BeginDragging()
    {
        if (_isDragging)
        {
            return;
        }

        _isPointerDown = false;
        _isDragging = true;
        _wasDockedBeforeDrag = _isDocked;
        ReleasePendingDragCapture();

        if (_isDocked)
        {
            SnapToFreeAtCurrentPosition();
        }

        try
        {
            // Use native move loop for reliability with Alt+Tab / capture edge cases.
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Ignore: left button may already be released.
        }
        finally
        {
            EndDragging();
        }
    }

    private void EndDragging()
    {
        ReleasePendingDragCapture();
        _isDragging = false;

        var rect = new Rect(Left, Top, Width, Height);
        var confirmTarget = ResolveSnapTarget(rect, requireConfirm: true);

        if (confirmTarget is not null && confirmTarget.IsConfirm)
        {
            ApplyDockedTarget(confirmTarget);
        }
        else
        {
            _freeLeft = Left;
            _freeTop = Top;
            ClearPreviewState();
        }

        if (_wasDockedBeforeDrag && !_isDocked)
        {
            EnsureInScreenBounds();
        }

        EnsureVisibleOnAnyScreen();
    }

    private void CancelDragging()
    {
        ReleasePendingDragCapture();
        _isDragging = false;

        if (_wasDockedBeforeDrag)
        {
            DockToTaskbarByPreference();
        }
        else
        {
            _freeLeft = Left;
            _freeTop = Top;
            ClearPreviewState();
            EnsureInScreenBounds();
        }

        EnsureVisibleOnAnyScreen();
    }

    private void ReleasePendingDragCapture()
    {
        if (_dragCaptureElement?.IsMouseCaptured == true)
        {
            _dragCaptureElement.ReleaseMouseCapture();
        }

        _dragCaptureElement = null;
    }

    private void DockToTaskbarByPreference()
    {
        var taskbar = GetTaskbarPlacement();
        var target = FindBestDockingTarget(taskbar);
        if (target is null)
        {
            return;
        }

        ApplyDockedTarget(target with { Distance = 0d, IsConfirm = true });
    }

    private void RefreshDockedTargetForCurrentTaskbarState()
    {
        if (!_isDocked || _isDragging)
        {
            return;
        }

        var taskbar = GetTaskbarPlacement();
        var target = FindBestDockingTarget(taskbar);
        if (target is null)
        {
            return;
        }

        if (Math.Abs(Left - target.TargetBounds.Left) <= 1d &&
            Math.Abs(Top - target.TargetBounds.Top) <= 1d &&
            Math.Abs(Width - target.TargetBounds.Width) <= 1d &&
            Math.Abs(Height - DockedHeight) <= 1d)
        {
            return;
        }

        ApplyDockedTarget(target with { Distance = 0d, IsConfirm = true }, updatePreferredSide: false);
    }

    private void SnapToFreeAtCurrentPosition()
    {
        _isDocked = false;
        _currentDockedStyle = DockedStyle.Normal;
        Width = DefaultFreeWidth;
        Height = DefaultFreeHeight;
        _freeLeft = Left;
        _freeTop = Top;
        ApplyDockedVisualState();
        ClearPreviewState();
        EnsureInScreenBounds();
    }

    private SnapTarget? ResolveSnapTarget(Rect windowRect, bool requireConfirm)
    {
        var taskbar = GetTaskbarPlacement();
        var targets = BuildTaskbarDockTargets(taskbar);
        if (targets.Count == 0)
        {
            return null;
        }

        var intersectsTaskbar = Intersects(windowRect, taskbar.Rect);
        if (requireConfirm && intersectsTaskbar)
        {
            SnapTarget? nearestInTaskbar = null;
            foreach (var target in targets)
            {
                var distance = EdgeDistance(windowRect, target.TargetBounds);
                if (nearestInTaskbar is null || distance < nearestInTaskbar.Distance)
                {
                    nearestInTaskbar = target with { Distance = distance, IsConfirm = true };
                }
            }

            if (nearestInTaskbar is not null)
            {
                return nearestInTaskbar;
            }
        }

        SnapTarget? nearest = null;
        foreach (var target in targets)
        {
            var distance = EdgeDistance(windowRect, target.TargetBounds);
            if (nearest is null || distance < nearest.Distance)
            {
                nearest = target with { Distance = distance, IsConfirm = distance <= SnapConfirmDistance };
            }
        }

        if (nearest is null)
        {
            return null;
        }

        var threshold = requireConfirm ? SnapConfirmDistance : SnapPreviewDistance;
        return nearest.Distance <= threshold ? nearest : null;
    }

    private void ApplyDockedTarget(SnapTarget target, bool updatePreferredSide = true)
    {
        _isDocked = true;
        _currentDockedStyle = target.Style;
        Width = target.TargetBounds.Width;
        Height = DockedHeight;
        if (updatePreferredSide)
        {
            _preferredDockSide = target.Slot.Side;
        }

        Left = target.TargetBounds.Left;
        Top = target.TargetBounds.Top;

        ApplyDockedVisualState();
        if (PlayerPickerPopup.IsOpen)
        {
            RefreshPlayerPickerPopupPlacement();
        }

        EnsureInScreenBounds();
        ClearPreviewState();
        EnsureTopmost();
        EnsureVisibleOnAnyScreen();
    }

    private void ApplyDockedVisualState(bool animateThemeTransition = false)
    {
        WidgetBorder.BorderThickness = new Thickness(0);
        WidgetBorder.BorderBrush = Brushes.Transparent;
        WidgetBackgroundHost.Margin = new Thickness(0);
        WidgetBackgroundHost.CornerRadius = new CornerRadius(_widgetCornerRadius);

        PlayerPickerPanel.BorderBrush = Brushes.Transparent;
        PlayerPickerPanel.BorderThickness = new Thickness(0);

        UpdateBrushResource(
            "ContextMenuBackgroundBrush",
            _isDarkTheme ? DarkContextMenuBackgroundColor : LightContextMenuBackgroundColor,
            animateThemeTransition);
        UpdateBrushResource(
            "ContextMenuBorderBrush",
            _isDarkTheme ? DarkContextMenuBorderColor : LightContextMenuBorderColor,
            animateThemeTransition);
        UpdateBrushResource(
            "ContextMenuTextBrush",
            _isDarkTheme ? DarkContextMenuTextColor : LightContextMenuTextColor,
            animateThemeTransition);
        UpdateBrushResource(
            "ContextMenuHoverBrush",
            _isDarkTheme ? DarkContextMenuHoverColor : LightContextMenuHoverColor,
            animateThemeTransition);

        UpdateProgressBrushResources(animateThemeTransition);
        ApplyDockedContentLayout();
        UpdateMainSpectrumPopupVisibility();
    }

    private void ApplyDockedContentLayout()
    {
        var isCompactNano = _isDocked && _currentDockedStyle == DockedStyle.Nano;
        var hasVisibleLyric = !isCompactNano && !string.IsNullOrWhiteSpace(LyricBaseText.Text);

        SourcePickerToggleButton.Visibility = isCompactNano ? Visibility.Collapsed : Visibility.Visible;
        SongTitleText.Visibility = isCompactNano ? Visibility.Collapsed : Visibility.Visible;
        ArtistText.Visibility = isCompactNano
            ? Visibility.Collapsed
            : hasVisibleLyric ? Visibility.Collapsed : Visibility.Visible;
        LyricLineHost.Visibility = hasVisibleLyric ? Visibility.Visible : Visibility.Collapsed;
        LikeButton.Visibility = isCompactNano ? Visibility.Collapsed : Visibility.Visible;
        InlineProgressHost.Visibility = isCompactNano
            ? Visibility.Collapsed
            : _progressBarDisplayMode == ProgressBarDisplayMode.InlineBottomBar ? Visibility.Visible : Visibility.Collapsed;

        var transportVisibility = isCompactNano ? Visibility.Collapsed : Visibility.Visible;
        PrevButton.Visibility = transportVisibility;
        PlayPauseButton.Visibility = transportVisibility;
        NextButton.Visibility = transportVisibility;
        DefaultPlaybackModeButton.Visibility = transportVisibility;

        RefreshActivePlayerLogoLayout();
    }

    private bool IsCompactNanoDockedLayout()
    {
        if (!_isDocked || _currentDockedStyle != DockedStyle.Nano)
        {
            return false;
        }

        if (Math.Abs(Width - CompactNanoDockedWidth) > 2d)
        {
            return false;
        }

        var taskbar = GetTaskbarPlacement();
        return Intersects(new Rect(Left, Top, Width, Height), taskbar.Rect);
    }

    private void RefreshActivePlayerLogoLayout()
    {
        if (ActivePlayerLogoImage.Source is null)
        {
            ActivePlayerLogoButton.Visibility = Visibility.Collapsed;
            return;
        }

        ActivePlayerLogoButton.Visibility = Visibility.Visible;

        if (!IsCompactNanoDockedLayout())
        {
            Grid.SetColumn(ActivePlayerLogoButton, 3);
            ActivePlayerLogoButton.HorizontalAlignment = HorizontalAlignment.Center;
            ActivePlayerLogoButton.Margin = new Thickness(0);
            return;
        }

        Grid.SetColumn(ActivePlayerLogoButton, 2);
        ActivePlayerLogoButton.HorizontalAlignment = HorizontalAlignment.Left;
        ActivePlayerLogoButton.Margin = new Thickness(12, 0, 0, 0);
    }

    private List<TaskbarSlot> BuildTaskbarSlots(TaskbarPlacement taskbar)
    {
        if (taskbar.Edge is AppBarEdge.Bottom or AppBarEdge.Top)
        {
            return BuildHorizontalTaskbarSlots(taskbar);
        }

        return BuildVerticalTaskbarSlots(taskbar);
    }

    private List<TaskbarSlot> BuildHorizontalTaskbarSlots(TaskbarPlacement taskbar)
    {
        var left = taskbar.Rect.Left + DockedEdgeMargin;
        var right = taskbar.Rect.Right - DockedEdgeMargin;
        if (right <= left)
        {
            return new List<TaskbarSlot>();
        }

        var occupied = GetHorizontalOccupiedRanges(taskbar);
        var free = SubtractRanges(left, right, occupied);
        var slots = new List<TaskbarSlot>();

        foreach (var range in free)
        {
            var width = range.End - range.Start;
            if (width < CompactNanoDockedWidth)
            {
                continue;
            }

            var slotRect = new Rect(range.Start, taskbar.Rect.Top, width, taskbar.Rect.Height);
            var side = DetermineHorizontalSlotSide(slotRect, taskbar.Rect);
            slots.Add(new TaskbarSlot(slotRect, side));
        }

        if (taskbar.Alignment == TaskbarAlignment.Left)
        {
            var taskbarMidpoint = taskbar.Rect.Left + (taskbar.Rect.Width / 2d);
            slots = slots.Where(slot => slot.Rect.Right > taskbarMidpoint).ToList();
        }

        if (slots.Count == 0)
        {
            slots.Add(new TaskbarSlot(
                new Rect(left, taskbar.Rect.Top, Math.Max(0d, right - left), taskbar.Rect.Height),
                DockSide.Right));
        }

        return slots;
    }

    private static DockSide DetermineHorizontalSlotSide(Rect slotRect, Rect taskbarRect)
    {
        var taskbarMidpoint = taskbarRect.Left + (taskbarRect.Width / 2d);
        if (slotRect.Right <= taskbarMidpoint)
        {
            return DockSide.Left;
        }

        if (slotRect.Left >= taskbarMidpoint)
        {
            return DockSide.Right;
        }

        return (slotRect.Left + slotRect.Right) / 2d >= taskbarMidpoint
            ? DockSide.Right
            : DockSide.Left;
    }

    private List<TaskbarSlot> BuildVerticalTaskbarSlots(TaskbarPlacement taskbar)
    {
        var rect = new Rect(
            taskbar.Rect.Left,
            taskbar.Rect.Top + DockedEdgeMargin,
            taskbar.Rect.Width,
            Math.Max(0d, taskbar.Rect.Height - DockedEdgeMargin));
        return new List<TaskbarSlot> { new(rect, taskbar.Edge == AppBarEdge.Right ? DockSide.Right : DockSide.Left) };
    }

    private List<SnapTarget> BuildTaskbarDockTargets(TaskbarPlacement taskbar)
    {
        var targets = new List<SnapTarget>();
        var horizontalTaskbar = taskbar.Edge is AppBarEdge.Bottom or AppBarEdge.Top;
        var slots = BuildTaskbarSlots(taskbar);
        var hasSplitHorizontalSlots = horizontalTaskbar &&
            slots.Any(slot => slot.Side == DockSide.Left) &&
            slots.Any(slot => slot.Side == DockSide.Right);

        foreach (var slot in slots)
        {
            var style = slot.Rect.Width > CompactNanoMaxSlotWidth
                ? DockedStyle.Normal
                : slot.Rect.Width >= CompactNanoDockedWidth ? DockedStyle.Nano : (DockedStyle?)null;

            if (style is null)
            {
                continue;
            }

            var width = style == DockedStyle.Nano
                ? Math.Min(slot.Rect.Width, CompactNanoDockedWidth)
                : Math.Min(slot.Rect.Width, DockedWidth);

            var candidateSides = horizontalTaskbar
                ? taskbar.Alignment == TaskbarAlignment.Left
                    ? new[] { DockSide.Right }
                    : hasSplitHorizontalSlots ? new[] { slot.Side } : new[] { DockSide.Left, DockSide.Right }
                : new[] { slot.Side };

            foreach (var side in candidateSides)
            {
                var centeredLeft = slot.Rect.Left + ((slot.Rect.Width - width) / 2d);
                var availableOffset = Math.Max(0d, (slot.Rect.Width - width) / 2d);
                var clampedSplitBias = Math.Max(-availableOffset, Math.Min(RightSplitDockBias, availableOffset));
                var rightInset = Math.Min(LeftAlignedRightDockInset, Math.Max(0d, slot.Rect.Width - width));
                var targetLeft = horizontalTaskbar
                    ? taskbar.Alignment == TaskbarAlignment.Left
                        ? slot.Rect.Right - width - rightInset
                        : hasSplitHorizontalSlots
                            ? side == DockSide.Right
                                ? centeredLeft + clampedSplitBias
                                : centeredLeft
                            : side == DockSide.Right ? slot.Rect.Right - width : slot.Rect.Left
                    : side == DockSide.Right ? slot.Rect.Right - width : slot.Rect.Left;
                var targetRect = new Rect(
                    targetLeft,
                    slot.Rect.Top + ((slot.Rect.Height - DockedHeight) / 2d),
                    width,
                    DockedHeight);

                var targetSlot = new TaskbarSlot(slot.Rect, side);
                targets.Add(new SnapTarget(targetSlot, targetRect, taskbar.Edge, style.Value, double.MaxValue, false));
            }
        }

        return targets;
    }

    private SnapTarget? FindBestDockingTarget(TaskbarPlacement taskbar)
    {
        var targets = BuildTaskbarDockTargets(taskbar);
        if (targets.Count == 0)
        {
            return null;
        }

        var preferred = targets
            .Where(t => t.Slot.Side == _preferredDockSide)
            .OrderByDescending(t => t.Slot.Rect.Width)
            .FirstOrDefault();

        return preferred ?? targets.OrderByDescending(t => t.Slot.Rect.Width).FirstOrDefault();
    }

    private List<Range1D> GetHorizontalOccupiedRanges(TaskbarPlacement taskbar)
    {
        var ranges = new List<Range1D>();
        var taskbarHwnd = FindWindow("Shell_TrayWnd", null);
        if (taskbarHwnd == IntPtr.Zero)
        {
            return ranges;
        }

        var callback = new EnumWindowsProc((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            if (!GetWindowRect(hwnd, out var rawRect))
            {
                return true;
            }

            var rect = ToDipRect(rawRect);
            if (rect.Width < MinOccupiedWidth)
            {
                return true;
            }

            if (!Intersects(taskbar.Rect, rect))
            {
                return true;
            }

            if (rect.Width > taskbar.Rect.Width * 0.9)
            {
                return true;
            }

            if (rect.Height < taskbar.Rect.Height * 0.4)
            {
                return true;
            }

            var className = GetClassNameSafe(hwnd);
            if (ShouldIgnoreTaskbarClass(className))
            {
                return true;
            }

            var start = Math.Max(taskbar.Rect.Left, rect.Left);
            var end = Math.Min(taskbar.Rect.Right, rect.Right);
            if (end > start)
            {
                ranges.Add(new Range1D(start, end));
            }

            return true;
        });

        EnumChildWindows(taskbarHwnd, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        return MergeRanges(ranges);
    }

    private static bool ShouldIgnoreTaskbarClass(string className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return true;
        }

        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Shell_TrayWnd",
            "ReBarWindow32",
            "WorkerW",
            "SysPager"
        };
        return ignored.Contains(className);
    }

    private static bool Intersects(Rect a, Rect b)
    {
        return !(a.Right <= b.Left || a.Left >= b.Right || a.Bottom <= b.Top || a.Top >= b.Bottom);
    }

    private Rect ToDipRect(RECT rawRect)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return new Rect(rawRect.Left, rawRect.Top, rawRect.Right - rawRect.Left, rawRect.Bottom - rawRect.Top);
        }

        var transform = source.CompositionTarget.TransformFromDevice;
        var topLeft = transform.Transform(new Point(rawRect.Left, rawRect.Top));
        var bottomRight = transform.Transform(new Point(rawRect.Right, rawRect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private static string GetClassNameSafe(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        _ = GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetWindowTextSafe(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static uint GetWindowProcessId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }

    private static List<Range1D> MergeRanges(List<Range1D> ranges)
    {
        if (ranges.Count == 0)
        {
            return ranges;
        }

        var ordered = ranges.OrderBy(r => r.Start).ToList();
        var merged = new List<Range1D> { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var last = merged[^1];
            var current = ordered[i];
            if (current.Start <= last.End + 1d)
            {
                merged[^1] = new Range1D(last.Start, Math.Max(last.End, current.End));
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }

    private static List<Range1D> SubtractRanges(double start, double end, List<Range1D> occupied)
    {
        var free = new List<Range1D>();
        var cursor = start;
        foreach (var range in occupied)
        {
            if (range.End <= cursor)
            {
                continue;
            }

            if (range.Start > cursor)
            {
                free.Add(new Range1D(cursor, Math.Min(range.Start, end)));
            }

            cursor = Math.Max(cursor, range.End);
            if (cursor >= end)
            {
                break;
            }
        }

        if (cursor < end)
        {
            free.Add(new Range1D(cursor, end));
        }

        return free.Where(r => r.End > r.Start).ToList();
    }

    private static double EdgeDistance(Rect a, Rect b)
    {
        var gapX = Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right);
        var gapY = Math.Max(a.Top, b.Top) - Math.Min(a.Bottom, b.Bottom);
        var dx = Math.Max(0d, gapX);
        var dy = Math.Max(0d, gapY);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double GetDistanceToTaskbarSide(Rect windowRect, Rect taskbarRect, DockSide side)
    {
        var windowEdge = side == DockSide.Right ? windowRect.Right : windowRect.Left;
        var taskbarEdge = side == DockSide.Right ? taskbarRect.Right : taskbarRect.Left;
        return Math.Abs(windowEdge - taskbarEdge);
    }

    private void ApplyPreviewState(SnapTarget? target)
    {
        if (target is null)
        {
            ClearPreviewState();
            return;
        }

        _currentPreview = target;
        ApplyWidgetBackground(GetEffectivePreviewBackgroundColor());
        WidgetBorder.Opacity = target.IsConfirm ? 1d : 0.9d;
    }

    private void ClearPreviewState()
    {
        _currentPreview = null;
        ApplyWidgetBackground(GetEffectiveBaseBackgroundColor());
        WidgetBorder.Opacity = 1d;
    }

    private Color GetEffectiveBaseBackgroundColor()
    {
        return _isDocked ? DockedInteractiveTransparentColor : _contentBackgroundColor;
    }

    private Color GetEffectivePreviewBackgroundColor()
    {
        return _isDocked ? DockedInteractiveTransparentColor : _previewBackgroundColor;
    }

    private void ApplyWidgetBackground(Color toColor, bool animateTransition = false)
    {
        // 停靠模式下悬停时，亚克力效果处于激活状态，
        // 拒绝被周期性定时器（VisibilityGuardTimer）等路径重置为停靠透明色，
        // 防止鼠标悬停一会后背景自动消失。
        if (_isDocked && _isHovering && toColor == DockedInteractiveTransparentColor)
        {
            return;
        }

        if (_useGradientBackground
            && !_isDocked
            && _currentPreview is null
            && _rawGradientBackgroundColors.Length >= 2)
        {
            WidgetBackgroundHost.Background = CreateAlbumGradientBrush();
            return;
        }

        AnimateWidgetBackground(toColor, animateTransition);
    }

    private Brush CreateAlbumGradientBrush()
    {
        var colors = GetDisplayGradientColors();
        return _gradientBackgroundMode switch
        {
            GradientBackgroundMode.Radial => CreateRadialAlbumGradientBrush(colors),
            GradientBackgroundMode.Angle => CreateAngleAlbumGradientBrush(colors),
            _ => CreateLinearAlbumGradientBrush(colors)
        };
    }

    private Color[] GetDisplayGradientColors()
    {
        return _rawGradientBackgroundColors
            .Take(3)
            .Select(color => ApplyWidgetOpacityToColor(BlendColors(BoostSaturation(color, 1.12d), _baseBackgroundColor, 0.62d)))
            .ToArray();
    }

    private static LinearGradientBrush CreateLinearAlbumGradientBrush(Color[] colors)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };

        if (colors.Length == 2)
        {
            brush.GradientStops.Add(new GradientStop(colors[0], 0d));
            brush.GradientStops.Add(new GradientStop(colors[1], 1d));
            return brush;
        }

        brush.GradientStops.Add(new GradientStop(colors[0], 0d));
        brush.GradientStops.Add(new GradientStop(colors[1], 0.52d));
        brush.GradientStops.Add(new GradientStop(colors[2], 1d));
        return brush;
    }

    private static RadialGradientBrush CreateRadialAlbumGradientBrush(Color[] colors)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.34, 0.45),
            GradientOrigin = new Point(0.28, 0.38),
            RadiusX = 0.78,
            RadiusY = 1.15
        };

        if (colors.Length == 2)
        {
            brush.GradientStops.Add(new GradientStop(colors[0], 0d));
            brush.GradientStops.Add(new GradientStop(colors[1], 1d));
            return brush;
        }

        brush.GradientStops.Add(new GradientStop(colors[0], 0d));
        brush.GradientStops.Add(new GradientStop(colors[1], 0.58d));
        brush.GradientStops.Add(new GradientStop(colors[2], 1d));
        return brush;
    }

    private static ImageBrush CreateAngleAlbumGradientBrush(Color[] colors)
    {
        const int width = 96;
        const int height = 24;
        const int bytesPerPixel = 4;
        var pixels = new byte[width * height * bytesPerPixel];
        var centerX = (width - 1) / 2d;
        var centerY = (height - 1) / 2d;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var angle = Math.Atan2(y - centerY, x - centerX);
                var t = (angle + Math.PI) / (Math.PI * 2d);
                var color = SampleGradientColor(colors, t);
                var offset = (y * width + x) * bytesPerPixel;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = color.A;
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            pixels,
            width * bytesPerPixel);
        bitmap.Freeze();

        var brush = new ImageBrush(bitmap)
        {
            Stretch = Stretch.Fill
        };
        brush.Freeze();
        return brush;
    }

    private static Color SampleGradientColor(Color[] colors, double offset)
    {
        if (colors.Length <= 1)
        {
            return colors.Length == 1 ? colors[0] : Colors.Transparent;
        }

        if (colors.Length == 2)
        {
            return InterpolateColor(colors[0], colors[1], offset);
        }

        if (offset < 0.5d)
        {
            return InterpolateColor(colors[0], colors[1], offset / 0.5d);
        }

        return InterpolateColor(colors[1], colors[2], (offset - 0.5d) / 0.5d);
    }

    private static Color InterpolateColor(Color from, Color to, double progress)
    {
        progress = Math.Clamp(progress, 0d, 1d);
        return Color.FromArgb(
            (byte)(from.A + (to.A - from.A) * progress),
            (byte)(from.R + (to.R - from.R) * progress),
            (byte)(from.G + (to.G - from.G) * progress),
            (byte)(from.B + (to.B - from.B) * progress));
    }

    private void AnimateWidgetBackground(Color toColor, bool animateTransition)
    {
        if (!ReferenceEquals(WidgetBackgroundHost.Background, _widgetBackgroundBrush))
        {
            _widgetBackgroundBrush.Color = toColor;
            WidgetBackgroundHost.Background = _widgetBackgroundBrush;
        }

        if (_widgetBackgroundBrush.Color == toColor)
        {
            return;
        }

        SetBrushColor(_widgetBackgroundBrush, toColor, animateTransition);
    }

    private void PlayerPickerPopup_Opened(object sender, EventArgs e)
    {
        UpdatePlayerPickerPlacementCallback();
        RefreshPlayerPickerPopupPlacement();
        RefreshFloatingProgressPopupVisibility(_lastPlaybackProgressSnapshot is not null && _lastPlaybackProgressSnapshot.DurationMs > 1000d);
    }

    private void PlayerPickerPopup_Loaded(object sender, RoutedEventArgs e)
    {
        UpdatePlayerPickerPlacementCallback();
    }

    private void PlayerPickerPopup_Closed(object sender, EventArgs e)
    {
        AnimatePickerArrow(false);
        RefreshFloatingProgressPopupVisibility(_lastPlaybackProgressSnapshot is not null && _lastPlaybackProgressSnapshot.DurationMs > 1000d);
    }

    private void AnimatePickerArrow(bool expanded)
    {
        var toValue = expanded ? -1d : 1d;
        var animation = new DoubleAnimation
        {
            To = toValue,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SourcePickerToggleScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void RefreshPlayerPickerPopupPlacement()
    {
        UpdatePlayerPickerPlacementCallback();
        if (Math.Abs(PlayerPickerPopup.HorizontalOffset) < 0.1d)
        {
            PlayerPickerPopup.HorizontalOffset = 1d;
        }

        PlayerPickerPopup.HorizontalOffset = 0d;
    }

    private void UpdatePlayerPickerPlacementCallback()
    {
        var mode = GetPlayerPickerPlacementMode();
        PlayerPickerPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            MenuPlacement.GetPlayerPickerPlacement(popupSize, targetSize, offset, mode);
    }

    private PlayerPickerPlacementMode GetPlayerPickerPlacementMode()
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

}

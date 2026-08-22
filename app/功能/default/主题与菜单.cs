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
    private static readonly TimeSpan ThemeTransitionDuration = TimeSpan.FromMilliseconds(260);

    private void UpdateProgressBrushResources(bool animateTransition = false)
    {
        var progressTrackColor = _isDocked
            ? Color.FromArgb(0x38, DockedContextMenuTextColor.R, DockedContextMenuTextColor.G, DockedContextMenuTextColor.B)
            : _isDarkTheme ? DarkProgressTrackColor : LightProgressTrackColor;
        var progressFillColor = _isDocked
            ? DockedContextMenuTextColor
            : _isDarkTheme ? DarkProgressFillColor : LightProgressFillColor;
        var floatingBackgroundColor = _isDocked
            ? DockedContextMenuBackgroundColor
            : _isDarkTheme ? DarkFloatingProgressBackgroundColor : LightFloatingProgressBackgroundColor;

        UpdateBrushResource("ProgressTrackBrush", progressTrackColor, animateTransition);
        UpdateBrushResource("ProgressFillBrush", progressFillColor, animateTransition);
        UpdateBrushResource("FloatingProgressBackgroundBrush", floatingBackgroundColor, animateTransition);
    }

    private void ApplyTheme(bool isDarkTheme, bool force = false, bool animateTransition = false)
    {
        if (!force && _isDarkTheme == isDarkTheme)
        {
            return;
        }

        _isDarkTheme = isDarkTheme;
        _baseBackgroundColor = isDarkTheme ? DarkBackgroundColor : LightBackgroundColor;
        if (_rawContentBackgroundColor == default)
        {
            _rawContentBackgroundColor = _baseBackgroundColor;
        }

        _previewBackgroundColor = ApplyWidgetOpacityToColor(isDarkTheme ? DarkPreviewColor : LightPreviewColor);
        UpdateAlbumArtBackgroundColor(AlbumArtImage.Source as BitmapImage);

        UpdateBrushResource("WidgetBorderBrush", isDarkTheme ? DarkBorderColor : LightBorderColor, animateTransition);
        UpdateBrushResource("AlbumPlaceholderBrush", isDarkTheme ? DarkAlbumPlaceholderColor : LightAlbumPlaceholderColor, animateTransition);
        UpdateBrushResource("PrimaryTextBrush", isDarkTheme ? DarkPrimaryTextColor : LightPrimaryTextColor, animateTransition);
        UpdateBrushResource("SecondaryTextBrush", isDarkTheme ? DarkSecondaryTextColor : LightSecondaryTextColor, animateTransition);
        UpdateBrushResource("IconBrush", isDarkTheme ? DarkIconColor : LightIconColor, animateTransition);
        UpdateBrushResource("ButtonHoverBrush", isDarkTheme ? DarkButtonHoverColor : LightButtonHoverColor, animateTransition);
        UpdateBrushResource("ButtonPressedBrush", isDarkTheme ? DarkButtonPressedColor : LightButtonPressedColor, animateTransition);
        UpdateBrushResource("LikeActiveBrush", isDarkTheme ? DarkLikeActiveColor : LightLikeActiveColor, animateTransition);
        UpdateBrushResource("LikeUnavailableBrush", isDarkTheme ? DarkLikeUnavailableColor : LightLikeUnavailableColor, animateTransition);

        if (_currentPreview is not null)
        {
            ApplyWidgetBackground(GetEffectivePreviewBackgroundColor(), animateTransition);
            WidgetBorder.Opacity = _currentPreview.IsConfirm ? 1d : 0.9d;
        }
        else
        {
            ApplyWidgetBackground(GetEffectiveBaseBackgroundColor(), animateTransition);
            WidgetBorder.Opacity = 1d;
        }

        ApplyDockedVisualState(animateTransition);
        ApplyLikeState();
        UpdateThemeMenuItems();
    }

    private void CloseProgramMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ── 圆角调节 ──────────────────────────────────────────────────────────

    /// <summary>滑块值变化：实时更新圆角并刷新值文字</summary>
    private void CornerRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CornerRadiusResetButton is null || WidgetBorder is null || WidgetBackgroundHost is null)
        {
            return;
        }

        var radius = (double)(int)e.NewValue;
        _widgetCornerRadius = radius;

        CornerRadiusResetButton.Content = ((int)radius).ToString();

        var cr = new CornerRadius(radius);
        WidgetBorder.CornerRadius = cr;
        WidgetBackgroundHost.CornerRadius = cr;
    }

    private void CornerRadiusResetButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        var animation = new DoubleAnimation
        {
            To = 15d,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => CornerRadiusSlider.Value = 15d;
        CornerRadiusSlider.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, animation);
    }

    /// <summary>应用圆角到主窗口</summary>
    private void ApplyWidgetCornerRadius()
    {
        var cr = new CornerRadius(_widgetCornerRadius);
        WidgetBorder.CornerRadius = cr;
        WidgetBackgroundHost.CornerRadius = cr;
    }

    private void WidgetOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WidgetOpacityValueText is null)
        {
            return;
        }

        var percent = (int)e.NewValue;
        _widgetOpacity = percent / 100d;
        WidgetOpacityValueText.Text = percent.ToString(CultureInfo.InvariantCulture);
        ApplyWidgetOpacity();
    }

    private void ApplyWidgetOpacity()
    {
        _contentBackgroundColor = ApplyWidgetOpacityToColor(_rawContentBackgroundColor == default
            ? _baseBackgroundColor
            : _rawContentBackgroundColor);
        _previewBackgroundColor = ApplyWidgetOpacityToColor(_isDarkTheme ? DarkPreviewColor : LightPreviewColor);
        ApplyWidgetBackground(_currentPreview is not null
            ? GetEffectivePreviewBackgroundColor()
            : GetEffectiveBaseBackgroundColor());
    }

    private void GradientEnabledMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _useGradientBackground = !_useGradientBackground;
        UpdateGradientMenuItems();
        ApplyWidgetBackground(_currentPreview is not null
            ? GetEffectivePreviewBackgroundColor()
            : GetEffectiveBaseBackgroundColor());
    }

    private void ThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string themeName })
        {
            return;
        }

        _useSystemTheme = false;
        ApplyTheme(string.Equals(themeName, "Dark", StringComparison.Ordinal), animateTransition: true);
        UpdateThemeMenuItems();
        SaveWidgetPreferences();
        e.Handled = true;
    }

    private void GradientLinearMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetGradientBackgroundMode(GradientBackgroundMode.Linear);
    }

    private void GradientRadialMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetGradientBackgroundMode(GradientBackgroundMode.Radial);
    }

    private void GradientAngleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetGradientBackgroundMode(GradientBackgroundMode.Angle);
    }

    private void SetGradientBackgroundMode(GradientBackgroundMode mode)
    {
        _gradientBackgroundMode = mode;
        _useGradientBackground = true;
        UpdateGradientMenuItems();
        ApplyWidgetBackground(_currentPreview is not null
            ? GetEffectivePreviewBackgroundColor()
            : GetEffectiveBaseBackgroundColor());
    }

    private void UpdateGradientMenuItems()
    {
        GradientEnabledMenuItem.IsChecked = _useGradientBackground;
        GradientLinearMenuItem.IsChecked = _gradientBackgroundMode == GradientBackgroundMode.Linear;
        GradientRadialMenuItem.IsChecked = _gradientBackgroundMode == GradientBackgroundMode.Radial;
        GradientAngleMenuItem.IsChecked = _gradientBackgroundMode == GradientBackgroundMode.Angle;
    }

    private void UpdateThemeMenuItems()
    {
        if (LightThemeMenuItem is null || DarkThemeMenuItem is null)
        {
            return;
        }

        LightThemeMenuItem.IsChecked = !_isDarkTheme;
        DarkThemeMenuItem.IsChecked = _isDarkTheme;
    }

    private void MainSpectrumEnabledMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _mainSpectrumEnabled = MainSpectrumEnabledMenuItem?.IsChecked == true;
        UpdateMainSpectrumPopupVisibility();
        SaveWidgetPreferences();
    }

    private void MainSpectrumTopPositionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetMainSpectrumPosition(MainSpectrumPosition.Top);
    }

    private void MainSpectrumBottomPositionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetMainSpectrumPosition(MainSpectrumPosition.Bottom);
    }

    private void UpdateMainSpectrumMenuItem()
    {
        if (MainSpectrumEnabledMenuItem is null)
        {
            return;
        }

        var state = MainSpectrumMenuState.Compute(_mainSpectrumEnabled, _mainSpectrumPosition);
        MainSpectrumEnabledMenuItem.IsChecked = state.EnabledChecked;
        MainSpectrumTopPositionMenuItem.IsChecked = state.TopChecked;
        MainSpectrumBottomPositionMenuItem.IsChecked = state.BottomChecked;
    }

    private Color ApplyWidgetOpacityToColor(Color color)
    {
        var alpha = (byte)Math.Clamp((int)Math.Round(255d * _widgetOpacity), 1, 255);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }


    private void CloseProgramMenuItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void CloseProgramMenuItem_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void SubmenuPopup_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.Popup popup)
        {
            popup.CustomPopupPlacementCallback = MenuPlacement.RightSubmenuPlacementCallback;
        }
    }

    private void InlineProgressModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetProgressBarDisplayMode(ProgressBarDisplayMode.InlineBottomBar);
    }

    private void FloatingProgressModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetProgressBarDisplayMode(ProgressBarDisplayMode.FloatingBelow);
    }

    private void HiddenProgressModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetProgressBarDisplayMode(ProgressBarDisplayMode.Hidden);
    }

    private void WidgetContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        ExitHideEditMode();
        CollapsePlayerPickerOverlay();
        _isContextMenuOpen = true;
        _suspendTopmostGuardUntilUtc = DateTime.UtcNow.AddMilliseconds(350);
        UpdateProgressModeMenuItems();
        RefreshFloatingProgressPopupVisibility(_lastPlaybackProgressSnapshot is not null && _lastPlaybackProgressSnapshot.DurationMs > 1000d);
        EnsureTopmost();

        // 打开时重新绑定动态资源，既避免 Popup 首帧闪色，也确保主题切换时
        // 根菜单与子菜单使用同一份最新画刷。
        if (sender is ContextMenu menu)
        {
            menu.SetResourceReference(Control.BackgroundProperty, "ContextMenuBackgroundBrush");
            menu.SetResourceReference(Control.BorderBrushProperty, "ContextMenuBorderBrush");
            menu.SetResourceReference(Control.ForegroundProperty, "ContextMenuTextBrush");
        }

        CornerRadiusSlider.Value = _widgetCornerRadius;
        CornerRadiusResetButton.Content = ((int)_widgetCornerRadius).ToString();
        var opacityPercent = (int)Math.Round(_widgetOpacity * 100d);
        WidgetOpacitySlider.Value = opacityPercent;
        WidgetOpacityValueText.Text = opacityPercent.ToString(CultureInfo.InvariantCulture);
        UpdateGradientMenuItems();
        UpdateThemeMenuItems();
        UpdateMainSpectrumMenuItem();
        UpdateDisplayModeMenuItems();
        UpdateShowLyricsMenuItem();
        PopulateSoundEffectMenu();
        PopulateRestoreHiddenButtonsMenu();
    }

    private void WidgetContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _isContextMenuOpen = false;
        RefreshFloatingProgressPopupVisibility(_lastPlaybackProgressSnapshot is not null && _lastPlaybackProgressSnapshot.DurationMs > 1000d);
        EnsureTopmost();

        // 菜单关闭时保存圆角偏好
        SaveWidgetPreferences();
    }

    // ── 氛围音效 ─────────────────────────────────────────────────────────

    private void PopulateSoundEffectMenu()
    {
        if (SoundEffectEnabledMenuItem is null || SoundEffectSelectorMenuItem is null)
            return;

        SoundEffectEnabledMenuItem.IsChecked = _soundEffectEnabled;

        // 同步音量滑块
        if (SoundEffectVolumeSlider != null)
        {
            var volPercent = (int)Math.Round(_soundEffectPlayer.Volume * 100d);
            if (Math.Abs(SoundEffectVolumeSlider.Value - volPercent) > 0.5d)
            {
                SoundEffectVolumeSlider.Value = volPercent;
            }
        }

        if (SoundEffectVolumeValueText != null)
        {
            SoundEffectVolumeValueText.Text = ((int)Math.Round(_soundEffectPlayer.Volume * 100d)).ToString(CultureInfo.InvariantCulture);
        }

        // 清空并重新填充音效选择列表
        SoundEffectSelectorMenuItem.Items.Clear();

        try
        {
            if (!Directory.Exists(SoundEffectFolder))
            {
                SoundEffectSelectorMenuItem.IsEnabled = false;
                SoundEffectSelectorMenuItem.Header = "音效文件夹不存在";
                return;
            }

            SoundEffectSelectorMenuItem.IsEnabled = true;
            SoundEffectSelectorMenuItem.Header = "选择音效";

            var soundFiles = Directory.GetFiles(SoundEffectFolder, "*.mp3")
                .Concat(Directory.GetFiles(SoundEffectFolder, "*.wav"))
                .OrderBy(f => f)
                .ToList();

            if (soundFiles.Count == 0)
            {
                var emptyItem = new MenuItem
                {
                    Header = "无音效文件",
                    Style = (Style)FindResource("WidgetContextMenuItemStyle"),
                    IsEnabled = false
                };
                SoundEffectSelectorMenuItem.Items.Add(emptyItem);
                return;
            }

            foreach (var filePath in soundFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var displayName = GetDisplayName(fileName);
                var item = new MenuItem
                {
                    Header = displayName,
                    Style = (Style)FindResource("WidgetContextMenuItemStyle"),
                    IsCheckable = true,
                    Tag = filePath
                };
                item.Click += SoundEffectItem_Click;

                // 标记当前选中的音效
                if (_soundEffectEnabled && string.Equals(_currentSoundEffectName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsChecked = true;
                }

                SoundEffectSelectorMenuItem.Items.Add(item);
            }
        }
        catch
        {
            SoundEffectSelectorMenuItem.IsEnabled = false;
            SoundEffectSelectorMenuItem.Header = "无法加载音效";
        }
    }

    private void SoundEffectEnabledMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _soundEffectEnabled = SoundEffectEnabledMenuItem?.IsChecked == true;

        if (_soundEffectEnabled)
        {
            // 如果有上次选择的音效，自动播放
            if (!string.IsNullOrEmpty(_currentSoundEffectName))
            {
                var filePath = Path.Combine(SoundEffectFolder, _currentSoundEffectName + ".mp3");
                if (!File.Exists(filePath))
                {
                    // 尝试 .wav 扩展名
                    filePath = Path.Combine(SoundEffectFolder, _currentSoundEffectName + ".wav");
                }

                if (File.Exists(filePath))
                {
                    _soundEffectPlayer.Play(filePath);
                }
            }
        }
        else
        {
            _soundEffectPlayer.Stop();
        }
    }

    private void SoundEffectItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string filePath)
            return;

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        _currentSoundEffectName = fileName;
        _soundEffectEnabled = true;

        if (SoundEffectEnabledMenuItem != null)
        {
            SoundEffectEnabledMenuItem.IsChecked = true;
        }

        // 更新所有选择项的勾选状态
        foreach (var child in SoundEffectSelectorMenuItem.Items)
        {
            if (child is MenuItem menuItem)
            {
                menuItem.IsChecked = string.Equals(menuItem.Tag as string, filePath, StringComparison.OrdinalIgnoreCase);
            }
        }

        _soundEffectPlayer.Play(filePath);
    }

    /// <summary>
    /// 将文件名转换为友好显示名称
    /// </summary>
    private static string GetDisplayName(string fileName)
    {
        // 移除常见的编辑后缀
        var name = fileName
            .Replace("_edited", "")
            .Replace("-edited", "")
            .Replace("_", " ")
            .Replace("-", " ");

        // 首字母大写
        if (name.Length > 0)
        {
            name = char.ToUpperInvariant(name[0]) + name[1..];
        }

        return name;
    }

    private void SoundEffectVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SoundEffectVolumeValueText is null)
            return;

        var percent = (int)e.NewValue;
        SoundEffectVolumeValueText.Text = percent.ToString(CultureInfo.InvariantCulture);
        _soundEffectPlayer.Volume = percent / 100f;
    }

    private bool DetectSystemDarkTheme()
    {
        try
        {
            using var personalizeKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var appsUseLightTheme = personalizeKey?.GetValue("AppsUseLightTheme");
            if (appsUseLightTheme is int themeValue)
            {
                return themeValue == 0;
            }

            if (appsUseLightTheme is long themeValueLong)
            {
                return themeValueLong == 0;
            }
        }
        catch
        {
            // Fallback below.
        }

        return false;
    }

    private void UpdateBrushResource(string key, Color color, bool animateTransition = false)
    {
        if (Resources[key] is SolidColorBrush brush)
        {
            if (brush.IsFrozen)
            {
                var replacementBrush = new SolidColorBrush(animateTransition ? brush.Color : color);
                if (animateTransition)
                {
                    SetBrushColor(replacementBrush, color, animateTransition: true);
                }

                Resources[key] = replacementBrush;
                return;
            }

            SetBrushColor(brush, color, animateTransition);
            return;
        }

        Resources[key] = new SolidColorBrush(color);
    }

    private static void SetBrushColor(SolidColorBrush brush, Color targetColor, bool animateTransition)
    {
        var currentColor = brush.Color;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        brush.Color = targetColor;

        if (!animateTransition || currentColor == targetColor)
        {
            return;
        }

        brush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new ColorAnimation(currentColor, targetColor, ThemeTransitionDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private SolidColorBrush GetResourceBrush(string key)
    {
        if (Resources[key] is SolidColorBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.White);
    }

    private Point GetCursorScreenDipPosition()
    {
        var p = GetCursorPos();
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return new Point(p.X, p.Y);
        }

        var transform = source.CompositionTarget.TransformFromDevice;
        return transform.Transform(new Point(p.X, p.Y));
    }

    private double GetDefaultFreeLeft()
    {
        return (SystemParameters.PrimaryScreenWidth - DefaultFreeWidth) / 2d;
    }

    private void RestoreToDefaultPositionAnimated()
    {
        _isPointerDown = false;
        _isDragging = false;
        _isDocked = false;
        _currentDockedStyle = DockedStyle.Normal;
        ApplyDockedVisualState();
        ClearPreviewState();

        Width = DefaultFreeWidth;
        Height = DefaultFreeHeight;

        var targetLeft = GetDefaultFreeLeft();
        var targetTop = DefaultFreeTop;
        _freeLeft = targetLeft;
        _freeTop = targetTop;

        AnimateWindowPosition(targetLeft, targetTop);
        AnimateRestoreEmphasis();
        EnsureTopmost();
    }

    private void AnimateWindowPosition(double targetLeft, double targetTop)
    {
        BeginAnimation(Window.LeftProperty, null);
        BeginAnimation(Window.TopProperty, null);

        var fromLeft = Left;
        var fromTop = Top;
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        var leftAnimation = new DoubleAnimation
        {
            From = fromLeft,
            To = targetLeft,
            Duration = RestoreAnimationDuration,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        leftAnimation.Completed += (_, _) =>
        {
            BeginAnimation(Window.LeftProperty, null);
            Left = targetLeft;
        };

        var topAnimation = new DoubleAnimation
        {
            From = fromTop,
            To = targetTop,
            Duration = RestoreAnimationDuration,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        topAnimation.Completed += (_, _) =>
        {
            BeginAnimation(Window.TopProperty, null);
            Top = targetTop;
        };

        BeginAnimation(Window.LeftProperty, leftAnimation);
        BeginAnimation(Window.TopProperty, topAnimation);
    }

}

internal readonly record struct RestoreWindowPositionAnimationPlan(
    bool SetTargetBeforeAnimation,
    bool CommitTargetAfterAnimation)
{
    public static RestoreWindowPositionAnimationPlan Default { get; } = new(
        SetTargetBeforeAnimation: false,
        CommitTargetAfterAnimation: true);
}

public partial class MainWindow : Window
{
    private void AnimateRestoreEmphasis()
    {
        WidgetRestoreScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        WidgetRestoreScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        WidgetBorder.BeginAnimation(OpacityProperty, null);

        WidgetRestoreScaleTransform.ScaleX = 1d;
        WidgetRestoreScaleTransform.ScaleY = 1d;
        WidgetBorder.Opacity = 1d;

        var easing = new BackEase
        {
            Amplitude = 0.28,
            EasingMode = EasingMode.EaseOut
        };

        WidgetRestoreScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            From = 0.965d,
            To = 1d,
            Duration = RestoreEmphasisDuration,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        });

        WidgetRestoreScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            From = 0.965d,
            To = 1d,
            Duration = RestoreEmphasisDuration,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        });

        WidgetBorder.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0.72d,
            To = 1d,
            Duration = RestoreEmphasisDuration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    private static bool IsPointInsideInteractiveControl(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Button)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsPointInsideElement(FrameworkElement target, DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void PositionNearTaskbar()
    {
        const double margin = 6d;
        var taskbar = GetTaskbarPlacement();

        Left = taskbar.Edge switch
        {
            AppBarEdge.Bottom or AppBarEdge.Top => taskbar.Rect.Left + 520,
            AppBarEdge.Left => taskbar.Rect.Right + margin,
            AppBarEdge.Right => taskbar.Rect.Left - Width - margin,
            _ => Left
        };

        Top = taskbar.Edge switch
        {
            AppBarEdge.Bottom => taskbar.Rect.Top - Height - margin,
            AppBarEdge.Top => taskbar.Rect.Bottom + margin,
            AppBarEdge.Left or AppBarEdge.Right => taskbar.Rect.Top + 180,
            _ => Top
        };

        EnsureInScreenBounds();
    }

    private void EnsureTopmost()
    {
        if (_isContextMenuOpen)
        {
            return;
        }

        if (!Topmost)
        {
            Topmost = true;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING;
        _ = SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
    }

    private void EnsureInScreenBounds()
    {
        var area = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        if (Left + Width > area.Right)
        {
            Left = area.Right - Width - 8;
        }

        if (Left < area.Left)
        {
            Left = area.Left + 8;
        }

        if (Top + Height > area.Bottom)
        {
            Top = area.Bottom - Height - 8;
        }

        if (Top < area.Top)
        {
            Top = area.Top + 8;
        }
    }

    private void EnsureVisibleOnAnyScreen()
    {
        var current = new Rect(Left, Top, Width, Height);
        if (Width <= 0 || Height <= 0 || double.IsNaN(Left) || double.IsNaN(Top))
        {
            Width = 430;
            Height = 46;
            PositionNearTaskbar();
            Show();
            return;
        }

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        if (current.Right < virtualLeft + 10
            || current.Left > virtualRight - 10
            || current.Bottom < virtualTop + 10
            || current.Top > virtualBottom - 10)
        {
            Width = 430;
            Height = 46;
            PositionNearTaskbar();
            Show();
            EnsureTopmost();
            return;
        }

        foreach (var area in GetAllMonitorBoundsDip())
        {
            var overlapX = Math.Max(0d, Math.Min(current.Right, area.Right) - Math.Max(current.Left, area.Left));
            var overlapY = Math.Max(0d, Math.Min(current.Bottom, area.Bottom) - Math.Max(current.Top, area.Top));
            if (overlapX >= 20 && overlapY >= 20)
            {
                if (!IsVisible)
                {
                    Show();
                }

                return;
            }
        }

        Width = 430;
        Height = 46;
        PositionNearTaskbar();
        Show();
        EnsureTopmost();
    }

    private List<Rect> GetAllMonitorBoundsDip()
    {
        var areas = new List<Rect>();
        var source = PresentationSource.FromVisual(this);
        var hasTransform = source?.CompositionTarget is not null;
        var fromDevice = hasTransform
            ? source!.CompositionTarget!.TransformFromDevice
            : Matrix.Identity;

        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr _, ref RECT __, IntPtr ___) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref mi))
            {
                return true;
            }

            var monitor = mi.rcMonitor;
            if (!hasTransform)
            {
                areas.Add(new Rect(monitor.Left, monitor.Top, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top));
                return true;
            }

            var topLeft = fromDevice.Transform(new Point(monitor.Left, monitor.Top));
            var bottomRight = fromDevice.Transform(new Point(monitor.Right, monitor.Bottom));
            areas.Add(new Rect(topLeft, bottomRight));
            return true;
        };

        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        return areas;
    }

    private TaskbarPlacement GetTaskbarPlacement()
    {
        var alignment = DetectTaskbarAlignment();
        var appBarData = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>()
        };
        var result = SHAppBarMessage(ABM_GETTASKBARPOS, ref appBarData);

        if (result == IntPtr.Zero)
        {
            return GetTaskbarPlacementFallback();
        }

        var rawRect = new Rect(
            appBarData.rc.Left,
            appBarData.rc.Top,
            appBarData.rc.Right - appBarData.rc.Left,
            appBarData.rc.Bottom - appBarData.rc.Top);

        if (rawRect.Width > SystemParameters.PrimaryScreenWidth * 0.95 &&
            rawRect.Height > SystemParameters.PrimaryScreenHeight * 0.95)
        {
            return GetTaskbarPlacementFallback();
        }

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return new TaskbarPlacement(rawRect, (AppBarEdge)appBarData.uEdge, alignment);
        }

        var transform = source.CompositionTarget.TransformFromDevice;
        var topLeft = transform.Transform(new Point(rawRect.Left, rawRect.Top));
        var bottomRight = transform.Transform(new Point(rawRect.Right, rawRect.Bottom));
        var dipRect = new Rect(topLeft, bottomRight);

        return new TaskbarPlacement(dipRect, (AppBarEdge)appBarData.uEdge, alignment);
    }

    private TaskbarPlacement GetTaskbarPlacementFallback()
    {
        var alignment = DetectTaskbarAlignment();
        var taskbarHwnd = FindWindow("Shell_TrayWnd", null);
        if (taskbarHwnd == IntPtr.Zero || !GetWindowRect(taskbarHwnd, out var taskbarRect))
        {
            return new TaskbarPlacement(new Rect(0, 0, SystemParameters.PrimaryScreenWidth, 48), AppBarEdge.Bottom, alignment);
        }

        var rect = new Rect(
            taskbarRect.Left,
            taskbarRect.Top,
            taskbarRect.Right - taskbarRect.Left,
            taskbarRect.Bottom - taskbarRect.Top);

        var edge = AppBarEdge.Bottom;
        if (rect.Top <= 2 && rect.Height < rect.Width) edge = AppBarEdge.Top;
        else if (rect.Left <= 2 && rect.Height > rect.Width) edge = AppBarEdge.Left;
        else if (Math.Abs(rect.Right - SystemParameters.PrimaryScreenWidth) < 3 && rect.Height > rect.Width) edge = AppBarEdge.Right;

        return new TaskbarPlacement(rect, edge, alignment);
    }
}

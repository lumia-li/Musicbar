using System;
using System.Windows;

namespace MusicBar;

internal enum WidgetDisplayMode
{
    Default,
    Compact,
    CompactWithSpectrum
}

public partial class MainWindow : Window
{
    private void DefaultDisplayModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetDisplayMode(WidgetDisplayMode.Default);
    }

    private void CompactDisplayModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetDisplayMode(WidgetDisplayMode.Compact);
    }

    private void CompactWithSpectrumDisplayModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetDisplayMode(WidgetDisplayMode.CompactWithSpectrum);
    }

    private void SetDisplayMode(WidgetDisplayMode mode)
    {
        _displayMode = mode;
        UpdateDisplayModeMenuItems();
        // ApplyDockedVisualState 内部会调用 ApplyDockedContentLayout 与
        // UpdateMainSpectrumPopupVisibility，一次性刷新全部内容布局。
        ApplyDockedVisualState();
        RefreshFloatingProgressPopupVisibility(_lastPlaybackProgressSnapshot is not null
            && _lastPlaybackProgressSnapshot.DurationMs > 1000d);
        SaveWidgetPreferences();
    }

    private void UpdateDisplayModeMenuItems()
    {
        if (DefaultDisplayModeMenuItem is null || CompactDisplayModeMenuItem is null)
        {
            return;
        }

        DefaultDisplayModeMenuItem.IsChecked = _displayMode == WidgetDisplayMode.Default;
        CompactDisplayModeMenuItem.IsChecked = _displayMode == WidgetDisplayMode.Compact;
        CompactWithSpectrumDisplayModeMenuItem.IsChecked = _displayMode == WidgetDisplayMode.CompactWithSpectrum;
    }

    private static WidgetDisplayMode ParseDisplayMode(string? rawValue)
    {
        return Enum.TryParse<WidgetDisplayMode>(rawValue, ignoreCase: true, out var mode)
            ? mode
            : WidgetDisplayMode.Default;
    }
}

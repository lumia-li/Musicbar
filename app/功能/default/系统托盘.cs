using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SD = System.Drawing;
using SWF = System.Windows.Forms;

namespace MusicBar;

public partial class MainWindow : Window
{
    private SWF.NotifyIcon? _trayIcon;
    private ContextMenu? _trayContextMenu;
    private MenuItem? _trayLightThemeMenuItem;
    private MenuItem? _trayDarkThemeMenuItem;
    private SD.Bitmap? _trayIconBitmap;

    private void InitializeTrayIcon()
    {
        var icon = LoadTrayIcon();

        _trayContextMenu = CreateTrayContextMenu();

        _trayIcon = new SWF.NotifyIcon
        {
            Icon = icon,
            Text = "MusicBar",
            Visible = true
        };
        _trayIcon.MouseClick += TrayIcon_MouseClick;
        _trayIcon.DoubleClick += TrayIcon_DoubleClick;
    }

    private SD.Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/TrayIcon/tray.png", UriKind.Absolute);
            var resource = Application.GetResourceStream(uri);
            if (resource is null)
            {
                return SD.SystemIcons.Application;
            }

            using var stream = resource.Stream;
            using var original = new SD.Bitmap(stream);
            var resized = CropAndResize(original, 32, 32);
            _trayIconBitmap?.Dispose();
            _trayIconBitmap = resized;
            return SD.Icon.FromHandle(resized.GetHicon());
        }
        catch
        {
            return SD.SystemIcons.Application;
        }
    }

    private static SD.Bitmap CropAndResize(SD.Bitmap source, int targetWidth, int targetHeight)
    {
        var squareSize = Math.Min(source.Width, source.Height);
        var x = (source.Width - squareSize) / 2;
        var y = (source.Height - squareSize) / 2;

        using var cropped = new SD.Bitmap(squareSize, squareSize);
        using (var cropGraphics = SD.Graphics.FromImage(cropped))
        {
            cropGraphics.Clear(SD.Color.Transparent);
            cropGraphics.DrawImage(
                source,
                new SD.Rectangle(0, 0, squareSize, squareSize),
                new SD.Rectangle(x, y, squareSize, squareSize),
                SD.GraphicsUnit.Pixel);
        }

        var resized = new SD.Bitmap(targetWidth, targetHeight, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var resizeGraphics = SD.Graphics.FromImage(resized))
        {
            resizeGraphics.SmoothingMode = SD.Drawing2D.SmoothingMode.HighQuality;
            resizeGraphics.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
            resizeGraphics.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.Half;
            resizeGraphics.Clear(SD.Color.White);
            var padding = targetWidth / 8;
            var drawRect = new SD.Rectangle(padding, padding, targetWidth - padding * 2, targetHeight - padding * 2);
            resizeGraphics.DrawImage(cropped, drawRect);
        }

        return resized;
    }

    private ContextMenu CreateTrayContextMenu()
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("WidgetContextMenuStyle"),
            Resources = this.Resources
        };
        menu.SetResourceReference(Control.BackgroundProperty, "ContextMenuBackgroundBrush");
        menu.SetResourceReference(Control.BorderBrushProperty, "ContextMenuBorderBrush");
        menu.SetResourceReference(Control.ForegroundProperty, "ContextMenuTextBrush");

        if (TrayMenuHost != null)
        {
            TrayMenuHost.ContextMenu = menu;
        }

        var playbackMenu = new MenuItem
        {
            Header = "播放控制",
            Style = (Style)FindResource("WidgetContextMenuItemStyle")
        };
        playbackMenu.Items.Add(CreateTrayMenuItem("上一首", PrevButton_Click));
        playbackMenu.Items.Add(CreateTrayMenuItem("播放 / 暂停", PlayPauseButton_Click));
        playbackMenu.Items.Add(CreateTrayMenuItem("下一首", NextButton_Click));
        menu.Items.Add(playbackMenu);

        var playerMenu = new MenuItem
        {
            Header = "播放器",
            Style = (Style)FindResource("WidgetContextMenuItemStyle")
        };
        playerMenu.Items.Add(CreateTrayMenuItem("默认", PlayerTargetAutoButton_Click, staysOpenOnClick: true));
        playerMenu.Items.Add(CreateTrayMenuItem("QQ 音乐", PlayerTargetQqButton_Click, staysOpenOnClick: true));
        playerMenu.Items.Add(CreateTrayMenuItem("网易云音乐", PlayerTargetNeteaseButton_Click, staysOpenOnClick: true));
        playerMenu.Items.Add(CreateTrayMenuItem("Spotify", PlayerTargetSpotifyButton_Click, staysOpenOnClick: true));
        playerMenu.Items.Add(CreateTrayMenuItem("酷狗音乐", PlayerTargetKugouButton_Click, staysOpenOnClick: true));
        playerMenu.Items.Add(CreateTrayMenuItem("汽水音乐", PlayerTargetSodaButton_Click, staysOpenOnClick: true));
        menu.Items.Add(playerMenu);

        var themeMenu = new MenuItem
        {
            Header = "颜色主题",
            Style = (Style)FindResource("WidgetContextMenuItemStyle")
        };
        _trayLightThemeMenuItem = CreateTrayMenuItem("浅色", (_, _) => SetTrayTheme(false), staysOpenOnClick: true);
        _trayLightThemeMenuItem.IsCheckable = true;
        _trayDarkThemeMenuItem = CreateTrayMenuItem("深色", (_, _) => SetTrayTheme(true), staysOpenOnClick: true);
        _trayDarkThemeMenuItem.IsCheckable = true;
        themeMenu.Items.Add(_trayLightThemeMenuItem);
        themeMenu.Items.Add(_trayDarkThemeMenuItem);
        menu.Items.Add(themeMenu);

        var exitItem = new MenuItem
        {
            Header = "关闭程序",
            Style = (Style)FindResource("WidgetContextMenuItemStyle"),
            Foreground = new SolidColorBrush(Colors.IndianRed)
        };
        exitItem.Click += (_, _) => Close();
        menu.Items.Add(exitItem);

        UpdateTrayThemeMenuItems();
        return menu;
    }

    private MenuItem CreateTrayMenuItem(string header, RoutedEventHandler clickHandler, bool staysOpenOnClick = false)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = (Style)FindResource("WidgetContextMenuItemStyle"),
            StaysOpenOnClick = staysOpenOnClick
        };
        item.Click += clickHandler;
        return item;
    }

    private void SetTrayTheme(bool isDark)
    {
        _useSystemTheme = false;
        _isDarkTheme = isDark;
        ApplyTheme(isDark, force: true);
        SaveWidgetPreferences();
        UpdateTrayThemeMenuItems();
    }

    private void UpdateTrayThemeMenuItems()
    {
        if (_trayLightThemeMenuItem != null)
        {
            _trayLightThemeMenuItem.IsChecked = !_useSystemTheme && !_isDarkTheme;
        }

        if (_trayDarkThemeMenuItem != null)
        {
            _trayDarkThemeMenuItem.IsChecked = !_useSystemTheme && _isDarkTheme;
        }
    }

    private void TrayIcon_MouseClick(object? sender, SWF.MouseEventArgs e)
    {
        if (e.Button == SWF.MouseButtons.Right)
        {
            Dispatcher.Invoke(() =>
            {
                if (!IsVisible)
                {
                    Show();
                }

                Activate();

                if (_trayContextMenu != null)
                {
                    _trayContextMenu.SetResourceReference(Control.BackgroundProperty, "ContextMenuBackgroundBrush");
                    _trayContextMenu.SetResourceReference(Control.BorderBrushProperty, "ContextMenuBorderBrush");
                    _trayContextMenu.SetResourceReference(Control.ForegroundProperty, "ContextMenuTextBrush");
                    _trayContextMenu.IsOpen = true;
                }
            });
        }
    }

    private void TrayIcon_DoubleClick(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (IsVisible)
            {
                Hide();
            }
            else
            {
                Show();
                Activate();
            }
        });
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon != null)
        {
            _trayIcon.MouseClick -= TrayIcon_MouseClick;
            _trayIcon.DoubleClick -= TrayIcon_DoubleClick;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayIconBitmap?.Dispose();
        _trayIconBitmap = null;
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace MusicBar;

internal sealed class NteCoverWindow : Window
{
    private readonly Image _coverImage;
    private readonly TextBlock _placeholder;
    private readonly Border _root;

    public NteCoverWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 130;
        Height = 130;

        _coverImage = new Image { Stretch = Stretch.UniformToFill };
        _placeholder = new TextBlock
        {
            Text = "OST",
            Foreground = new SolidColorBrush(Color.FromRgb(255, 0, 127)),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new Grid();
        content.Children.Add(_coverImage);
        content.Children.Add(_placeholder);

        _root = new Border
        {
            Width = 130,
            Height = 130,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(238, 26, 28, 36)),
            ClipToBounds = true,
            Child = content,
            Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 3,
                Direction = 315,
                Opacity = 0.28,
                Color = Color.FromRgb(0, 0, 0)
            }
        };

        Content = _root;
    }

    public void UpdateCover(ImageSource? source)
    {
        if (source != null)
        {
            _coverImage.Source = source;
            _placeholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            _coverImage.Source = null;
            _placeholder.Visibility = Visibility.Visible;
        }
    }

    public void PositionBesideOwner(Window owner)
    {
        Left = owner.Left - Width - 6;
        Top = owner.Top + owner.Height - Height;
    }
}

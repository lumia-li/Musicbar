using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MusicBar.功能;

namespace MusicBar;

partial class MainWindow
{
    private const string NteIconHeart = "爱心.png";
    private const string NteIconHeartFilled = "爱心（填充）.png";
    private const string NteIconPause = "暂停.png";
    private const string NteIconContinue = "继续.png";
    private const string NteIconListLoop = "列表循环.png";
    private const string NteIconSingleLoop = "单曲循环.png";
    private const string NteIconRandom = "随机播放.png";

    private readonly NteMusicLibrary _nteLibrary = new();
    private readonly NteAudioPlaybackEngine _ntePlayer = new();
    private readonly NtePlaybackAdjustment _ntePlaybackAdjustment = new();
    private readonly DispatcherTimer _ntePlaybackTimer = new();
    private readonly DispatcherTimer _nteSpectrumTimer = new();
    private readonly DispatcherTimer _nteTitleScrollTimer = new();
    private readonly Random _nteRandom = new();

    private List<NteMusicSong> _nteCurrentQueue = new();
    private NteMusicSong? _nteCurrentSong;
    private int _nteCurrentIndex = -1;
    private bool _nteIsPlaying;
    private NtePlayMode _ntePlayMode = NtePlayMode.ListLoop;
    private bool _ntePlayerInitialized;
    private bool _nteDetachedLayoutEnabled;
    private NteCoverWindow? _nteCoverWindow;
    private double _nteTitleScrollOffset;
    private DateTime _nteTitleScrollStartedUtc = DateTime.UtcNow;

    private void InitializeNtePlayer()
    {
        if (!_ntePlayerInitialized)
        {
            _ntePlayer.PlaybackEnded += (_, _) => Dispatcher.BeginInvoke(NtePlayAfterCurrentSongEnds);
            _ntePlaybackTimer.Interval = TimeSpan.FromMilliseconds(300);
            _ntePlaybackTimer.Tick += (_, _) => NteUpdatePlaybackVisuals();
            _ntePlaybackTimer.Start();

            _nteSpectrumTimer.Interval = TimeSpan.FromMilliseconds(70);
            _nteSpectrumTimer.Tick += (_, _) => NteRenderSpectrum();
            _nteSpectrumTimer.Start();

            _nteTitleScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
            _nteTitleScrollTimer.Tick += (_, _) => NteUpdateTitleScroll();
            _nteTitleScrollTimer.Start();

            _ntePlayerInitialized = true;
        }

        NteRefreshQueue();
        NteRefreshList();
        NteRenderSpectrum();
        ApplyNteDetachedLayoutVisuals(NtePlayerLayoutState.Compute(false, _nteDetachedLayoutEnabled, _isDocked), animate: false);
    }

    private void NteLikeCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_nteCurrentSong is null)
        {
            return;
        }

        _nteLibrary.ToggleFavorite(_nteCurrentSong.Id);

        if (NteLikeCurrentImage != null)
        {
            var updated = _nteLibrary.FavoriteSongIds.Contains(_nteCurrentSong.Id);
            NteLikeCurrentImage.Source = NteLoadResourceImage(updated ? NteIconHeartFilled : NteIconHeart);
            NteLikeCurrentImage.Opacity = updated ? 1.0 : 0.6;
        }

        NteRefreshQueue();
        NteRefreshList();
    }

    private void NteImportFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择音乐文件夹（支持子目录，可多选）",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folders = dialog.FolderNames?.Length > 0 ? dialog.FolderNames : new[] { dialog.FolderName };
        var previousCount = _nteLibrary.Songs.Count;
        _nteLibrary.ImportFolders(folders);
        var added = _nteLibrary.Songs.Count - previousCount;

        NteRefreshQueue();
        NteRefreshList();

        if (NteTitleText != null)
        {
            if (added > 0)
            {
                NteTitleText.Text = $"已新增 {added} 首歌曲，点击播放";
                NteResetTitleScroll();
            }
            else if (_nteCurrentSong is null)
            {
                NteTitleText.Text = _nteLibrary.Songs.Count > 0 ? "列表已更新，点击播放" : "所选文件夹内没有可识别的歌曲";
                NteResetTitleScroll();
            }
        }
    }

    private void NteImportFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择音乐文件（可多选）",
            Multiselect = true,
            Filter = "音乐文件|*.mp3;*.wav;*.ogg;*.flac;*.m4a;*.aac;*.wma|所有文件|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var previousCount = _nteLibrary.Songs.Count;
        _nteLibrary.ImportFiles(dialog.FileNames);
        var added = _nteLibrary.Songs.Count - previousCount;

        NteRefreshQueue();
        NteRefreshList();

        if (NteTitleText != null && added > 0)
        {
            NteTitleText.Text = $"已新增 {added} 首歌曲，点击播放";
            NteResetTitleScroll();
        }
    }

    private void NteToggleListButton_Click(object sender, RoutedEventArgs e)
    {
        var show = NtePlaylistGrid?.Visibility != Visibility.Visible;
        ApplyNteExpandedLayout(show);
    }

    private void NteDetachedLayoutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _nteDetachedLayoutEnabled = NteDetachedLayoutMenuItem?.IsChecked == true;
        var expanded = NtePlaylistGrid?.Visibility == Visibility.Visible;
        ApplyNteExpandedLayout(expanded, animateDetachedChange: true);
        NteRefreshTitleLayout();
    }

    private void NtePlaybackRateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider slider)
        {
            return;
        }

        var previous = _ntePlaybackAdjustment.PlaybackRate;
        var changed = _ntePlaybackAdjustment.SetPlaybackRate(e.NewValue);
        var snapped = _ntePlaybackAdjustment.PlaybackRate;
        if (Math.Abs(slider.Value - snapped) > 0.0001d)
        {
            slider.Value = snapped;
        }

        if (changed)
        {
            ApplyNtePlaybackAdjustment();
            if (Math.Abs(e.NewValue - snapped) > 0.0001d || Math.Abs(previous - snapped) >= 0.249d)
            {
                AnimateNtePlaybackAdjustmentText(NtePlaybackRateValueText);
            }
        }
    }

    private void NtePitchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider slider)
        {
            return;
        }

        var previous = _ntePlaybackAdjustment.PitchSemitones;
        var changed = _ntePlaybackAdjustment.SetPitchSemitones(e.NewValue);
        var snapped = _ntePlaybackAdjustment.PitchSemitones;
        if (Math.Abs(slider.Value - snapped) > 0.0001d)
        {
            slider.Value = snapped;
        }

        if (changed)
        {
            ApplyNtePlaybackAdjustment();
            if (Math.Abs(e.NewValue - snapped) > 0.0001d || Math.Abs(previous - snapped) >= 0.95d)
            {
                AnimateNtePlaybackAdjustmentText(NtePitchValueText);
            }
        }
    }

    private void NtePlaybackAdjustmentResetButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _ntePlaybackAdjustment.Reset();
        ApplyNtePlaybackAdjustment();
        AnimateNtePlaybackAdjustmentText(NtePlaybackRateValueText);
        AnimateNtePlaybackAdjustmentText(NtePitchValueText);
    }

    private void ApplyNteDefaultPositionLayout()
    {
        BeginNteDetachedAnimations(null);
        var expanded = NtePlaylistGrid?.Visibility == Visibility.Visible;
        ApplyNteExpandedLayout(expanded, animateDetachedChange: false);
        NteRefreshTitleLayout();
    }

    private void ApplyNteExpandedLayout(bool expanded, bool animateDetachedChange = false)
    {
        var wasDocked = _isDocked;
        var oldHeight = Height;
        var state = NtePlayerLayoutState.Compute(expanded, _nteDetachedLayoutEnabled, wasDocked);

        if (NtePlaylistGrid != null)
        {
            NtePlaylistGrid.Visibility = state.PlaylistVisible ? Visibility.Visible : Visibility.Collapsed;
        }
        if (NteCoverPanel != null)
        {
            NteCoverPanel.Visibility = state.CoverVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        if (wasDocked)
        {
            Height = state.WindowHeight;
            Top += oldHeight - Height;
            ApplyNtePlaylistDirection(expanded);
            ApplyNteDetachedLayoutVisuals(state, animate: animateDetachedChange);
            ApplyNteDockedContentLayout();
            EnsureTopmost();
            return;
        }

        ApplyNtePlaylistDirection(false);
        Height = state.WindowHeight;
        ApplyNteDetachedLayoutVisuals(state, animate: animateDetachedChange || state.DetachedActive);
        EnsureInScreenBounds();
    }

    private void ApplyNteDetachedLayoutVisuals(NtePlayerLayoutState state, bool animate)
    {
        if (NteToolbarBorder != null)
        {
            NteToolbarBorder.Background = Brushes.Transparent;
            NteToolbarBorder.BorderThickness = new Thickness(0);
            NteToolbarBorder.BorderBrush = Brushes.Transparent;
            NteToolbarBorder.Effect = null;
            NteToolbarBorder.Margin = new Thickness(0);
            Grid.SetColumn(NteToolbarBorder, 0);
            Grid.SetColumnSpan(NteToolbarBorder, 3);
        }

        if (NteStatusBar != null)
        {
            NteStatusBar.Visibility = state.StatusVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        if (NteCoverPanel != null)
        {
            NteCoverPanel.Width = 110d;
            NteCoverPanel.Height = 110d;
            NteCoverPanel.CornerRadius = new CornerRadius(14);
            NteCoverPanel.Background = new SolidColorBrush(Color.FromRgb(26, 28, 36));
            NteCoverPanel.Effect = null;
            Grid.SetColumn(NteCoverPanel, 0);
            NteCoverPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            NteCoverPanel.VerticalAlignment = VerticalAlignment.Stretch;
            NteCoverPanel.Margin = new Thickness(0, 6, 10, 0);
            Panel.SetZIndex(NteCoverPanel, 0);

            NteCoverPanel.Visibility = state.DetachedActive ? Visibility.Collapsed : (state.CoverVisible ? Visibility.Visible : Visibility.Collapsed);
        }

        if (state.DetachedActive && state.CoverVisible)
        {
            if (_nteCoverWindow == null)
            {
                _nteCoverWindow = new NteCoverWindow();
                _nteCoverWindow.Show();
            }
            NteUpdateCoverWindowImage();
            _nteCoverWindow.PositionBesideOwner(this);
        }
        else
        {
            NteCloseCoverWindow();
        }

        if (NtePlaylistGrid != null)
        {
            if (state.DetachedActive)
            {
                Grid.SetColumn(NtePlaylistGrid, 0);
                Grid.SetColumnSpan(NtePlaylistGrid, 3);
                NtePlaylistGrid.Margin = new Thickness(0, 8, 0, 0);
            }
            else
            {
                Grid.SetColumn(NtePlaylistGrid, 2);
                Grid.SetColumnSpan(NtePlaylistGrid, 1);
                NtePlaylistGrid.Margin = new Thickness(0, 6, 0, 0);
            }
        }

        if (NtePlaylistPanel != null)
        {
            NtePlaylistPanel.CornerRadius = state.DetachedActive ? new CornerRadius(13) : new CornerRadius(12);
            NtePlaylistPanel.Background = state.DetachedActive
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromArgb(179, 15, 20, 30));
            NtePlaylistPanel.BorderBrush = state.DetachedActive
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromArgb(31, 255, 255, 255));
            NtePlaylistPanel.BorderThickness = state.DetachedActive ? new Thickness(0) : new Thickness(1);
            NtePlaylistPanel.Effect = state.DetachedActive ? null : NteCreateDetachedShadow(12, 0.38);
        }

        if (animate)
        {
            AnimateNteDetachedSeparation(state.DetachedActive);
        }
        else
        {
            BeginNteDetachedAnimations(null);
            SetNteDetachedTransforms(state.DetachedActive ? 0d : 0d, state.DetachedActive ? 0d : 0d);
        }
    }

    private static DropShadowEffect NteCreateDetachedShadow(double blurRadius, double opacity)
    {
        return new DropShadowEffect
        {
            BlurRadius = blurRadius,
            ShadowDepth = 3,
            Direction = 315,
            Opacity = opacity,
            Color = Color.FromRgb(0, 0, 0)
        };
    }

    private void AnimateNteDetachedSeparation(bool detachedActive)
    {
        var fromY = detachedActive ? -10d : 0d;
        var toY = 0d;
        var fromX = detachedActive ? -10d : 0d;
        var playlistFromX = detachedActive ? 12d : 0d;

        AnimateNteTransform(NteToolbarTransform, 0d, 0d, fromY / 2d, toY);
        AnimateNteTransform(NteCoverTransform, fromX, 0d, fromY, toY);
        AnimateNteTransform(NtePlaylistTransform, playlistFromX, 0d, fromY, toY);
        AnimateNteOpacity(NteCoverPanel, detachedActive ? 0.76d : 1d, 1d);
        AnimateNteOpacity(NtePlaylistGrid, detachedActive ? 0.78d : 1d, 1d);
    }

    private static void AnimateNteTransform(TranslateTransform? transform, double fromX, double toX, double fromY, double toY)
    {
        if (transform == null)
        {
            return;
        }

        transform.BeginAnimation(TranslateTransform.XProperty, CreateNteEaseAnimation(fromX, toX));
        transform.BeginAnimation(TranslateTransform.YProperty, CreateNteEaseAnimation(fromY, toY));
    }

    private static void AnimateNteOpacity(UIElement? element, double from, double to)
    {
        element?.BeginAnimation(UIElement.OpacityProperty, CreateNteEaseAnimation(from, to));
    }

    private void BeginNteDetachedAnimations(AnimationTimeline? animation)
    {
        NteToolbarTransform?.BeginAnimation(TranslateTransform.XProperty, animation);
        NteToolbarTransform?.BeginAnimation(TranslateTransform.YProperty, animation);
        NteCoverTransform?.BeginAnimation(TranslateTransform.XProperty, animation);
        NteCoverTransform?.BeginAnimation(TranslateTransform.YProperty, animation);
        NtePlaylistTransform?.BeginAnimation(TranslateTransform.XProperty, animation);
        NtePlaylistTransform?.BeginAnimation(TranslateTransform.YProperty, animation);
        NteCoverPanel?.BeginAnimation(UIElement.OpacityProperty, animation);
        NtePlaylistGrid?.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static DoubleAnimation CreateNteEaseAnimation(double from, double to)
    {
        return new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
    }

    private void SetNteDetachedTransforms(double x, double y)
    {
        if (NteToolbarTransform != null)
        {
            NteToolbarTransform.X = x;
            NteToolbarTransform.Y = y;
        }

        if (NteCoverTransform != null)
        {
            NteCoverTransform.X = x;
            NteCoverTransform.Y = y;
        }

        if (NtePlaylistTransform != null)
        {
            NtePlaylistTransform.X = x;
            NtePlaylistTransform.Y = y;
        }
    }

    private void ApplyNtePlaylistDirection(bool expandUp)
    {
        if (NteRow0 != null)
        {
            NteRow0.Height = expandUp ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        }

        if (NteRow1 != null)
        {
            NteRow1.Height = GridLength.Auto;
        }

        if (NteRow2 != null)
        {
            NteRow2.Height = expandUp ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        }

        if (NteRow3 != null)
        {
            NteRow3.Height = expandUp ? new GridLength(36) : GridLength.Auto;
        }

        if (NteToolbarBorder != null)
        {
            Grid.SetRow(NteToolbarBorder, expandUp ? 3 : 0);
            Panel.SetZIndex(NteToolbarBorder, 2);
        }

        if (NteStatusBar != null)
        {
            Grid.SetRow(NteStatusBar, 1);
        }

        if (NtePlaylistGrid != null)
        {
            Grid.SetRow(NtePlaylistGrid, expandUp ? 0 : 2);
            Panel.SetZIndex(NtePlaylistGrid, 1);
        }
    }

    private void NtePlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_nteCurrentSong is null)
        {
            if (_nteCurrentQueue.Count == 0)
            {
                return;
            }

            NtePlaySongAt(0);
            return;
        }

        if (_nteIsPlaying)
        {
            _ntePlayer.Pause();
            _nteIsPlaying = false;
        }
        else
        {
            _ntePlayer.Play();
            _nteIsPlaying = true;
        }

        NteUpdatePlaybackVisuals();
        NteRefreshList();
    }

    private void NtePrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_nteCurrentQueue.Count == 0)
        {
            return;
        }

        if (_ntePlayMode == NtePlayMode.Random)
        {
            NtePlaySongAt(_nteRandom.Next(_nteCurrentQueue.Count));
            return;
        }

        var index = _nteCurrentIndex <= 0 ? _nteCurrentQueue.Count - 1 : _nteCurrentIndex - 1;
        NtePlaySongAt(index);
    }

    private void NteNextButton_Click(object sender, RoutedEventArgs e)
    {
        NtePlayNext();
    }

    private void NteModeButton_Click(object sender, RoutedEventArgs e)
    {
        _ntePlayMode = _ntePlayMode switch
        {
            NtePlayMode.ListLoop => NtePlayMode.SingleLoop,
            NtePlayMode.SingleLoop => NtePlayMode.Random,
            _ => NtePlayMode.ListLoop
        };

        if (NteModeIcon != null)
        {
            NteModeIcon.Source = NteLoadResourceImage(_ntePlayMode switch
            {
                NtePlayMode.SingleLoop => NteIconSingleLoop,
                NtePlayMode.Random => NteIconRandom,
                _ => NteIconListLoop
            });
        }
    }

    private void NteFavoriteFilterButton_Click(object sender, RoutedEventArgs e)
    {
        _nteLibrary.ToggleFavoritesOnly();
        if (NteFavoriteFilterIcon != null)
        {
            NteFavoriteFilterIcon.Source = NteLoadResourceImage(_nteLibrary.FavoritesOnly ? NteIconHeartFilled : NteIconHeart);
        }
        NteRefreshQueue();
        NteRefreshList();

        if (NteTitleText != null && _nteLibrary.FavoritesOnly && _nteCurrentQueue.Count == 0 && _nteCurrentSong is null)
        {
            NteTitleText.Text = "收藏夹为空（再次点击爱心按钮可退出只看收藏）";
            NteResetTitleScroll();
        }
    }

    private void NteSongList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NteSongList == null)
        {
            return;
        }

        if (NteSongList.SelectedItem is not ListBoxItem { Tag: NteMusicSong song })
        {
            return;
        }

        var index = _nteCurrentQueue.FindIndex(item => item.Id == song.Id);
        if (index >= 0)
        {
            NtePlaySongAt(index);
        }
    }

    private void NteToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: NteMusicSong song })
        {
            return;
        }

        _nteLibrary.ToggleFavorite(song.Id);
        NteRefreshQueue();
        NteRefreshList();
    }

    private void NteRemoveSong_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: NteMusicSong song })
        {
            return;
        }

        var wasCurrent = _nteCurrentSong?.Id == song.Id;
        _nteLibrary.RemoveSong(song.Id);

        if (wasCurrent)
        {
            _ntePlayer.Stop();
            _nteCurrentSong = null;
            _nteCurrentIndex = -1;
            _nteIsPlaying = false;
            if (NteTitleText != null)
            {
                NteTitleText.Text = "未在播放";
                NteResetTitleScroll();
            }
            NteSetCover(null);
        }

        NteRefreshQueue();
        NteRefreshList();
        NteUpdatePlaybackVisuals();
    }

    private void NtePlaySongAt(int index)
    {
        if (index < 0 || index >= _nteCurrentQueue.Count)
        {
            return;
        }

        _nteCurrentIndex = index;
        _nteCurrentSong = _nteCurrentQueue[index];
        _ntePlayer.Open(_nteCurrentSong.Path, _ntePlaybackAdjustment.PlaybackRate, _ntePlaybackAdjustment.PitchSemitones);
        NteLoadSpectrumSnapshotAsync(_nteCurrentSong.Path);
        _ntePlayer.Play();
        _nteIsPlaying = true;
        if (NteTitleText != null)
        {
            NteTitleText.Text = _nteCurrentSong.Title;
            NteResetTitleScroll();
        }

        if (NteLikeCurrentImage != null)
        {
            var isFav = _nteLibrary.FavoriteSongIds.Contains(_nteCurrentSong.Id);
            NteLikeCurrentImage.Source = NteLoadResourceImage(isFav ? NteIconHeartFilled : NteIconHeart);
            NteLikeCurrentImage.Opacity = isFav ? 1.0 : 0.6;
        }

        NteSetCover(_nteCurrentSong.CoverPath, _nteCurrentSong.Path);
        NteUpdatePlaybackVisuals();
        NteRefreshList();
    }

    private void NtePlayNext()
    {
        if (_nteCurrentQueue.Count == 0)
        {
            return;
        }

        if (_ntePlayMode == NtePlayMode.Random)
        {
            NtePlaySongAt(_nteRandom.Next(_nteCurrentQueue.Count));
            return;
        }

        var index = _nteCurrentIndex < 0 ? 0 : (_nteCurrentIndex + 1) % _nteCurrentQueue.Count;
        NtePlaySongAt(index);
    }

    private void NtePlayAfterCurrentSongEnds()
    {
        if (_ntePlayMode == NtePlayMode.SingleLoop && _nteCurrentIndex >= 0)
        {
            NtePlaySongAt(_nteCurrentIndex);
            return;
        }

        NtePlayNext();
    }

    private void NteRefreshQueue()
    {
        _nteCurrentQueue = _nteLibrary.Queue.ToList();
        _nteCurrentIndex = _nteCurrentSong is null
            ? -1
            : _nteCurrentQueue.FindIndex(song => song.Id == _nteCurrentSong.Id);
    }

    private void NteRefreshList()
    {
        if (NteSongList == null)
        {
            return;
        }

        NteSongList.Items.Clear();
        var compactDockedList = _isDocked && Width <= CompactNanoDockedWidth + 2d;

        foreach (var group in _nteLibrary.QueueFolderGroups)
        {
            if (!compactDockedList)
            {
                NteSongList.Items.Add(NteCreateFolderHeaderItem(group));
            }

            foreach (var song in group.Songs)
            {
                NteSongList.Items.Add(NteCreateSongListItem(song, compactDockedList));
            }
        }
    }

    private ListBoxItem NteCreateSongListItem(NteMusicSong song, bool compactDockedList)
    {
        var item = new ListBoxItem
        {
            Tag = song,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };

        var row = new Grid
        {
            Tag = song,
            Height = 28,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };
        row.MouseLeftButtonUp += NteSongRow_MouseLeftButtonUp;
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (_nteCurrentSong?.Id == song.Id)
        {
            row.Background = new SolidColorBrush(Color.FromArgb(28, 255, 0, 127));
        }

        var favoriteButton = NteCreateMiniButton(song.IsFavorite ? NteIconHeartFilled : NteIconHeart, song);
        favoriteButton.Click += NteToggleFavorite_Click;
        Grid.SetColumn(favoriteButton, 0);
        row.Children.Add(favoriteButton);

        var title = new TextBlock
        {
            Text = song.Title,
            Foreground = _nteCurrentSong?.Id == song.Id
                ? new SolidColorBrush(Color.FromRgb(255, 0, 127))
                : new SolidColorBrush(Color.FromRgb(221, 221, 221)),
            FontWeight = _nteCurrentSong?.Id == song.Id ? FontWeights.Bold : FontWeights.Normal,
            FontSize = compactDockedList ? 10 : 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = compactDockedList ? new Thickness(3, 0, 2, 0) : new Thickness(6, 0, 6, 0)
        };
        Grid.SetColumn(title, 1);
        row.Children.Add(title);

        if (!compactDockedList && _nteCurrentSong?.Id == song.Id && _nteIsPlaying)
        {
            var activeMark = NteCreateInlineSpectrumIndicator();
            Grid.SetColumn(activeMark, 2);
            row.Children.Add(activeMark);
        }

        var removeButton = new Button
        {
            Content = NteCreateRemoveIcon(),
            Tag = song,
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(255, 90, 95)),
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            Template = NteCreateMiniButtonTemplate(),
            ToolTip = "从列表和收藏中移除（不会删除源文件）",
            Visibility = _nteLibrary.FavoritesOnly ? Visibility.Collapsed : Visibility.Visible
        };
        removeButton.Click += NteRemoveSong_Click;
        Grid.SetColumn(removeButton, 3);
        if (!compactDockedList)
        {
            row.Children.Add(removeButton);
        }

        item.Content = row;
        return item;
    }

    private ListBoxItem NteCreateFolderHeaderItem(NteMusicFolderGroup group)
    {
        var item = new ListBoxItem
        {
            Tag = group,
            IsHitTestVisible = true,
            Focusable = false,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };

        var row = new Grid
        {
            Tag = group,
            Height = 24,
            Background = Brushes.Transparent,
            Cursor = Cursors.IBeam,
            ToolTip = "双击重命名列表中的文件夹显示名"
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.MouseLeftButtonDown += NteFolderHeader_MouseLeftButtonDown;

        var name = new TextBlock
        {
            Text = group.DisplayName,
            Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 8, 0)
        };
        Grid.SetColumn(name, 0);
        row.Children.Add(name);

        var marker = new TextBlock
        {
            Text = group.DirectoryMarker,
            Foreground = new SolidColorBrush(Color.FromRgb(145, 150, 160)),
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 145,
            Margin = new Thickness(0, 0, 6, 0)
        };
        Grid.SetColumn(marker, 1);
        row.Children.Add(marker);

        item.Content = row;
        return item;
    }

    private void NteSongRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NteMusicSong song })
        {
            return;
        }

        var index = _nteCurrentQueue.FindIndex(item => item.Id == song.Id);
        if (index >= 0)
        {
            NtePlaySongAt(index);
        }
    }

    private void NteFolderHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not Grid { Tag: NteMusicFolderGroup group } row)
        {
            return;
        }

        e.Handled = true;
        NteBeginFolderRename(row, group);
    }

    private void NteBeginFolderRename(Grid row, NteMusicFolderGroup group)
    {
        row.Children.Clear();
        row.ColumnDefinitions.Clear();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var editor = new TextBox
        {
            Text = group.DisplayName,
            Tag = group,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
            Background = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(3, 1, 3, 1),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        editor.KeyDown += NteFolderRenameTextBox_KeyDown;
        editor.LostKeyboardFocus += NteFolderRenameTextBox_LostKeyboardFocus;
        Grid.SetColumn(editor, 0);
        row.Children.Add(editor);

        editor.Focus();
        editor.SelectAll();
    }

    private void NteFolderRenameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor || editor.Tag is not NteMusicFolderGroup group)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            NteCommitFolderRename(editor, group);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            NteRefreshList();
        }
    }

    private void NteFolderRenameTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox editor && editor.Tag is NteMusicFolderGroup group)
        {
            NteCommitFolderRename(editor, group);
        }
    }

    private void NteCommitFolderRename(TextBox editor, NteMusicFolderGroup group)
    {
        var name = editor.Text.Trim();
        if (name.Length > 0)
        {
            _nteLibrary.RenameFolder(group.Id, name);
        }

        NteRefreshList();
    }

    private static Button NteCreateMiniButton(string imageName, NteMusicSong song)
    {
        return new Button
        {
            Tag = song,
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            Template = NteCreateMiniButtonTemplate(),
            Content = new Image
            {
                Source = NteLoadResourceImage(imageName),
                Width = 13,
                Height = 13
            }
        };
    }

    private static Viewbox NteCreateRemoveIcon()
    {
        return new Viewbox
        {
            Width = 11,
            Height = 11,
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M632.117978 513.833356l361.805812 361.735298a85.462608 85.462608 0 1 1-121.001515 120.789974L511.116463 634.552816 146.913186 998.756094a86.026718 86.026718 0 0 1-121.706652-121.706652L389.480325 512.775651 27.674513 150.969839A85.392095 85.392095 0 0 1 148.393973 30.250379L510.199785 392.056191l366.671258-366.671258a86.026718 86.026718 0 0 1 121.706652 121.706652z"),
                Fill = new SolidColorBrush(Color.FromRgb(216, 30, 6)),
                Stretch = Stretch.Uniform
            }
        };
    }

    private static ControlTemplate NteCreateMiniButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Root";
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = border
        };

        var hoverTrigger = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)), "Root"));
        template.Triggers.Add(hoverTrigger);

        var pressedTrigger = new Trigger
        {
            Property = ButtonBase.IsPressedProperty,
            Value = true
        };
        pressedTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72, "Root"));
        template.Triggers.Add(pressedTrigger);

        return template;
    }

    private void NteUpdatePlaybackVisuals()
    {
        if (NtePlayIcon != null)
        {
            NtePlayIcon.Source = NteLoadResourceImage(_nteIsPlaying ? NteIconPause : NteIconContinue);
        }
    }

    private void ApplyNtePlaybackAdjustment()
    {
        _ntePlayer.SetPlaybackAdjustment(_ntePlaybackAdjustment.PlaybackRate, _ntePlaybackAdjustment.PitchSemitones);
        UpdateNtePlaybackAdjustmentMenuItem();
    }

    private static void AnimateNtePlaybackAdjustmentText(TextBlock? textBlock)
    {
        if (textBlock?.RenderTransform is not ScaleTransform scale)
        {
            return;
        }

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = 1d;
        scale.ScaleY = 1d;

        var animation = new DoubleAnimation
        {
            From = 1.22d,
            To = 1d,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void NteResetTitleScroll()
    {
        _nteTitleScrollOffset = 0d;
        _nteTitleScrollStartedUtc = DateTime.UtcNow;
        if (NteTitleTransform != null)
        {
            NteTitleTransform.X = 0d;
        }

        if (NteTitleViewport != null)
        {
            NteTitleViewport.OpacityMask = null;
        }
    }

    private void NteRefreshTitleLayout()
    {
        NteTitleText?.InvalidateMeasure();
        NteTitleViewport?.InvalidateMeasure();
        NteTitleViewport?.UpdateLayout();
        NteUpdateTitleScroll();
    }

    private void NteUpdateTitleScroll()
    {
        if (NteTitleText == null || NteTitleViewport == null || NteTitleTransform == null)
        {
            return;
        }

        NteTitleText.Measure(new Size(double.PositiveInfinity, NteTitleViewport.ActualHeight));
        var textWidth = Math.Max(NteTitleText.DesiredSize.Width, NteTitleText.ActualWidth);
        var viewportWidth = NteTitleViewport.ActualWidth;
        var overflow = textWidth - viewportWidth;
        if (overflow <= 4d)
        {
            _nteTitleScrollOffset = 0d;
            NteTitleTransform.X = 0d;
            NteTitleViewport.OpacityMask = null;
            return;
        }

        NteTitleViewport.OpacityMask = TryFindResource("FadeOutTextOpacityMask") as Brush;

        var elapsed = (DateTime.UtcNow - _nteTitleScrollStartedUtc).TotalSeconds;
        if (elapsed < 1.2d)
        {
            NteTitleTransform.X = 0d;
            return;
        }

        const double speed = 28d;
        const double endPause = 1.2d;
        var travel = overflow + 16d;
        var cycleSeconds = (travel / speed) + endPause;
        var cyclePosition = (elapsed - 1.2d) % cycleSeconds;

        _nteTitleScrollOffset = cyclePosition >= travel / speed
            ? overflow
            : Math.Min(overflow, cyclePosition * speed);
        NteTitleTransform.X = -_nteTitleScrollOffset;
    }

    private void NteSetCover(string? coverPath, string? audioPath = null)
    {
        if (NteCoverImage == null || NteCoverPlaceholder == null)
        {
            return;
        }

        var resolvedCoverPath = coverPath;
        if (string.IsNullOrWhiteSpace(resolvedCoverPath) || !File.Exists(resolvedCoverPath))
        {
            var embeddedCover = NteLoadEmbeddedCover(audioPath);
            if (embeddedCover != null)
            {
                NteCoverImage.Source = embeddedCover;
                NteCoverPlaceholder.Visibility = Visibility.Collapsed;
                NteUpdateCoverWindowImage(embeddedCover);
                return;
            }

            NteCoverImage.Source = null;
            NteCoverPlaceholder.Visibility = Visibility.Visible;
            NteUpdateCoverWindowImage(null);
            return;
        }

        var bitmap = new BitmapImage(new Uri(resolvedCoverPath));
        NteCoverImage.Source = bitmap;
        NteCoverPlaceholder.Visibility = Visibility.Collapsed;
        NteUpdateCoverWindowImage(bitmap);
    }

    private void NteUpdateCoverWindowImage(ImageSource? source = null)
    {
        if (_nteCoverWindow == null)
        {
            return;
        }

        if (source == null && NteCoverImage?.Source != null)
        {
            source = NteCoverImage.Source;
        }

        _nteCoverWindow.UpdateCover(source);
    }

    private void NteCloseCoverWindow()
    {
        if (_nteCoverWindow != null)
        {
            _nteCoverWindow.Close();
            _nteCoverWindow = null;
        }
    }

    private static BitmapImage? NteLoadEmbeddedCover(string? audioPath)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            return null;
        }

        try
        {
            using var file = TagLib.File.Create(audioPath);
            var picture = file.Tag.Pictures.FirstOrDefault();
            if (picture?.Data.Data is not { Length: > 0 } data)
            {
                return null;
            }

            var image = new BitmapImage();
            using var stream = new MemoryStream(data);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage? NteLoadResourceImage(string imageName)
    {
        var uri = new Uri($"pack://application:,,,/Assets/Nte/{imageName}", UriKind.Absolute);
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

    private void TeardownNtePlayer()
    {
        try
        {
            _ntePlaybackTimer.Stop();
            _nteSpectrumTimer.Stop();
            _nteTitleScrollTimer.Stop();
            _ntePlayer.Dispose();
            NteCloseCoverWindow();
        }
        catch
        {
        }
    }
}

internal enum NtePlayMode
{
    ListLoop,
    SingleLoop,
    Random
}

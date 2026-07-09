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
    private async Task RefreshCurrentSessionAsync(bool forceRebind = false)
    {
        if (_sessionManager is null)
        {
            SetIdleText();
            return;
        }

        var newSession = await ResolveTargetSessionAsync(_sessionManager, forceRebind);

        // If the OS momentarily reports no active session (which can happen when
        // the user pauses from the music app itself, or when the system briefly
        // demotes the session right after a transport control), keep the current
        // session and lyric state instead of dropping everything to idle.
        if (!forceRebind
            && newSession is null
            && _session is not null
            && ShouldKeepDisplayWhenSessionMissing(
                hasLoadedLyric: !string.IsNullOrWhiteSpace(_loadedLyricTrackSignature),
                hasCurrentTrack: !string.IsNullOrWhiteSpace(_currentTrackSignature),
                hasKnownMusicProcess: IsAnyKnownMusicProcessRunning(),
                inTransportGrace: DateTime.UtcNow <= _transportControlSessionGraceUntilUtc))
        {
            UpdatePlaybackStateUi();
            return;
        }

        if (_session == newSession)
        {
            await UpdateTrackAndPlaybackUiAsync();
            return;
        }

        DetachSessionHandlers(_session);
        _session = newSession;
        AttachSessionHandlers(_session);

        await UpdateTrackAndPlaybackUiAsync();
    }

    private void AttachSessionHandlers(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null)
        {
            return;
        }

        session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
        session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
    }

    private void DetachSessionHandlers(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null)
        {
            return;
        }

        session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
        session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
    }

    private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        if (sender != _session)
        {
            return;
        }

        await Dispatcher.InvokeAsync(async () => await UpdateTrackAndPlaybackUiAsync());
    }

    private async void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        if (sender != _session)
        {
            return;
        }

        await Dispatcher.InvokeAsync(UpdatePlaybackStateUi);
    }

    private void UpdatePlaybackStateUi()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var status = _session.GetPlaybackInfo().PlaybackStatus;
            SetPlayPauseIcon(status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        }
        catch
        {
        }
    }

    private async Task UpdateTrackAndPlaybackUiAsync()
    {
        UpdateActivePlayerLogo();

        if (_session is null)
        {
            if (await TryApplyKuGouAutomationSnapshotAsync())
            {
                return;
            }

            if (HasActiveFallbackPlayerDisplayState())
            {
                return;
            }

            // Don't wipe out a perfectly good loaded lyric just because the OS
            // momentarily reports no active session (e.g. when the music app
            // itself goes through a pause/resume internal session shuffle).
            if (ShouldKeepDisplayWhenSessionMissing(
                hasLoadedLyric: !string.IsNullOrWhiteSpace(_loadedLyricTrackSignature),
                hasCurrentTrack: !string.IsNullOrWhiteSpace(_currentTrackSignature),
                hasKnownMusicProcess: IsAnyKnownMusicProcessRunning(),
                inTransportGrace: DateTime.UtcNow <= _transportControlSessionGraceUntilUtc))
            {
                return;
            }

            _currentTrackSignature = string.Empty;
            _liked = false;
            ApplyLikeState();
            SetIdleText();
            return;
        }

        try
        {
            var playbackInfo = _session.GetPlaybackInfo();
            var status = playbackInfo.PlaybackStatus;
            SetPlayPauseIcon(status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);

            var media = await _session.TryGetMediaPropertiesAsync();
            var title = string.IsNullOrWhiteSpace(media.Title) ? string.Empty : media.Title.Trim();
            title = NormalizeMediaTitle(title)?.Trim() ?? string.Empty;

            var artist = string.IsNullOrWhiteSpace(media.Artist) ? media.AlbumArtist : media.Artist;
            artist = NormalizeMediaArtist(artist)?.Trim();

            var hasTrackIdentity = !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(artist);
            if (!hasTrackIdentity
                && (!string.IsNullOrWhiteSpace(_currentTrackSignature) || !string.IsNullOrWhiteSpace(_loadedLyricTrackSignature)))
            {
                return;
            }

            SongTitleText.Text = string.IsNullOrWhiteSpace(title) ? "未知歌曲" : title;
            ArtistText.Text = string.IsNullOrWhiteSpace(artist) ? "来自系统媒体会话" : artist;

            var trackSignature = BuildTrackSignature(title, artist, media.AlbumTitle);
            var lyricTrackSignature = BuildLyricTrackSignature(title, artist);
            if (!string.Equals(_currentTrackSignature, trackSignature, StringComparison.Ordinal))
            {
                _currentTrackSignature = trackSignature;
                _liked = _trackLikeState.TryGetValue(trackSignature, out var rememberedLiked) && rememberedLiked;
                ApplyLikeState();
            }
            RefreshLyricsForTrack(lyricTrackSignature, title, artist);

            await UpdateAlbumArtAsync(media);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(_currentTrackSignature) || !string.IsNullOrWhiteSpace(_loadedLyricTrackSignature))
            {
                UpdatePlaybackStateUi();
                return;
            }

            if (await TryApplyKuGouAutomationSnapshotAsync())
            {
                return;
            }

            if (HasActiveFallbackPlayerDisplayState())
            {
                return;
            }

            SetErrorText("读取媒体信息失败");
            SetAlbumArt(null);
        }
    }

    private static bool ShouldKeepDisplayWhenSessionMissing(
        bool hasLoadedLyric,
        bool hasCurrentTrack,
        bool hasKnownMusicProcess,
        bool inTransportGrace)
    {
        if (inTransportGrace)
        {
            return true;
        }

        return hasKnownMusicProcess && (hasLoadedLyric || hasCurrentTrack);
    }

    private void SetIdleText()
    {
        if (_selectedPlayerTarget == PlayerControlTarget.Auto)
        {
            SongTitleText.Text = "未检测到音乐播放";
            ArtistText.Text = "打开受支持的播放器后会显示";
        }
        else
        {
            SongTitleText.Text = $"等待 {GetPlayerTargetDisplayName(_selectedPlayerTarget)} 播放";
            ArtistText.Text = "点击左箭头可切换控制目标";
        }

        SetPlayPauseIcon(isPlaying: false);
        _liked = false;
        ApplyLikeState();
        SetAlbumArt(null);
        UpdateActivePlayerLogo();
        ClearLyricState();
    }

    private void SetErrorText(string message)
    {
        SongTitleText.Text = message;
        ArtistText.Text = "请尝试管理员权限或重启播放器";
        SetPlayPauseIcon(isPlaying: false);
        _liked = false;
        ApplyLikeState();
        SetAlbumArt(null);
        UpdateActivePlayerLogo();
        ClearLyricState();
    }

    private async Task UpdateAlbumArtAsync(GlobalSystemMediaTransportControlsSessionMediaProperties media)
    {
        if (media.Thumbnail is null)
        {
            SetAlbumArt(null);
            return;
        }

        try
        {
            using var thumbnailStream = await media.Thumbnail.OpenReadAsync();
            using var managedStream = thumbnailStream.AsStreamForRead();
            using var memory = new MemoryStream();
            await managedStream.CopyToAsync(memory);
            memory.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();

            SetAlbumArt(image);
        }
        catch
        {
            SetAlbumArt(null);
        }
    }

    private void SetAlbumArt(BitmapImage? image)
    {
        AlbumArtImage.Source = image;
        AlbumArtFallback.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateAlbumArtBackgroundColor(image);
        ApplyAlbumArtBackgroundColor();
    }

    private void UpdateAlbumArtBackgroundColor(BitmapImage? image)
    {
        if (image is null)
        {
            _rawContentBackgroundColor = _baseBackgroundColor;
            _rawGradientBackgroundColors = Array.Empty<Color>();
            _contentBackgroundColor = ApplyWidgetOpacityToColor(_rawContentBackgroundColor);
            return;
        }

        var dominantColor = ExtractDominantColor(image);
        var softenedColor = BlendColors(BoostSaturation(dominantColor, 1.18d), _baseBackgroundColor, 0.58d);
        _rawContentBackgroundColor = softenedColor;
        _rawGradientBackgroundColors = ExtractGradientColors(image);
        _contentBackgroundColor = ApplyWidgetOpacityToColor(_rawContentBackgroundColor);
    }

    private void ApplyAlbumArtBackgroundColor()
    {
        ApplyWidgetBackground(GetEffectiveBaseBackgroundColor());
    }

    private Color ExtractDominantColor(BitmapImage image)
    {
        try
        {
            var width = image.PixelWidth;
            var height = image.PixelHeight;
            var stride = width * 4;
            var pixelData = new byte[stride * height];

            image.CopyPixels(pixelData, stride, 0);

            const int levels = 8;
            var counts = new int[levels * levels * levels];
            var redSums = new double[counts.Length];
            var greenSums = new double[counts.Length];
            var blueSums = new double[counts.Length];
            var saturationSums = new double[counts.Length];
            var lightnessSums = new double[counts.Length];

            var step = Math.Max(1, width * height / 2600);
            for (var i = 0; i < width * height; i += step)
            {
                var offset = i * 4;
                if (offset + 2 >= pixelData.Length)
                {
                    break;
                }

                var red = pixelData[offset + 2];
                var green = pixelData[offset + 1];
                var blue = pixelData[offset];
                var (saturation, lightness) = GetColorMetrics(red, green, blue);
                if (!IsUsableAlbumColor(saturation, lightness))
                {
                    continue;
                }

                var ri = red * levels / 256;
                var gi = green * levels / 256;
                var bi = blue * levels / 256;
                var bucket = ri * levels * levels + gi * levels + bi;
                counts[bucket]++;
                redSums[bucket] += red;
                greenSums[bucket] += green;
                blueSums[bucket] += blue;
                saturationSums[bucket] += saturation;
                lightnessSums[bucket] += lightness;
            }

            var bestIndex = -1;
            var bestScore = 0d;
            for (var i = 0; i < counts.Length; i++)
            {
                var count = counts[i];
                if (count == 0)
                {
                    continue;
                }

                var averageSaturation = saturationSums[i] / count;
                var averageLightness = lightnessSums[i] / count;
                var balancedLightness = 1d - Math.Abs(averageLightness - 0.56d) / 0.56d;
                var brightNeutralBoost = averageLightness >= 0.72d && averageSaturation < 0.24d ? 1.42d : 1d;
                var score = count * (0.72d + averageSaturation) * Math.Clamp(balancedLightness, 0.46d, 1d) * brightNeutralBoost;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                return _baseBackgroundColor;
            }

            var bestCount = counts[bestIndex];
            return Color.FromRgb(
                (byte)Math.Clamp(redSums[bestIndex] / bestCount, 0, 255),
                (byte)Math.Clamp(greenSums[bestIndex] / bestCount, 0, 255),
                (byte)Math.Clamp(blueSums[bestIndex] / bestCount, 0, 255));
        }
        catch
        {
            return _baseBackgroundColor;
        }
    }

    private static (double Saturation, double Lightness) GetColorMetrics(byte red, byte green, byte blue)
    {
        var r = red / 255.0;
        var g = green / 255.0;
        var b = blue / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var lightness = (max + min) / 2.0;
        if (delta < 0.0001d)
        {
            return (0d, lightness);
        }

        var saturation = lightness <= 0.5d
            ? delta / (max + min)
            : delta / (2.0d - max - min);
        return (saturation, lightness);
    }

    private static bool IsUsableAlbumColor(double saturation, double lightness)
    {
        if (lightness < 0.13d || lightness > 0.97d)
        {
            return false;
        }

        return saturation >= 0.16d || lightness >= 0.64d;
    }

    private Color[] ExtractGradientColors(BitmapImage image)
    {
        try
        {
            var width = image.PixelWidth;
            var height = image.PixelHeight;
            var stride = width * 4;
            var pixelData = new byte[stride * height];
            image.CopyPixels(pixelData, stride, 0);

            const int levels = 8;
            var buckets = new GradientColorBucket[levels * levels * levels];
            var step = Math.Max(1, width * height / 3200);
            for (var i = 0; i < width * height; i += step)
            {
                var offset = i * 4;
                if (offset + 2 >= pixelData.Length)
                {
                    break;
                }

                var red = pixelData[offset + 2];
                var green = pixelData[offset + 1];
                var blue = pixelData[offset];
                var (saturation, lightness) = GetColorMetrics(red, green, blue);
                if (!IsUsableAlbumColor(saturation, lightness))
                {
                    continue;
                }

                var ri = red * levels / 256;
                var gi = green * levels / 256;
                var bi = blue * levels / 256;
                var bucket = ri * levels * levels + gi * levels + bi;
                buckets[bucket].Count++;
                buckets[bucket].Red += red;
                buckets[bucket].Green += green;
                buckets[bucket].Blue += blue;
                buckets[bucket].Saturation += saturation;
                buckets[bucket].Lightness += lightness;
            }

            var candidates = buckets
                .Where(bucket => bucket.Count > 0)
                .Select(bucket =>
                {
                    var color = Color.FromRgb(
                        (byte)Math.Clamp(bucket.Red / bucket.Count, 0, 255),
                        (byte)Math.Clamp(bucket.Green / bucket.Count, 0, 255),
                        (byte)Math.Clamp(bucket.Blue / bucket.Count, 0, 255));
                    var averageSaturation = bucket.Saturation / bucket.Count;
                    var averageLightness = bucket.Lightness / bucket.Count;
                    var balancedLightness = Math.Clamp(1d - Math.Abs(averageLightness - 0.56d) / 0.56d, 0.46d, 1d);
                    var brightNeutralBoost = averageLightness >= 0.72d && averageSaturation < 0.24d ? 1.42d : 1d;
                    var score = bucket.Count * (0.72d + averageSaturation) * balancedLightness * brightNeutralBoost;
                    return new ScoredColor(color, score);
                })
                .OrderByDescending(candidate => candidate.Score)
                .ToArray();

            var selected = new List<Color>(3);
            foreach (var candidate in candidates)
            {
                if (selected.All(color => GetColorDistance(color, candidate.Color) >= 62d))
                {
                    selected.Add(candidate.Color);
                    if (selected.Count == 3)
                    {
                        break;
                    }
                }
            }

            if (selected.Count < 2)
            {
                foreach (var candidate in candidates)
                {
                    if (selected.All(color => GetColorDistance(color, candidate.Color) >= 28d))
                    {
                        selected.Add(candidate.Color);
                        if (selected.Count == 3)
                        {
                            break;
                        }
                    }
                }
            }

            if (selected.Count >= 2)
            {
                return selected.ToArray();
            }
        }
        catch
        {
        }

        return Array.Empty<Color>();
    }

    private static double GetColorDistance(Color left, Color right)
    {
        var dr = left.R - right.R;
        var dg = left.G - right.G;
        var db = left.B - right.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    private struct GradientColorBucket
    {
        public int Count;
        public double Red;
        public double Green;
        public double Blue;
        public double Saturation;
        public double Lightness;
    }

    private readonly record struct ScoredColor(Color Color, double Score);

    private static Color BlendColors(Color foreground, Color background, double foregroundRatio)
    {
        var bgRatio = 1.0 - foregroundRatio;
        return Color.FromRgb(
            (byte)(foreground.R * foregroundRatio + background.R * bgRatio),
            (byte)(foreground.G * foregroundRatio + background.G * bgRatio),
            (byte)(foreground.B * foregroundRatio + background.B * bgRatio));
    }

    private static Color BoostSaturation(Color color, double factor)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double l = (max + min) / 2.0;

        if (delta < 0.0001) return color;

        double h;
        if (max == r)
            h = ((g - b) / delta) % 6.0;
        else if (max == g)
            h = (b - r) / delta + 2.0;
        else
            h = (r - g) / delta + 4.0;

        h = (h + 6.0) % 6.0;

        double s = l <= 0.5 ? delta / (max + min) : delta / (2.0 - max - min);
        s = Math.Min(1.0, s * factor);

        double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
        double p = 2.0 * l - q;

        return Color.FromRgb(
            (byte)Math.Clamp(HueToRgb(p, q, h / 6.0 + 1.0 / 3.0) * 255, 0, 255),
            (byte)Math.Clamp(HueToRgb(p, q, h / 6.0) * 255, 0, 255),
            (byte)Math.Clamp(HueToRgb(p, q, h / 6.0 - 1.0 / 3.0) * 255, 0, 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1.0;
        if (t > 1) t -= 1.0;
        if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        return p;
    }

    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "vivaldi", "opera"
    };

    private static bool IsAnyKnownMusicProcessRunning()
    {
        var checkedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var names in PlayerWindowProcessNames.Values)
        {
            foreach (var name in names)
            {
                if (!checkedNames.Add(name))
                {
                    continue;
                }

                if (BrowserProcessNames.Contains(name))
                {
                    continue;
                }

                try
                {
                    if (Process.GetProcessesByName(name).Length > 0)
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
        }

        return false;
    }
}

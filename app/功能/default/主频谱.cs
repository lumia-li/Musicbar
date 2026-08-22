using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Dsp;
using NAudio.Wave;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MusicBar;

public partial class MainWindow : Window
{
    private const int MainSpectrumBarCount = MainSpectrumVisual.BarCount;
    private const int MainSpectrumSampleCount = 2048;
    private readonly double[] _mainSpectrumSmoothedBars = new double[MainSpectrumBarCount];
    private readonly float[] _mainSpectrumSamples = new float[MainSpectrumSampleCount];
    // 复用的条形 Border 缓存，避免每帧 Clear + 重建
    private readonly List<Border> _mainSpectrumBarVisuals = new();
    private readonly object _mainSpectrumSampleLock = new();
    private DispatcherTimer? _mainSpectrumTimer;
    private WasapiLoopbackCapture? _mainSpectrumCapture;
    private bool _mainSpectrumIsPlaying;
    private int _mainSpectrumSampleWriteIndex;
    private int _mainSpectrumSampleRate = 44100;

    private void InitializeMainSpectrum()
    {
        _mainSpectrumTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _mainSpectrumTimer.Tick += (_, _) => RenderMainSpectrum();
        ApplyMainSpectrumPosition();
    }

    private void StopMainSpectrum()
    {
        _mainSpectrumTimer?.Stop();
        StopMainSpectrumCapture();
        Array.Fill(_mainSpectrumSmoothedBars, 0d);
        MainSpectrumHost.Visibility = Visibility.Collapsed;
    }

    private void SetMainSpectrumPlaybackState(bool isPlaying)
    {
        _mainSpectrumIsPlaying = isPlaying;
        UpdateMainSpectrumPopupVisibility();
        RenderMainSpectrum();
    }

    private void UpdateMainSpectrumPopupVisibility()
    {
        if (MainSpectrumHost == null)
        {
            return;
        }

        var state = MainSpectrumPopupState.Compute(
            _mainSpectrumEnabled && _displayMode != WidgetDisplayMode.Compact,
            _mainSpectrumIsPlaying,
            HasVisibleMainSpectrumEnergy());
        MainSpectrumHost.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;

        if (state.Visible && _mainSpectrumIsPlaying)
        {
            StartMainSpectrumCapture();
            _mainSpectrumTimer?.Start();
        }
        else if (state.Visible)
        {
            StopMainSpectrumCapture();
            _mainSpectrumTimer?.Start();
        }
        else
        {
            // 频谱不可见（简洁模式或已禁用）时彻底停止，
            // 避免 WASAPI 采集与每帧重建循环在后台空转。
            _mainSpectrumTimer?.Stop();
            StopMainSpectrumCapture();
        }
    }

    private void RenderMainSpectrum()
    {
        if (MainSpectrumBars == null || MainSpectrumHost == null)
        {
            return;
        }

        var bars = MainSpectrumAnalyzer.CalculateBars(GetMainSpectrumSamples(), _mainSpectrumSampleRate, MainSpectrumBarCount);
        var barColor = GetMainSpectrumColor();
        var layout = MainSpectrumOverlayLayout.Compute(_mainSpectrumPosition, MainSpectrumHost.Height);
        var visibleEnergy = 0d;

        EnsureMainSpectrumBarVisuals();

        for (var i = 0; i < MainSpectrumBarCount; i++)
        {
            var target = _mainSpectrumIsPlaying && i < bars.Length ? bars[i] : 0d;
            _mainSpectrumSmoothedBars[i] += (target - _mainSpectrumSmoothedBars[i]) * 0.45d;
            visibleEnergy = Math.Max(visibleEnergy, _mainSpectrumSmoothedBars[i]);

            var visual = MainSpectrumVisual.CreateBar(_mainSpectrumSmoothedBars[i]);
            var border = _mainSpectrumBarVisuals[i];
            border.Width = visual.Width;
            border.Height = visual.Height;
            border.Margin = new Thickness(visual.HorizontalMargin, 0, visual.HorizontalMargin, 0);
            border.VerticalAlignment = layout.BarVerticalAlignment;

            // 复用同一份可变画刷，仅在主题/专辑色变化时更新颜色，
            // 避免每帧新建 100 个画刷造成 GC 压力。
            if (border.Background is SolidColorBrush brush)
            {
                if (brush.Color != barColor)
                {
                    brush.Color = barColor;
                }
            }
            else
            {
                border.Background = new SolidColorBrush(barColor);
            }
        }

        if (!_mainSpectrumIsPlaying && visibleEnergy <= 0.015d)
        {
            _mainSpectrumTimer?.Stop();
            MainSpectrumHost.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>按需构建 100 个条形 Border 并只添加一次，后续帧仅更新属性。</summary>
    private void EnsureMainSpectrumBarVisuals()
    {
        while (_mainSpectrumBarVisuals.Count < MainSpectrumBarCount)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(1),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            _mainSpectrumBarVisuals.Add(border);
            MainSpectrumBars.Items.Add(border);
        }
    }

    private void StartMainSpectrumCapture()
    {
        if (_mainSpectrumCapture != null)
        {
            return;
        }

        try
        {
            _mainSpectrumCapture = new WasapiLoopbackCapture();
            _mainSpectrumSampleRate = Math.Max(1, _mainSpectrumCapture.WaveFormat.SampleRate);
            _mainSpectrumCapture.DataAvailable += MainSpectrumCapture_DataAvailable;
            _mainSpectrumCapture.RecordingStopped += MainSpectrumCapture_RecordingStopped;
            _mainSpectrumCapture.StartRecording();
        }
        catch
        {
            StopMainSpectrumCapture();
        }
    }

    private void StopMainSpectrumCapture()
    {
        if (_mainSpectrumCapture == null)
        {
            return;
        }

        try
        {
            _mainSpectrumCapture.DataAvailable -= MainSpectrumCapture_DataAvailable;
            _mainSpectrumCapture.RecordingStopped -= MainSpectrumCapture_RecordingStopped;
            _mainSpectrumCapture.StopRecording();
            _mainSpectrumCapture.Dispose();
        }
        catch
        {
        }
        finally
        {
            _mainSpectrumCapture = null;
        }
    }

    private async void MainSpectrumCapture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        StopMainSpectrumCapture();

        if (_mainSpectrumIsPlaying)
        {
            await Task.Delay(1500);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_mainSpectrumIsPlaying && _mainSpectrumCapture == null)
                {
                    StartMainSpectrumCapture();
                }
            });
        }
    }

    private void MainSpectrumCapture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_mainSpectrumCapture == null || e.BytesRecorded <= 0)
        {
            return;
        }

        var format = _mainSpectrumCapture.WaveFormat;
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var frameSize = bytesPerSample * channels;
        if (frameSize <= 0)
        {
            return;
        }

        lock (_mainSpectrumSampleLock)
        {
            for (var offset = 0; offset + frameSize <= e.BytesRecorded; offset += frameSize)
            {
                double sum = 0d;
                for (var channel = 0; channel < channels; channel++)
                {
                    var sampleOffset = offset + (channel * bytesPerSample);
                    sum += ReadMainSpectrumSample(e.Buffer, sampleOffset, format);
                }

                _mainSpectrumSamples[_mainSpectrumSampleWriteIndex] = (float)(sum / channels);
                _mainSpectrumSampleWriteIndex = (_mainSpectrumSampleWriteIndex + 1) % _mainSpectrumSamples.Length;
            }
        }
    }

    private static float ReadMainSpectrumSample(byte[] buffer, int offset, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            return BitConverter.ToSingle(buffer, offset);
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            return BitConverter.ToInt16(buffer, offset) / 32768f;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 24)
        {
            var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
            if ((value & 0x800000) != 0)
            {
                value |= unchecked((int)0xFF000000);
            }

            return value / 8388608f;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 32)
        {
            return BitConverter.ToInt32(buffer, offset) / 2147483648f;
        }

        return 0f;
    }

    private float[] GetMainSpectrumSamples()
    {
        var samples = new float[_mainSpectrumSamples.Length];
        lock (_mainSpectrumSampleLock)
        {
            var tailLength = _mainSpectrumSamples.Length - _mainSpectrumSampleWriteIndex;
            Array.Copy(_mainSpectrumSamples, _mainSpectrumSampleWriteIndex, samples, 0, tailLength);
            Array.Copy(_mainSpectrumSamples, 0, samples, tailLength, _mainSpectrumSampleWriteIndex);
        }

        return samples;
    }

    private bool HasVisibleMainSpectrumEnergy()
    {
        return _mainSpectrumSmoothedBars.Any(value => value > 0.015d);
    }

    private void SetMainSpectrumPosition(MainSpectrumPosition position)
    {
        _mainSpectrumPosition = position;
        ApplyMainSpectrumPosition();
        UpdateMainSpectrumMenuItem();
        UpdateMainSpectrumPopupVisibility();
        SaveWidgetPreferences();
    }

    private void ApplyMainSpectrumPosition()
    {
        if (MainSpectrumHost == null || MainSpectrumBars == null)
        {
            return;
        }

        var layout = MainSpectrumOverlayLayout.Compute(_mainSpectrumPosition, MainSpectrumHost.Height);
        MainSpectrumHost.VerticalAlignment = layout.HostVerticalAlignment;
        MainSpectrumBars.VerticalAlignment = layout.BarVerticalAlignment;
    }

    private Color GetMainSpectrumColor()
    {
        if (_useGradientBackground && _rawGradientBackgroundColors.Length > 0)
        {
            return MainSpectrumVisual.ColorFromBackground(_rawGradientBackgroundColors[0]);
        }

        // 停靠时使用内容背景色，避免悬停丙烯酸效果干扰频谱颜色
        var background = _isDocked
            ? _contentBackgroundColor
            : WidgetBackgroundHost.Background switch
            {
                SolidColorBrush brush => brush.Color,
                _ => _contentBackgroundColor
            };
        return MainSpectrumVisual.ColorFromBackground(background);
    }
}

internal readonly record struct MainSpectrumPopupState(bool Visible)
{
    public static MainSpectrumPopupState Compute(bool enabled, bool isPlaying, bool hasVisibleEnergy)
    {
        return new MainSpectrumPopupState(enabled && (isPlaying || hasVisibleEnergy));
    }
}

internal enum MainSpectrumPosition
{
    Top,
    Bottom
}

internal readonly record struct MainSpectrumOverlayLayout(
    double Top,
    double Height,
    VerticalAlignment HostVerticalAlignment,
    VerticalAlignment BarVerticalAlignment)
{
    public static MainSpectrumOverlayLayout Compute(MainSpectrumPosition position, double height)
    {
        return position == MainSpectrumPosition.Bottom
            ? new MainSpectrumOverlayLayout(0d, height, VerticalAlignment.Bottom, VerticalAlignment.Bottom)
            : new MainSpectrumOverlayLayout(0d, height, VerticalAlignment.Top, VerticalAlignment.Top);
    }

    public static MainSpectrumOverlayLayout Compute(double height)
    {
        return Compute(MainSpectrumPosition.Top, height);
    }
}

internal readonly record struct MainSpectrumLayerLayout(int SpectrumZIndex, int ControlsZIndex)
{
    public static MainSpectrumLayerLayout Default { get; } = new(0, 1);
}

internal readonly record struct MainSpectrumMenuState(bool EnabledChecked, bool TopChecked, bool BottomChecked)
{
    public static MainSpectrumMenuState Compute(bool enabled, MainSpectrumPosition position)
    {
        return new MainSpectrumMenuState(
            enabled,
            position == MainSpectrumPosition.Top,
            position == MainSpectrumPosition.Bottom);
    }
}

internal readonly record struct MainSpectrumBarVisual(double Width, double Height, double HorizontalMargin)
{
}

internal static class MainSpectrumVisual
{
    public const int BarCount = 100;
    public const double BarWidth = 2d;
    public const double BarHorizontalMargin = 1d;
    public const double TotalWidth = BarCount * (BarWidth + (BarHorizontalMargin * 2d));

    public static MainSpectrumBarVisual CreateBar(double energy)
    {
        var clamped = Math.Clamp(energy, 0d, 1d);
        return new MainSpectrumBarVisual(BarWidth, 2d + (clamped * 14d), BarHorizontalMargin);
    }

    public static Color ColorFromBackground(Color background)
    {
        var brightness = ((background.R * 0.299d) + (background.G * 0.587d) + (background.B * 0.114d)) / 255d;
        var lift = brightness < 0.45d ? 92 : 52;
        return Color.FromArgb(
            232,
            (byte)Math.Clamp(background.R + lift, 80, 255),
            (byte)Math.Clamp(background.G + lift, 80, 255),
            (byte)Math.Clamp(background.B + lift, 80, 255));
    }
}

internal static class MainSpectrumAnalyzer
{
    public static double[] CalculateBars(float[] samples, int sampleRate, int barCount)
    {
        var bars = new double[Math.Max(0, barCount)];
        if (samples.Length == 0 || sampleRate <= 0 || bars.Length == 0)
        {
            return bars;
        }

        var fftSize = 1;
        while (fftSize * 2 <= samples.Length)
        {
            fftSize *= 2;
        }

        fftSize = Math.Min(2048, fftSize);
        if (fftSize < 2)
        {
            return bars;
        }

        var exponent = (int)Math.Log2(fftSize);
        var fft = new Complex[fftSize];
        var start = Math.Max(0, samples.Length - fftSize);
        for (var i = 0; i < fftSize; i++)
        {
            var window = 0.5d - (0.5d * Math.Cos(2d * Math.PI * i / Math.Max(1, fftSize - 1)));
            fft[i].X = (float)(samples[start + i] * window);
            fft[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, exponent, fft);

        var maxFrequency = Math.Min(sampleRate / 2d, 14000d);
        const double minFrequency = 45d;
        for (var band = 0; band < bars.Length; band++)
        {
            var low = minFrequency * Math.Pow(maxFrequency / minFrequency, band / (double)bars.Length);
            var high = minFrequency * Math.Pow(maxFrequency / minFrequency, (band + 1) / (double)bars.Length);
            var firstBin = Math.Clamp((int)Math.Floor(low * fftSize / sampleRate), 1, (fftSize / 2) - 1);
            var lastBin = Math.Clamp((int)Math.Ceiling(high * fftSize / sampleRate), firstBin + 1, fftSize / 2);

            double sum = 0d;
            for (var bin = firstBin; bin < lastBin; bin++)
            {
                var magnitude = Math.Sqrt((fft[bin].X * fft[bin].X) + (fft[bin].Y * fft[bin].Y));
                sum += magnitude * magnitude;
            }

            var rms = Math.Sqrt(sum / Math.Max(1, lastBin - firstBin));
            var decibels = 20d * Math.Log10(rms + 0.0000001d);
            var normalized = Math.Clamp((decibels + 58d) / 46d, 0d, 1d);
            var shaped = normalized <= 0.055d
                ? 0d
                : Math.Pow((normalized - 0.055d) / 0.945d, 0.78d);

            var t = band / Math.Max(1d, bars.Length - 1d);
            var presenceCurve = 0.74d + (0.34d * Math.Sin(Math.PI * t)) + (0.16d * (1d - t));
            bars[band] = Math.Clamp(shaped * presenceCurve, 0d, 1d);
        }

        if (bars.Length > 2)
        {
            var smoothed = new double[bars.Length];
            for (var i = 0; i < bars.Length; i++)
            {
                var previous = i > 0 ? bars[i - 1] : bars[i];
                var next = i < bars.Length - 1 ? bars[i + 1] : bars[i];
                smoothed[i] = Math.Clamp((bars[i] * 0.72d) + (previous * 0.14d) + (next * 0.14d), 0d, 1d);
            }

            bars = smoothed;
        }

        return bars;
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MusicBar.功能;
using NAudio.Dsp;
using NAudio.Wave;

namespace MusicBar;

partial class MainWindow
{
    private const int NteSpectrumBarCount = 24;
    private const int NteInlineSpectrumBarCount = 5;
    private const int NteSpectrumWindowSamples = 4096;
    private readonly double[] _nteSpectrumSmoothedBars = new double[NteSpectrumBarCount];
    private readonly double[] _nteSpectrumCurrentBars = new double[NteSpectrumBarCount];
    private CancellationTokenSource? _nteSpectrumLoadCancellation;
    private NteSpectrumSnapshot? _nteSpectrumSnapshot;

    private void NteLoadSpectrumSnapshotAsync(string path)
    {
        _nteSpectrumLoadCancellation?.Cancel();
        _nteSpectrumLoadCancellation?.Dispose();
        _nteSpectrumLoadCancellation = new CancellationTokenSource();
        var token = _nteSpectrumLoadCancellation.Token;

        _nteSpectrumSnapshot = null;
        Array.Fill(_nteSpectrumSmoothedBars, 0d);

        _ = Task.Run(() =>
        {
            try
            {
                var snapshot = NteDecodeSpectrumSnapshot(path, token);
                if (snapshot is null || token.IsCancellationRequested)
                {
                    return;
                }

                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        _nteSpectrumSnapshot = snapshot;
                    }
                });
            }
            catch
            {
                // Unsupported or unreadable files can still play when the main playback engine accepts them.
            }
        }, token);
    }

    private static NteSpectrumSnapshot? NteDecodeSpectrumSnapshot(string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        using var reader = new AudioFileReader(path);
        var waveFormat = reader.WaveFormat;
        var channels = Math.Max(1, waveFormat.Channels);
        var sampleRate = Math.Max(1, waveFormat.SampleRate);
        var samples = new float[Math.Max(sampleRate * 30, sampleRate)];
        var readBuffer = new float[sampleRate * channels];
        var writeIndex = 0;
        int read;

        while (!token.IsCancellationRequested && (read = reader.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            var frames = read / channels;
            if (writeIndex + frames > samples.Length)
            {
                Array.Resize(ref samples, Math.Max(writeIndex + frames, samples.Length * 2));
            }

            for (var frame = 0; frame < frames; frame++)
            {
                double sum = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += readBuffer[(frame * channels) + channel];
                }

                samples[writeIndex++] = (float)(sum / channels);
            }
        }

        if (token.IsCancellationRequested || writeIndex <= 0)
        {
            return null;
        }

        Array.Resize(ref samples, writeIndex);
        return new NteSpectrumSnapshot(samples, sampleRate);
    }

    private void NteRenderSpectrum()
    {
        if (NteSpectrumBars == null)
        {
            return;
        }

        var bars = NteCalculateSpectrumBars(_nteSpectrumSnapshot, _ntePlayer.Position, _nteIsPlaying);
        NteSpectrumBars.Items.Clear();

        for (var i = 0; i < bars.Length; i++)
        {
            var target = bars[i];
            _nteSpectrumSmoothedBars[i] += (target - _nteSpectrumSmoothedBars[i]) * 0.42d;
            _nteSpectrumCurrentBars[i] = _nteSpectrumSmoothedBars[i];
            var height = 2d + (_nteSpectrumSmoothedBars[i] * 10d);

            NteSpectrumBars.Items.Add(new Border
            {
                Width = 2,
                Height = height,
                Margin = new Thickness(0, Math.Max(0d, 12d - height), 1, 0),
                Background = new SolidColorBrush(Color.FromRgb(212, 223, 49))
            });
        }

        NteRefreshInlineSpectrumIndicator();
    }

    private static double[] NteCalculateSpectrumBars(NteSpectrumSnapshot? snapshot, TimeSpan position, bool isPlaying)
    {
        var bars = new double[NteSpectrumBarCount];
        if (!isPlaying || snapshot is null || snapshot.Samples.Length == 0)
        {
            return bars;
        }

        var center = Math.Clamp((int)(position.TotalSeconds * snapshot.SampleRate), 0, snapshot.Samples.Length - 1);
        var start = Math.Clamp(center - (NteSpectrumWindowSamples / 2), 0, Math.Max(0, snapshot.Samples.Length - NteSpectrumWindowSamples));
        var available = Math.Min(NteSpectrumWindowSamples, snapshot.Samples.Length - start);
        if (available <= 0)
        {
            return bars;
        }

        var fft = new Complex[NteSpectrumWindowSamples];
        for (var i = 0; i < NteSpectrumWindowSamples; i++)
        {
            var sampleIndex = start + i;
            var sample = sampleIndex < snapshot.Samples.Length ? snapshot.Samples[sampleIndex] : 0f;
            var window = 0.5d - (0.5d * Math.Cos(2d * Math.PI * i / Math.Max(1, NteSpectrumWindowSamples - 1)));
            fft[i].X = (float)(sample * window);
            fft[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, 12, fft);
        var maxFrequency = Math.Min(snapshot.SampleRate / 2d, 16000d);
        const double minFrequency = 40d;

        for (var band = 0; band < bars.Length; band++)
        {
            var low = minFrequency * Math.Pow(maxFrequency / minFrequency, band / (double)bars.Length);
            var high = minFrequency * Math.Pow(maxFrequency / minFrequency, (band + 1) / (double)bars.Length);
            var firstBin = Math.Clamp((int)Math.Floor(low * NteSpectrumWindowSamples / snapshot.SampleRate), 1, (NteSpectrumWindowSamples / 2) - 1);
            var lastBin = Math.Clamp((int)Math.Ceiling(high * NteSpectrumWindowSamples / snapshot.SampleRate), firstBin + 1, NteSpectrumWindowSamples / 2);

            double sumSquares = 0;
            for (var bin = firstBin; bin < lastBin; bin++)
            {
                var magnitude = Math.Sqrt((fft[bin].X * fft[bin].X) + (fft[bin].Y * fft[bin].Y));
                sumSquares += magnitude * magnitude;
            }

            var rms = Math.Sqrt(sumSquares / Math.Max(1, lastBin - firstBin));
            bars[band] = Math.Clamp(Math.Log10(1d + (rms * 36d)), 0d, 1d);
        }

        var max = bars.Max();
        if (max > 0.001d)
        {
            for (var i = 0; i < bars.Length; i++)
            {
                bars[i] = Math.Clamp(bars[i] / max, 0d, 1d);
            }
        }

        return bars;
    }

    private StackPanel NteCreateInlineSpectrumIndicator()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Tag = "NteInlineSpectrum"
        };

        for (var i = 0; i < NteInlineSpectrumBarCount; i++)
        {
            var sampleIndex = Math.Min(_nteSpectrumCurrentBars.Length - 1, 3 + (i * 4));
            var energy = sampleIndex >= 0 ? _nteSpectrumCurrentBars[sampleIndex] : 0d;
            panel.Children.Add(new Border
            {
                Width = 2,
                Height = 3 + (energy * 8),
                Margin = new Thickness(1.2, 0, 1.2, 0),
                CornerRadius = new CornerRadius(1),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(212, 223, 49))
            });
        }

        return panel;
    }

    private void NteRefreshInlineSpectrumIndicator()
    {
        if (NteSongList == null || _nteCurrentSong is null)
        {
            return;
        }

        foreach (var item in NteSongList.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is not NteMusicSong song || song.Id != _nteCurrentSong.Id)
            {
                continue;
            }

            if (item.Content is not Grid row)
            {
                return;
            }

            var panel = row.Children
                .OfType<StackPanel>()
                .FirstOrDefault(child => Equals(child.Tag, "NteInlineSpectrum"));
            if (panel is null)
            {
                return;
            }

            for (var i = 0; i < panel.Children.Count; i++)
            {
                if (panel.Children[i] is not Border bar)
                {
                    continue;
                }

                var sampleIndex = Math.Min(_nteSpectrumCurrentBars.Length - 1, 3 + (i * 4));
                var energy = sampleIndex >= 0 ? _nteSpectrumCurrentBars[sampleIndex] : 0d;
                bar.Height = 3 + (energy * 8);
            }

            return;
        }
    }

    private sealed record NteSpectrumSnapshot(float[] Samples, int SampleRate);
}

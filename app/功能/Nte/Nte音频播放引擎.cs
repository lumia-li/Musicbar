using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MusicBar.功能;

public sealed class NteAudioPlaybackEngine : IDisposable
{
    private DirectSoundOut? _output;
    private WaveStream? _reader;
    private ISampleProvider? _sourceProvider;
    private NteRateSampleProvider? _rateProvider;
    private SmbPitchShiftingSampleProvider? _pitchProvider;
    private double _playbackRate = 1d;
    private double _pitchSemitones;
    private bool _disposed;
    private bool _stopRequested;

    public event EventHandler? PlaybackEnded;

    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    public double PlaybackRate => _playbackRate;
    public double PitchSemitones => _pitchSemitones;

    public void Open(string path, double playbackRate, double pitchSemitones)
    {
        Stop();
        DisposePipeline();

        _playbackRate = Math.Clamp(playbackRate, NtePlaybackAdjustment.MinPlaybackRate, NtePlaybackAdjustment.MaxPlaybackRate);
        _pitchSemitones = Math.Clamp(pitchSemitones, NtePlaybackAdjustment.MinPitchSemitones, NtePlaybackAdjustment.MaxPitchSemitones);

        _reader = CreateReader(path);
        _sourceProvider = _reader is Mp3FileReader mp3Reader
            ? mp3Reader.ToSampleProvider()
            : new SampleChannel(_reader, forceStereo: false);
        BuildOutputPipeline();
    }

    public void Play()
    {
        _stopRequested = false;
        _output?.Play();
    }

    public void Pause()
    {
        _output?.Pause();
    }

    public void Stop()
    {
        _stopRequested = true;
        _output?.Stop();
        if (_reader != null)
        {
            _reader.Position = 0;
        }
    }

    public void SetPlaybackAdjustment(double playbackRate, double pitchSemitones)
    {
        _playbackRate = Math.Clamp(playbackRate, NtePlaybackAdjustment.MinPlaybackRate, NtePlaybackAdjustment.MaxPlaybackRate);
        _pitchSemitones = Math.Clamp(pitchSemitones, NtePlaybackAdjustment.MinPitchSemitones, NtePlaybackAdjustment.MaxPitchSemitones);

        var needsAdjustedPipeline = UsesAdjustedPipeline(_playbackRate, _pitchSemitones);

        if (_pitchProvider != null && needsAdjustedPipeline)
        {
            if (_rateProvider != null)
            {
                _rateProvider.Rate = _playbackRate;
            }

            _pitchProvider.PitchFactor = CalculatePitchFactor(_playbackRate, _pitchSemitones);
            return;
        }

        if (_reader != null && ((_pitchProvider != null) != needsAdjustedPipeline))
        {
            RebuildOutputPipeline();
        }
    }

    internal static ISampleProvider CreatePlaybackProvider(
        ISampleProvider source,
        double playbackRate,
        double pitchSemitones,
        out NteRateSampleProvider? rateProvider,
        out SmbPitchShiftingSampleProvider? pitchProvider)
    {
        var clampedPlaybackRate = Math.Clamp(playbackRate, NtePlaybackAdjustment.MinPlaybackRate, NtePlaybackAdjustment.MaxPlaybackRate);
        var clampedPitchSemitones = Math.Clamp(pitchSemitones, NtePlaybackAdjustment.MinPitchSemitones, NtePlaybackAdjustment.MaxPitchSemitones);

        rateProvider = null;
        pitchProvider = null;

        if (!UsesAdjustedPipeline(clampedPlaybackRate, clampedPitchSemitones))
        {
            return source;
        }

        rateProvider = new NteRateSampleProvider(source, clampedPlaybackRate);
        pitchProvider = new SmbPitchShiftingSampleProvider(rateProvider)
        {
            PitchFactor = CalculatePitchFactor(clampedPlaybackRate, clampedPitchSemitones)
        };
        return pitchProvider;
    }

    internal static bool ShouldUseMp3FrameReader(string path)
    {
        return string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase);
    }

    private static WaveStream CreateReader(string path)
    {
        return ShouldUseMp3FrameReader(path)
            ? new Mp3FileReader(path)
            : new AudioFileReader(path);
    }

    private static float CalculatePitchFactor(double playbackRate, double pitchSemitones)
    {
        var compensationSemitones = -12d * Math.Log(playbackRate, 2d);
        return (float)Math.Pow(2d, (pitchSemitones + compensationSemitones) / 12d);
    }

    private static bool UsesAdjustedPipeline(double playbackRate, double pitchSemitones)
    {
        return Math.Abs(playbackRate - 1d) > 0.0001d || Math.Abs(pitchSemitones) > 0.0001d;
    }

    private void BuildOutputPipeline()
    {
        if (_sourceProvider == null)
        {
            return;
        }

        var provider = CreatePlaybackProvider(_sourceProvider, _playbackRate, _pitchSemitones, out _rateProvider, out _pitchProvider);
        _output = new DirectSoundOut(70);
        _output.PlaybackStopped += Output_PlaybackStopped;
        _output.Init(provider);
    }

    private void RebuildOutputPipeline()
    {
        if (_reader == null)
        {
            return;
        }

        var position = _reader.CurrentTime;
        var resume = _output?.PlaybackState == PlaybackState.Playing;
        DisposeOutput();
        _reader.CurrentTime = position;
        BuildOutputPipeline();

        if (resume)
        {
            Play();
        }
    }

    private void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // 当播放自然结束（非主动停止）时，触发 PlaybackEnded 事件
        // 使用更宽松的条件：只要不是主动停止，就认为是自然结束
        if (!_stopRequested)
        {
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DisposePipeline()
    {
        DisposeOutput();

        _reader?.Dispose();
        _reader = null;
        _sourceProvider = null;
        _rateProvider = null;
        _pitchProvider = null;
    }

    private void DisposeOutput()
    {
        if (_output == null)
        {
            return;
        }

        _stopRequested = true;
        _output.PlaybackStopped -= Output_PlaybackStopped;
        _output.Dispose();
        _output = null;
        _rateProvider = null;
        _pitchProvider = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        DisposePipeline();
    }
}

internal sealed class NteRateSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly List<float[]> _frames = new();
    private double _position;
    private bool _sourceEnded;

    public NteRateSampleProvider(ISampleProvider source, double rate)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        Rate = Math.Clamp(rate, NtePlaybackAdjustment.MinPlaybackRate, NtePlaybackAdjustment.MaxPlaybackRate);
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }
    public double Rate { get; set; }

    public int Read(float[] buffer, int offset, int count)
    {
        var requestedFrames = count / _channels;
        var writtenFrames = 0;

        while (writtenFrames < requestedFrames)
        {
            var baseIndex = (int)Math.Floor(_position);
            if (!EnsureFrame(baseIndex))
            {
                break;
            }

            EnsureFrame(baseIndex + 1);

            var fraction = _position - baseIndex;
            var current = _frames[baseIndex];
            var next = _frames[Math.Min(baseIndex + 1, _frames.Count - 1)];
            var writeOffset = offset + (writtenFrames * _channels);

            for (var channel = 0; channel < _channels; channel++)
            {
                buffer[writeOffset + channel] = (float)(current[channel] + ((next[channel] - current[channel]) * fraction));
            }

            _position += Rate;
            writtenFrames++;

            var discard = Math.Max(0, (int)Math.Floor(_position) - 2);
            if (discard > 0)
            {
                _frames.RemoveRange(0, Math.Min(discard, _frames.Count));
                _position -= discard;
            }
        }

        return writtenFrames * _channels;
    }

    private bool EnsureFrame(int frameIndex)
    {
        while (_frames.Count <= frameIndex && !_sourceEnded)
        {
            var frame = new float[_channels];
            var read = _source.Read(frame, 0, _channels);
            if (read < _channels)
            {
                _sourceEnded = true;
                if (read > 0)
                {
                    _frames.Add(frame);
                }
                break;
            }

            _frames.Add(frame);
        }

        return _frames.Count > frameIndex;
    }
}

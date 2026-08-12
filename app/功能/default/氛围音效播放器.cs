using System;
using System.Threading;
using NAudio.Wave;

namespace MusicBar.功能;

/// <summary>
/// 氛围音效循环播放器
/// </summary>
public sealed class 氛围音效播放器 : IDisposable
{
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    private string? _currentFilePath;
    private float _volume = 1.0f;
    private bool _disposed;
    private bool _stopRequested;

    // 用代计数器避免回调线程与 UI 线程的竞争
    private int _generation;

    public string? CurrentFilePath => _currentFilePath;

    /// <summary>
    /// 音量 0.0 ~ 1.0
    /// </summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_reader != null)
            {
                _reader.Volume = _volume;
            }
        }
    }

    public void Play(string filePath)
    {
        StopInternal();
        _stopRequested = false;
        _currentFilePath = filePath;

        // 增加代计数：旧的回调携带旧代号，会被忽略，不会干扰新播放
        int currentGen = Interlocked.Increment(ref _generation);
        StartOutput(currentGen);
    }

    public void Stop()
    {
        _stopRequested = true;
        StopInternal();
    }

    public void Dispose()
    {
        _disposed = true;
        _stopRequested = true;
        StopInternal();
    }

    private void StartOutput(int generation)
    {
        if (_currentFilePath is null) return;

        _reader?.Dispose();
        _reader = new AudioFileReader(_currentFilePath) { Volume = _volume };

        _output?.Dispose();
        _output = new WaveOutEvent();
        _output.Init(_reader);
        // 用 lambda 捕获当前代号，旧回调即使延迟触发也不会影响新播放
        _output.PlaybackStopped += (s, e) => OnPlaybackStopped(generation);
        _output.Play();
    }

    private void OnPlaybackStopped(int generation)
    {
        // 如果回调对应的代不是当前代，说明这是旧播放的残留回调，直接忽略
        if (_disposed || _stopRequested || generation != _generation) return;

        // 在回调线程中重建输出继续循环播放
        Thread.Sleep(1); // 让出时间片，避免过于紧凑的重建

        _output?.Dispose();
        _output = null;

        if (_reader != null)
        {
            _reader.Position = 0;
            _reader.Volume = _volume;
        }

        _output = new WaveOutEvent();
        if (_reader != null)
        {
            _output.Init(_reader);
        }

        // 用当前代注册新回调，形成正确的循环链
        int currentGen = _generation;
        _output.PlaybackStopped += (s, e) => OnPlaybackStopped(currentGen);
        _output.Play();
    }

    private void StopInternal()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _reader?.Dispose();
        _reader = null;
    }
}

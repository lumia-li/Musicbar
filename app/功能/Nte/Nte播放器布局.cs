using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MusicBar.Tests")]

namespace MusicBar;

internal readonly record struct NtePlayerLayoutState(
    double WindowHeight,
    bool PlaylistVisible,
    bool CoverVisible,
    bool StatusVisible,
    bool DetachedActive)
{
    public static NtePlayerLayoutState Compute(bool expanded, bool detachedEnabled, bool docked)
    {
        const double defaultFreeHeight = 46d;
        const double dockedHeight = 40d;
        const double expandedHeight = 190d;
        const double detachedExpandedHeight = 204d;

        if (!expanded)
        {
            return new NtePlayerLayoutState(
                docked ? dockedHeight : defaultFreeHeight,
                PlaylistVisible: false,
                CoverVisible: false,
                StatusVisible: !docked,
                DetachedActive: false);
        }

        var detachedActive = detachedEnabled && !docked;
        return new NtePlayerLayoutState(
            detachedActive ? detachedExpandedHeight : expandedHeight,
            PlaylistVisible: true,
            CoverVisible: !docked,
            StatusVisible: !docked,
            DetachedActive: detachedActive);
    }
}

internal readonly record struct NteDetachedLayoutMenuState(bool Visible, bool Checked)
{
    public static NteDetachedLayoutMenuState Compute(bool isNteMode, bool detachedEnabled)
    {
        return new NteDetachedLayoutMenuState(isNteMode, isNteMode && detachedEnabled);
    }
}

internal sealed class NtePlaybackAdjustment
{
    private static readonly double[] PlaybackRateSnapPoints = { 0.5d, 0.75d, 1.0d, 1.25d, 1.5d, 1.75d, 2.0d };

    public const double MinPlaybackRate = 0.5d;
    public const double MaxPlaybackRate = 2.0d;
    public const double MinPitchSemitones = -12d;
    public const double MaxPitchSemitones = 12d;

    public double PlaybackRate { get; private set; } = 1.0d;
    public double PitchSemitones { get; private set; }

    public string PlaybackRateText => FormatPlaybackRate(PlaybackRate);
    public string PitchText => FormatPitchSemitones(PitchSemitones);

    public bool SetPlaybackRate(double value)
    {
        var snapped = SnapPlaybackRate(value);
        var changed = Math.Abs(PlaybackRate - snapped) > 0.0001d;
        PlaybackRate = snapped;
        return changed;
    }

    public bool SetPitchSemitones(double value)
    {
        var snapped = SnapPitchSemitones(value);
        var changed = Math.Abs(PitchSemitones - snapped) > 0.0001d;
        PitchSemitones = snapped;
        return changed;
    }

    public void Reset()
    {
        PlaybackRate = 1.0d;
        PitchSemitones = 0d;
    }

    public static double SnapPlaybackRate(double value)
    {
        var clamped = Math.Clamp(value, MinPlaybackRate, MaxPlaybackRate);
        foreach (var point in PlaybackRateSnapPoints)
        {
            if (Math.Abs(clamped - point) <= 0.035d)
            {
                return point;
            }
        }

        return Math.Round(clamped, 2);
    }

    public static double SnapPitchSemitones(double value)
    {
        var clamped = Math.Clamp(value, MinPitchSemitones, MaxPitchSemitones);
        var nearest = Math.Round(clamped);
        var threshold = Math.Abs(nearest) < 0.0001d ? 0.25d : 0.22d;
        if (Math.Abs(clamped - nearest) <= threshold)
        {
            return nearest;
        }

        return Math.Round(clamped, 2);
    }

    public static string FormatPlaybackRate(double value)
    {
        return $"{value:0.##}x";
    }

    public static string FormatPitchSemitones(double value)
    {
        if (Math.Abs(value) < 0.0001d)
        {
            return "0";
        }

        return value > 0 ? $"+{value:0.##}" : $"{value:0.##}";
    }
}

internal readonly record struct NtePlaybackAdjustmentMenuState(bool Visible, string Header)
{
    public static NtePlaybackAdjustmentMenuState Compute(bool isNteMode, double playbackRate, double pitchSemitones)
    {
        var header = $"播放调节  {NtePlaybackAdjustment.FormatPlaybackRate(playbackRate)} / {NtePlaybackAdjustment.FormatPitchSemitones(pitchSemitones)}";
        return new NtePlaybackAdjustmentMenuState(isNteMode, header);
    }
}

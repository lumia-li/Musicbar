using System;

namespace MusicBar;

internal static class 进度跳转显示保持
{
    private const double TimelineReachedTargetToleranceMs = 750d;

    public static bool ShouldUseHeldSeekPosition(
        double heldPositionMs,
        double observedPositionMs,
        DateTime holdUntilUtc,
        DateTime nowUtc)
    {
        if (heldPositionMs < 0d || nowUtc >= holdUntilUtc)
        {
            return false;
        }

        return Math.Abs(observedPositionMs - heldPositionMs) > TimelineReachedTargetToleranceMs;
    }
}

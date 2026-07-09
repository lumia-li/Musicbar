using System;

namespace MusicBar;

internal static class 进度跳转
{
    public static long MillisecondsToPlaybackPositionTicks(double milliseconds)
    {
        return TimeSpan.FromMilliseconds(Math.Max(0d, milliseconds)).Ticks;
    }
}

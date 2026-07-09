namespace MusicBar;

internal enum DefaultPlaybackMode
{
    Sequential,
    Loop,
    Shuffle
}

internal enum DefaultSessionRepeatMode
{
    None,
    List
}

internal readonly record struct DefaultPlaybackSessionCommand(
    DefaultSessionRepeatMode RepeatMode,
    bool ShuffleActive);

internal enum DefaultPlaybackFallbackPlayer
{
    MoeKoeMusic
}

internal readonly record struct DefaultPlaybackShortcut(bool Ctrl, bool Alt, bool Shift, byte Key);

internal static class DefaultPlaybackModeResolver
{
    public static DefaultPlaybackMode GetNext(DefaultPlaybackMode mode)
    {
        return mode switch
        {
            DefaultPlaybackMode.Sequential => DefaultPlaybackMode.Loop,
            DefaultPlaybackMode.Loop => DefaultPlaybackMode.Shuffle,
            _ => DefaultPlaybackMode.Sequential
        };
    }

    public static DefaultPlaybackSessionCommand ToSessionCommand(DefaultPlaybackMode mode)
    {
        return mode switch
        {
            DefaultPlaybackMode.Loop => new(DefaultSessionRepeatMode.List, ShuffleActive: false),
            DefaultPlaybackMode.Shuffle => new(DefaultSessionRepeatMode.None, ShuffleActive: true),
            _ => new(DefaultSessionRepeatMode.None, ShuffleActive: false)
        };
    }

    public static string GetDisplayName(DefaultPlaybackMode mode)
    {
        return mode switch
        {
            DefaultPlaybackMode.Loop => "循环播放",
            DefaultPlaybackMode.Shuffle => "随机播放",
            _ => "顺序播放"
        };
    }

    public static string GetIconText(DefaultPlaybackMode mode)
    {
        return mode switch
        {
            DefaultPlaybackMode.Loop => "↻",
            DefaultPlaybackMode.Shuffle => "⤨",
            _ => "≡"
        };
    }

    public static string GetIconAssetName(DefaultPlaybackMode mode)
    {
        return mode switch
        {
            DefaultPlaybackMode.Loop => "列表循环.png",
            DefaultPlaybackMode.Shuffle => "随机播放.png",
            _ => "列表.png"
        };
    }

    public static DefaultPlaybackShortcut? GetShortcutFallback(DefaultPlaybackFallbackPlayer player)
    {
        return player switch
        {
            DefaultPlaybackFallbackPlayer.MoeKoeMusic => new(Ctrl: true, Alt: true, Shift: false, Key: (byte)'P'),
            _ => null
        };
    }
}

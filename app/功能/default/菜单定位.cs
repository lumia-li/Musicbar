using System.Windows;
using System.Windows.Controls.Primitives;

namespace MusicBar;

public enum PlayerPickerPlacementMode
{
    Below,
    Above,
    Left,
    Right
}

public static class MenuPlacement
{
    private const double PlayerPickerGap = 8d;
    private const double FloatingProgressGap = 8d;

    public static CustomPopupPlacementCallback RightSubmenuPlacementCallback { get; } = GetRightSubmenuPlacement;
    public static CustomPopupPlacementCallback CenteredBelowPlacementCallback { get; } = GetCenteredBelowPlacement;

    public static CustomPopupPlacement[] GetRightSubmenuPlacement(
        Size popupSize,
        Size targetSize,
        Point offset)
    {
        return new[]
        {
            new CustomPopupPlacement(new Point(targetSize.Width, 0), PopupPrimaryAxis.Horizontal)
        };
    }

    public static CustomPopupPlacement[] GetCenteredBelowPlacement(
        Size popupSize,
        Size targetSize,
        Point offset)
    {
        return GetPlayerPickerPlacement(popupSize, targetSize, offset, PlayerPickerPlacementMode.Below);
    }

    public static CustomPopupPlacement[] GetPlayerPickerPlacement(
        Size popupSize,
        Size targetSize,
        Point offset,
        PlayerPickerPlacementMode mode)
    {
        var x = (targetSize.Width - popupSize.Width) / 2d + offset.X;
        var y = (targetSize.Height - popupSize.Height) / 2d + offset.Y;

        var placement = mode switch
        {
            PlayerPickerPlacementMode.Above => new CustomPopupPlacement(
                new Point(x, -popupSize.Height - GetGap(mode) + offset.Y),
                PopupPrimaryAxis.Vertical),
            PlayerPickerPlacementMode.Left => new CustomPopupPlacement(
                new Point(-popupSize.Width - GetGap(mode) + offset.X, y),
                PopupPrimaryAxis.Horizontal),
            PlayerPickerPlacementMode.Right => new CustomPopupPlacement(
                new Point(targetSize.Width + GetGap(mode) + offset.X, y),
                PopupPrimaryAxis.Horizontal),
            _ => new CustomPopupPlacement(
                new Point(x, targetSize.Height + GetGap(mode) + offset.Y),
                PopupPrimaryAxis.Vertical)
        };

        return new[] { placement };
    }

    private static double GetGap(PlayerPickerPlacementMode mode)
    {
        return mode == PlayerPickerPlacementMode.Below
            ? FloatingProgressGap
            : PlayerPickerGap;
    }
}

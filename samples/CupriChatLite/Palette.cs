using Avalonia.Media;

namespace CupriChatLite;

/// <summary>Assigns each identity a stable, distinguishable colour (within a fixed palette).</summary>
public static class Palette
{
    private static readonly IBrush[] Colors =
    [
        Brush.Parse("#e74c3c"), Brush.Parse("#2980b9"), Brush.Parse("#27ae60"), Brush.Parse("#8e44ad"),
        Brush.Parse("#e67e22"), Brush.Parse("#16a085"), Brush.Parse("#d35400"), Brush.Parse("#2c3e50"),
        Brush.Parse("#c0392b"), Brush.Parse("#2ecc71"), Brush.Parse("#9b59b6"), Brush.Parse("#f39c12"),
    ];

    public static IBrush For(string idHex)
    {
        if (string.IsNullOrEmpty(idHex))
            return Colors[0];

        int index;
        try
        {
            index = Convert.ToInt32(idHex[..Math.Min(2, idHex.Length)], 16);
        }
        catch
        {
            index = idHex.GetHashCode() & 0x7FFFFFFF; // avoid Math.Abs(int.MinValue) overflow
        }

        return Colors[index % Colors.Length];
    }
}

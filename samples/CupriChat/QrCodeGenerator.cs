using Avalonia.Media.Imaging;
using QRCoder;

namespace CupriChat;

/// <summary>Renders a string (the invite link) as a QR-code bitmap for display.</summary>
public static class QrCodeGenerator
{
    public static Bitmap Create(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        var bytes = png.GetGraphic(10);
        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    }
}

using QRCoder;

namespace CodexControl.Agent.Desktop;

public static class PairingQrRenderer
{
    public static Bitmap Render(string pairingUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingUrl);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(pairingUrl, QRCodeGenerator.ECCLevel.Q);
        using var qr = new QRCode(data);
        return qr.GetGraphic(8, Color.Black, Color.White, drawQuietZones: true);
    }
}

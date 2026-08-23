using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using CodexControl.Protocol;

namespace CodexControl.Agent.Control;

internal static class CodexImageAttachmentMapper
{
    public const int MaxAttachmentsPerMessage = 4;
    public const int MaxTotalDataUrlLength = 600_000;

    private const long MaxSourceBytes = 20 * 1024 * 1024;
    private const long MaxDecodedPixels = 50_000_000;
    private const int MaxDimension = 1_280;
    private const int MaxEncodedBytes = 430_000;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
    };

    public static bool TryMapLocal(string? path, out CodexThreadHistoryAttachmentPayload attachment)
    {
        attachment = null!;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var file = new FileInfo(fullPath);
            if (!file.Exists || file.Length is <= 0 or > MaxSourceBytes ||
                !AllowedExtensions.Contains(file.Extension))
            {
                return false;
            }

            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            return TryRender(stream, file.Name, out attachment);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            return false;
        }
    }

    public static bool TryMapDataUrl(string? url, out CodexThreadHistoryAttachmentPayload attachment)
    {
        attachment = null!;
        if (string.IsNullOrWhiteSpace(url) ||
            !url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separator = url.IndexOf(',', StringComparison.Ordinal);
        if (separator < 0 ||
            !url.AsSpan(0, separator).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(url[(separator + 1)..]);
            if (bytes.LongLength is <= 0 or > MaxSourceBytes)
            {
                return false;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            return TryRender(stream, "图片", out attachment);
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentException or OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool TryRender(
        Stream source,
        string name,
        out CodexThreadHistoryAttachmentPayload attachment)
    {
        attachment = null!;
        using var image = Image.FromStream(source, useEmbeddedColorManagement: false, validateImageData: true);
        if (image.Width <= 0 || image.Height <= 0 || image.Width * (long)image.Height > MaxDecodedPixels)
        {
            return false;
        }

        var scale = Math.Min(1d, MaxDimension / (double)Math.Max(image.Width, image.Height));
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(image, 0, 0, width, height);
        }

        var png = EncodePng(bitmap);
        if (png.Length <= MaxEncodedBytes)
        {
            attachment = Build(name, "image/png", png);
            return true;
        }

        foreach (var quality in new long[] { 85, 72, 60, 48 })
        {
            var jpeg = EncodeJpeg(bitmap, quality);
            if (jpeg.Length <= MaxEncodedBytes)
            {
                attachment = Build(name, "image/jpeg", jpeg);
                return true;
            }
        }

        return false;
    }

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] EncodeJpeg(Bitmap bitmap, long quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .Single(value => value.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        using var stream = new MemoryStream();
        bitmap.Save(stream, encoder, parameters);
        return stream.ToArray();
    }

    private static CodexThreadHistoryAttachmentPayload Build(string name, string mimeType, byte[] bytes) =>
        new(
            "image",
            name,
            mimeType,
            string.Concat("data:", mimeType, ";base64,", Convert.ToBase64String(bytes)));
}

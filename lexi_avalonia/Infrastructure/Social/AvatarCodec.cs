using System.Globalization;
using SkiaSharp;

namespace Lexi.Social;

/// <summary>Bounded avatar transcoding. The header is inspected before any pixel buffer is
/// allocated, the stored artifact is always a fresh 256x256 WebP with no inherited metadata,
/// and the caller's original file is only ever read.</summary>
public static class AvatarCodec
{
    public const int MaximumInputBytes = 5 * 1024 * 1024;
    public const long MaximumInputPixels = 20_000_000;
    public const int Edge = 256;
    public const int MaximumOutputBytes = 200_000;
    private const int PreferredQuality = 90;
    private const int MinimumQuality = 40;
    private const int QualityStep = 10;

    /// <summary>Reads the codec header only; no bitmap is created for a rejected file.</summary>
    public static bool TryReadDimensions(ReadOnlySpan<byte> input, out int width, out int height)
    {
        width = 0; height = 0;
        if (input.Length is < 12 or > MaximumInputBytes) return false;
        try
        {
            using var data = SKData.CreateCopy(input.ToArray());
            using var codec = SKCodec.Create(data);
            if (codec is null) return false;
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0) return false;
            width = info.Width; height = info.Height;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException) { return false; }
    }

    /// <summary>Centre-crops to a square and re-encodes. Returns false for anything the pipeline must not store.</summary>
    public static bool TryCreate(ReadOnlySpan<byte> input, out byte[]? webp)
    {
        webp = null;
        if (!TryReadDimensions(input, out var width, out var height)) return false;
        if ((long)width * height > MaximumInputPixels) return false;
        try
        {
            using var data = SKData.CreateCopy(input.ToArray());
            using var source = SKBitmap.Decode(data);
            if (source is null || source.Width <= 0 || source.Height <= 0) return false;
            using var square = new SKBitmap(new SKImageInfo(Edge, Edge, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(square))
            {
                canvas.Clear(SKColors.Transparent);
                var side = Math.Min(source.Width, source.Height);
                var left = (source.Width - side) / 2f; var top = (source.Height - side) / 2f;
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                canvas.DrawBitmap(source, new SKRect(left, top, left + side, top + side), new SKRect(0, 0, Edge, Edge), paint);
                canvas.Flush();
            }
            // Re-encoding from pixels is what drops camera EXIF, ICC profiles and every other
            // original metadata block: only the rendered square is ever handed to the encoder.
            using var image = SKImage.FromBitmap(square);
            for (var quality = PreferredQuality; quality >= MinimumQuality; quality -= QualityStep)
            {
                using var encoded = image.Encode(SKEncodedImageFormat.Webp, quality);
                if (encoded is null || encoded.Size > MaximumOutputBytes) continue;
                webp = encoded.ToArray();
                return true;
            }
            return false;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException) { return false; }
    }

    /// <summary>A timestamp plus fresh entropy, so a replacement upload can never overwrite a served object.</summary>
    public static string FileName(DateTimeOffset at, Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("An avatar object needs a real identifier.", nameof(id));
        return at.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + id.ToString("D") + ".webp";
    }

    /// <summary>Every object lives under the uploading account's own folder.</summary>
    public static string Path(Guid owner, string fileName)
    {
        if (owner == Guid.Empty) throw new ArgumentException("An avatar object needs a real owner.", nameof(owner));
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return owner.ToString("D") + "/" + fileName;
    }
}

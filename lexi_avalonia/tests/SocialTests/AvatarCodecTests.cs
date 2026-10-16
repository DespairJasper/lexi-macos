using System.Text;
using Lexi.Social;
using SkiaSharp;

/// <summary>Avatar pipeline contract tests. Fixtures are encoded in-process; nothing touches disk or the network.</summary>
internal static class AvatarCodecTests
{
    private static byte[] Encode(int width, int height, Action<SKCanvas, int, int> paint, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) paint(canvas, width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>A PNG whose IHDR honestly advertises the given dimensions, with a CRC the
    /// decoder will accept and no pixel data. Only the header is ever meant to be read.</summary>
    private static byte[] AdvertisedPng(int width, int height)
    {
        var output = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var data = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4, 4), height);
        data[8] = 8; data[9] = 6;
        var body = new List<byte>(); body.AddRange(Encoding.ASCII.GetBytes("IHDR")); body.AddRange(data);
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body.ToArray()));
        output.AddRange(length); output.AddRange(body); output.AddRange(crc);
        // A one-byte IDAT: libpng stops after the first IDAT header while reading image info,
        // so the advertised dimensions are readable and no pixel data is ever decompressed.
        output.AddRange(Chunk("IDAT", new byte[] { 0x00 }));
        output.AddRange(Chunk("IEND", Array.Empty<byte>()));
        return output.ToArray();
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var body = new List<byte>(); body.AddRange(Encoding.ASCII.GetBytes(type)); body.AddRange(data);
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body.ToArray()));
        var output = new List<byte>(); output.AddRange(length); output.AddRange(body); output.AddRange(crc);
        return output.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    internal static void Run(Action<bool, string> check)
    {
        var square = Encode(400, 400, (canvas, w, h) => canvas.Clear(new SKColor(20, 90, 160)));
        check(AvatarCodec.TryCreate(square, out var webp) && webp is not null, "a real encoded image is accepted");
        check(webp!.Length <= AvatarCodec.MaximumOutputBytes, "an encoded avatar stays inside the storage limit");
        check(Encoding.ASCII.GetString(webp, 0, 4) == "RIFF" && Encoding.ASCII.GetString(webp, 8, 4) == "WEBP",
            "the artifact is a WebP the transport will accept");
        using (var decoded = SKBitmap.Decode(webp))
            check(decoded is not null && decoded.Width == AvatarCodec.Edge && decoded.Height == AvatarCodec.Edge,
                "the artifact is exactly 256 by 256");
        check(!Encoding.ASCII.GetString(webp).Contains("Exif", StringComparison.Ordinal), "the re-encoded artifact carries no EXIF block");

        // Non-square input yields the centred square, not a stretched or corner-cropped one.
        var letterbox = Encode(300, 100, (canvas, w, h) =>
        {
            canvas.Clear(new SKColor(200, 30, 30));
            canvas.DrawRect(new SKRect(100, 0, 200, 100), new SKPaint { Color = new SKColor(30, 30, 200) });
        });
        check(AvatarCodec.TryCreate(letterbox, out var cropped) && cropped is not null, "a non-square image is still accepted");
        using (var decoded = SKBitmap.Decode(cropped!))
        {
            var middle = decoded!.GetPixel(decoded.Width / 2, decoded.Height / 2);
            var leftEdge = decoded.GetPixel(2, decoded.Height / 2);
            check(middle.Blue > middle.Red, "the centre of a wide image survives the crop");
            check(leftEdge.Blue > leftEdge.Red, "the crop keeps the centred square rather than the left edge");
        }

        // Header dimensions are read before any pixel buffer is allocated.
        var honest = Encode(40, 30, (canvas, w, h) => canvas.Clear(SKColors.Green));
        check(AvatarCodec.TryReadDimensions(honest, out var width, out var height) && width == 40 && height == 30,
            "header dimensions are read without decoding pixels");
        var huge = AdvertisedPng(6000, 4000);
        check(AvatarCodec.TryReadDimensions(huge, out var hugeWidth, out var hugeHeight) && hugeWidth == 6000 && hugeHeight == 4000,
            "a header claiming above-limit dimensions is reported before allocation");
        check(!AvatarCodec.TryCreate(huge, out _), "an input above the pixel limit is rejected before a bitmap is allocated");
        check(!AvatarCodec.TryCreate(AdvertisedPng(400000, 400000), out _), "an absurd header is rejected");

        // Malformed and oversized inputs never reach the encoder.
        check(!AvatarCodec.TryCreate("this is not an image at all"u8.ToArray(), out _), "arbitrary bytes are rejected");
        check(!AvatarCodec.TryCreate(ReadOnlySpan<byte>.Empty, out _), "an empty file is rejected");
        check(!AvatarCodec.TryCreate(Encoding.ASCII.GetBytes("RIFF____WEBP"), out _), "a truncated WebP header is rejected");
        var oversized = new byte[AvatarCodec.MaximumInputBytes + 1];
        square.CopyTo(oversized, 0);
        check(!AvatarCodec.TryCreate(oversized, out _), "a file above the byte limit is rejected");
        // Padding never bypasses the guard, but the limit itself is inclusive.
        var atLimit = new byte[AvatarCodec.MaximumInputBytes];
        square.CopyTo(atLimit, 0);
        check(AvatarCodec.TryCreate(atLimit, out _), "a valid image exactly at the byte limit is still accepted");
        var justOver = new byte[AvatarCodec.MaximumInputBytes + 2];
        square.CopyTo(justOver, 0);
        check(!AvatarCodec.TryCreate(justOver, out _), "one byte over the limit is rejected even though the header alone is valid");

        // Storage file names stay inside the owner's folder and satisfy the transport and database rules.
        var instant = DateTimeOffset.Parse("2026-10-10T12:34:56Z", System.Globalization.CultureInfo.InvariantCulture);
        var id = Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789");
        var name = AvatarCodec.FileName(instant, id);
        check(name.EndsWith(".webp", StringComparison.Ordinal), "an avatar file name ends in .webp");
        check(name.Stem().All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'), "an avatar file name only uses characters the bucket policy accepts");
        check(name.Stem().Length is >= 1 and <= 75, "an avatar file name stays inside the bucket policy length");
        check(AvatarCodec.FileName(instant, id) == name, "the same avatar instant and identifier give the same name");
        check(AvatarCodec.FileName(instant.AddSeconds(1), id) != name, "a later upload can never overwrite an earlier object");
        check(AvatarCodec.FileName(instant, Guid.NewGuid()) != name, "a different identifier gives a different object");
        check(AvatarCodec.Path(Guid.Parse("11111111-1111-1111-1111-111111111111"), name) == "11111111-1111-1111-1111-111111111111/" + name,
            "the storage path is owned by the uploading account");
    }

    private static string Stem(this string name) => name[..^5];
}

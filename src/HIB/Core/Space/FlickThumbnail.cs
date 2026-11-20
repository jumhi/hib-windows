// H.I.B Space (docs/SPACE.md §3.4): the small JPEG preview sent with flickStart (≤ 64 KB).
using System;
using System.IO;
using SkiaSharp;

namespace HIB.Core.Space;

public static class FlickThumbnail
{
    public const int MaxBytes = 64 * 1024;

    /// <summary>
    /// A base64-able JPEG preview of an image file, at most <paramref name="maxBytes"/>, or null when the
    /// file isn't a decodable image (HEIC, RAW…), is huge, or anything goes wrong. Uses SkiaSharp directly
    /// (no UI thread needed) and decodes at a reduced scale so big photos stay cheap.
    /// </summary>
    public static byte[]? TryMakeJpeg(string path, int maxBytes = MaxBytes, int maxSide = 360)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > 80L * 1024 * 1024) return null;
            using var codec = SKCodec.Create(path);
            if (codec is null) return null;
            var full = codec.Info;
            if (full.Width <= 0 || full.Height <= 0) return null;

            // Let the codec do most of the shrinking (JPEG can decode at 1/2, 1/4, 1/8).
            var scale = Math.Min(1f, (float)(maxSide * 2) / Math.Max(full.Width, full.Height));
            var dims = codec.GetScaledDimensions(scale);
            var decodeInfo = new SKImageInfo(dims.Width, dims.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var decoded = new SKBitmap(decodeInfo);
            var r = codec.GetPixels(decodeInfo, decoded.GetPixels());
            if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

            using var oriented = Orient(decoded, codec.EncodedOrigin);
            var src = oriented ?? decoded;
            for (var side = maxSide; side >= 96; side = (int)(side * 0.75))
            {
                var k = Math.Min(1.0, (double)side / Math.Max(src.Width, src.Height));
                var w = Math.Max(1, (int)Math.Round(src.Width * k));
                var h = Math.Max(1, (int)Math.Round(src.Height * k));
                using var resized = src.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
                if (resized is null) return null;
                // JPEG has no alpha: flatten onto the card's navy so transparent PNGs don't turn black.
                using var flat = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
                using (var canvas = new SKCanvas(flat))
                {
                    canvas.Clear(new SKColor(0x13, 0x18, 0x38));
                    canvas.DrawBitmap(resized, 0, 0);
                }
                using var image = SKImage.FromBitmap(flat);
                foreach (var quality in new[] { 82, 70, 58 })
                {
                    using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                    if (data is null) return null;
                    if (data.Size <= maxBytes) return data.ToArray();
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    static SKBitmap? Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return null;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dst = new SKBitmap(new SKImageInfo(swap ? src.Height : src.Width, swap ? src.Width : src.Height, src.ColorType, src.AlphaType));
        using var c = new SKCanvas(dst);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: c.Scale(-1, 1, src.Width / 2f, 0); break;
            case SKEncodedOrigin.BottomRight: c.RotateDegrees(180, src.Width / 2f, src.Height / 2f); break;
            case SKEncodedOrigin.BottomLeft: c.Scale(1, -1, 0, src.Height / 2f); break;
            case SKEncodedOrigin.LeftTop: c.RotateDegrees(90); c.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop: c.Translate(dst.Width, 0); c.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: c.Translate(dst.Width, dst.Height); c.RotateDegrees(90); c.Scale(-1, 1); break;
            case SKEncodedOrigin.LeftBottom: c.Translate(0, dst.Height); c.RotateDegrees(270); break;
        }
        c.DrawBitmap(src, 0, 0);
        return dst;
    }
}

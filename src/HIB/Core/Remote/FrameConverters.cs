// H.I.B Phone Remote (docs/SPACE.md §15.3): the picture conversions that don't need a GPU, so they are tested anywhere.
//  - PixelConvert: crop + scale (+ undo a rotated display) a BGRA desktop image, and BGRA → NV12 (BT.601, limited
//    range) for the H.264 encoder. Used when the GPU's video processor isn't there.
//  - JpegFrameEncoder: the JPEG fallback (kind 4), quality 0.6.
using System;
using SkiaSharp;

namespace HIB.Core.Remote;

public static class PixelConvert
{
    /// <summary>
    /// Samples the <paramref name="crop"/> of a display (as the user sees it, pixels relative to the display of size
    /// dw × dh) from its unrotated BGRA texture into an outW × outH BGRA image (bilinear).
    /// </summary>
    public static void CropScaleBgra(ReadOnlySpan<byte> src, int srcW, int srcH, int srcStride, PixelBox crop, int dw, int dh,
        RemoteRotation rotation, Span<byte> dst, int outW, int outH)
    {
        if (dst.Length < outW * outH * 4) throw new ArgumentException("Destination too small", nameof(dst));
        var sx = (double)crop.W / outW;
        var sy = (double)crop.H / outH;
        for (var oy = 0; oy < outH; oy++)
        {
            var dy = crop.Y + (oy + 0.5) * sy;
            var row = dst.Slice(oy * outW * 4, outW * 4);
            for (var ox = 0; ox < outW; ox++)
            {
                var dx = crop.X + (ox + 0.5) * sx;
                var (tx, ty) = RemoteRotationMath.PointToSource(dx, dy, dw, dh, rotation);
                Sample(src, srcW, srcH, srcStride, tx - 0.5, ty - 0.5, row.Slice(ox * 4, 4));
            }
        }
    }

    static void Sample(ReadOnlySpan<byte> src, int w, int h, int stride, double x, double y, Span<byte> outPx)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        var xa = Math.Clamp(x0, 0, w - 1);
        var xb = Math.Clamp(x0 + 1, 0, w - 1);
        var ya = Math.Clamp(y0, 0, h - 1);
        var yb = Math.Clamp(y0 + 1, 0, h - 1);
        for (var c = 0; c < 4; c++)
        {
            double p00 = src[ya * stride + xa * 4 + c], p10 = src[ya * stride + xb * 4 + c];
            double p01 = src[yb * stride + xa * 4 + c], p11 = src[yb * stride + xb * 4 + c];
            var top = p00 + (p10 - p00) * fx;
            var bottom = p01 + (p11 - p01) * fx;
            outPx[c] = (byte)Math.Clamp((int)Math.Round(top + (bottom - top) * fy), 0, 255);
        }
    }

    /// <summary>BGRA (tightly packed, w × h, both even) → NV12: a Y plane, then interleaved U/V at half size. BT.601 limited range.</summary>
    public static void BgraToNv12(ReadOnlySpan<byte> bgra, int w, int h, Span<byte> nv12)
    {
        if (nv12.Length < w * h * 3 / 2) throw new ArgumentException("Destination too small", nameof(nv12));
        var uv = nv12[(w * h)..];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                nv12[y * w + x] = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
            }
        }
        for (var y = 0; y < h / 2; y++)
        {
            for (var x = 0; x < w / 2; x++)
            {
                int r = 0, g = 0, b = 0;
                for (var j = 0; j < 2; j++)
                for (var k = 0; k < 2; k++)
                {
                    var i = ((y * 2 + j) * w + x * 2 + k) * 4;
                    b += bgra[i];
                    g += bgra[i + 1];
                    r += bgra[i + 2];
                }
                r /= 4; g /= 4; b /= 4;
                uv[y * w + x * 2] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                uv[y * w + x * 2 + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
            }
        }
    }
}

public static class JpegFrameEncoder
{
    /// <summary>§15.3: quality 0.6.</summary>
    public const int Quality = 60;

    /// <summary>A JPEG of a BGRA image (rows of <paramref name="stride"/> bytes).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> bgra, int w, int h, int stride, int quality = Quality)
    {
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        var dst = bitmap.GetPixelSpan();
        for (var y = 0; y < h; y++) bgra.Slice(y * stride, w * 4).CopyTo(dst.Slice(y * info.RowBytes, w * 4));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }
}

using SkiaSharp;

namespace HwSuite.Core;

/// <summary>
/// LINUX: a decoded picture as BGRA32 as decoded (straight alpha from the palette decoder, premultiplied from drawing; identical for opaque pictures). Stands in
/// for WPF's BitmapSource in the shared imaging code (palette thumbnails, AL80 previews). Immutable.
/// </summary>
public sealed class RasterImage
{
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    /// <summary>BGRA32, row stride = width * 4.</summary>
    public byte[] Bgra { get; }

    public RasterImage(int w, int h, byte[] bgra)
    {
        if (bgra.Length != w * h * 4) throw new ArgumentException("pixel buffer size does not match");
        PixelWidth = w; PixelHeight = h; Bgra = bgra;
    }

    public static RasterImage FromSkia(SKBitmap b)
    {
        using var conv = b.ColorType == SKColorType.Bgra8888 ? null : b.Copy(SKColorType.Bgra8888);
        var src = conv ?? b;
        var px = new byte[src.Width * src.Height * 4];
        var span = src.GetPixelSpan();
        for (int y = 0; y < src.Height; y++) span.Slice(y * src.RowBytes, src.Width * 4).CopyTo(px.AsSpan(y * src.Width * 4));
        return new RasterImage(src.Width, src.Height, px);
    }

    public SKBitmap ToSkia()
    {
        var b = new SKBitmap(new SKImageInfo(PixelWidth, PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        unsafe
        {
            var span = new Span<byte>((void*)b.GetPixels(), b.ByteCount);
            for (int y = 0; y < PixelHeight; y++) Bgra.AsSpan(y * PixelWidth * 4, PixelWidth * 4).CopyTo(span.Slice(y * b.RowBytes));
        }
        return b;
    }
}

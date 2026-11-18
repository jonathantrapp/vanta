// PORTED from src\HwSuite\Hardware\Al80\Al80Imaging.cs (Windows 1.20.0): WPF DrawingVisual/RenderTargetBitmap/GifBitmapDecoder
// -> SkiaSharp. Same layout numbers (positions, sizes, colours), same outputs (BGRA32 96x160, RGB565 frames). Font:
// Bahnschrift is a Windows font, so the nearest condensed sans on Fedora is used (see PickTypeface).
using System.Globalization;
using HwSuite.Core;
using SkiaSharp;

namespace HwSuite.Hardware.Al80;

/// <summary>Readings drawn on the live panel; all optional.</summary>
public sealed record Al80Readings(double? CpuLoad, double? CpuTemp, double? GpuLoad, double? GpuTemp, double? RamPct, double? RamUsedGb, DateTime Time);

public static class Al80Imaging
{
    static readonly string[] Families = { "Bahnschrift", "Adwaita Sans", "Noto Sans", "Cantarell", "DejaVu Sans", "Liberation Sans" };
    static readonly SKTypeface Bold = PickTypeface(SKFontStyleWeight.Bold);
    static readonly SKTypeface Regular = PickTypeface(SKFontStyleWeight.Normal);

    static SKTypeface PickTypeface(SKFontStyleWeight weight)
    {
        var style = new SKFontStyle(weight, SKFontStyleWidth.SemiCondensed, SKFontStyleSlant.Upright);
        foreach (var f in Families)
        {
            var tf = SKFontManager.Default.MatchFamily(f, style);
            if (tf != null) return tf;
        }
        return SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
    }

    public static string FontName => Bold.FamilyName;

    static SKColor C(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    static readonly SKColor Bg = C(0x05070D), Track = C(0x1C2333), Label = C(0x8C97B0), Value = C(0xF2F5FA), Accent = C(0x6FA8FF), Warm = C(0xFFB347), Hot = C(0xFF5A4E);

    enum Align { Left, Center, Right }

    /// <summary>WPF placed text by the top of its line box; Skia draws at the baseline, so shift by the font's ascent.</summary>
    static void Text(SKCanvas c, string s, SKTypeface tf, float size, SKColor col, float x, float y, Align align = Align.Left)
    {
        using var paint = new SKPaint { Color = col, IsAntialias = true, Typeface = tf, TextSize = size, SubpixelText = true };
        float w = paint.MeasureText(s);
        float dx = align switch { Align.Center => -w / 2, Align.Right => -w, _ => 0 };
        c.DrawText(s, x + dx, y - paint.FontMetrics.Ascent, paint);
    }

    static SKColor LoadColor(double? pct) => pct is >= 90 ? Hot : pct is >= 70 ? Warm : Accent;
    static SKColor TempColor(double? c) => c is >= 85 ? Hot : c is >= 70 ? Warm : Accent;

    static void RoundRect(SKCanvas c, SKColor col, float x, float y, float w, float h, float r)
    {
        using var p = new SKPaint { Color = col, IsAntialias = true };
        c.DrawRoundRect(new SKRect(x, y, x + w, y + h), r, r, p);
    }

    static void Row(SKCanvas c, float y, string label, double? pct, string value, SKColor bar)
    {
        Text(c, label, Bold, 11, Label, 6, y);
        Text(c, value, Bold, 17, Value, 90, y - 5, Align.Right);
        RoundRect(c, Track, 6, y + 17, 84, 4, 2);
        if (pct is double p) RoundRect(c, bar, 6, y + 17, (float)Math.Max(2, 84 * Math.Clamp(p, 0, 100) / 100), 4, 2);
    }

    static string Pct(double? v) => v is double d ? $"{d:0}%" : "--";
    static string Deg(double? v) => v is double d ? $"{d:0}°" : "--";

    /// <summary>The "System" panel: clock, CPU, GPU, RAM, each with a bar. Returns BGRA32 pixels, 96x160.</summary>
    public static byte[] RenderSystem(Al80Readings r)
    {
        using var bmp = new SKBitmap(new SKImageInfo(Al80Protocol.Width, Al80Protocol.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(Bg);
            Text(c, r.Time.ToString("h:mm", CultureInfo.InvariantCulture), Bold, 26, Value, 48, 0, Align.Center);
            Text(c, r.Time.ToString("ddd d MMM", CultureInfo.InvariantCulture).ToUpperInvariant(), Regular, 10, Label, 48, 29, Align.Center);
            Row(c, 46, "CPU", r.CpuLoad, Pct(r.CpuLoad), LoadColor(r.CpuLoad));
            Row(c, 72, "CPU °C", r.CpuTemp, Deg(r.CpuTemp), TempColor(r.CpuTemp));
            Row(c, 98, "GPU", r.GpuLoad, Pct(r.GpuLoad), LoadColor(r.GpuLoad));
            Row(c, 124, "GPU °C", r.GpuTemp, Deg(r.GpuTemp), TempColor(r.GpuTemp));
            Text(c, r.RamPct is double m ? $"RAM {m:0}%" + (r.RamUsedGb is double g ? $" · {g:0.0} GB" : "") : "RAM --", Regular, 10, Label, 48, 146, Align.Center);
        }
        return RasterImage.FromSkia(bmp).Bgra;
    }

    /// <summary>Scale to fill w x h, cropping the overflow (cover), and return BGRA32.</summary>
    public static byte[] Cover(RasterImage src, int w, int h)
    {
        using var s = src.ToSkia();
        return Cover(s, w, h);
    }

    static byte[] Cover(SKBitmap src, int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.Black);
            float s = Math.Max((float)w / src.Width, (float)h / src.Height);
            float dw = src.Width * s, dh = src.Height * s;
            using var p = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            c.DrawBitmap(src, new SKRect((w - dw) / 2, (h - dh) / 2, (w - dw) / 2 + dw, (h - dh) / 2 + dh), p);
        }
        return RasterImage.FromSkia(bmp).Bgra;
    }

    public static RasterImage Load(string path)
    {
        using var b = SKBitmap.Decode(path) ?? throw new InvalidDataException("not an image VANTA can read: " + path);
        return RasterImage.FromSkia(b);
    }

    /// <summary>
    /// Decode a GIF into full composited frames (Skia applies frame offsets and disposal through RequiredFrame), resized
    /// to the target, plus the playback rate from the first frame delay. Frames beyond the target's limit are evenly dropped.
    /// </summary>
    public static (List<byte[]> rgb565, int fps) LoadGif(string path, Al80Protocol.GifTarget target, int? fpsOverride = null)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException("not an image VANTA can read: " + path);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var frameInfo = codec.FrameInfo;
        int count = Math.Max(1, frameInfo.Length);
        var composed = new List<SKBitmap>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var bmp = new SKBitmap(info);
                int req = frameInfo.Length > 0 ? frameInfo[i].RequiredFrame : -1;
                if (req >= 0 && req < composed.Count) composed[req].CopyTo(bmp);
                else bmp.Erase(SKColors.Transparent);
                codec.GetPixels(info, bmp.GetPixels(), new SKCodecOptions(i, req));
                composed.Add(bmp);
            }
            int delayMs = frameInfo.Length > 0 ? frameInfo[0].Duration : 0;
            int fps = fpsOverride ?? (delayMs > 0 ? (int)Math.Round(1000.0 / delayMs) : 20);
            var pick = composed;
            if (composed.Count > target.MaxFrames)
            {
                double step = (double)composed.Count / target.MaxFrames;
                fps = Math.Max(1, (int)Math.Round(fps / step));
                pick = Enumerable.Range(0, target.MaxFrames).Select(i => composed[(int)(i * step)]).ToList();
            }
            var frames = pick.Select(c => Al80Protocol.Bgra32ToRgb565(Cover(c, Al80Protocol.Width, target.Height))).ToList();
            return (frames, Math.Clamp(fps, 1, 60));
        }
        finally { foreach (var b in composed) b.Dispose(); }
    }

    public static byte[] ToRgb565(byte[] bgra) => Al80Protocol.Bgra32ToRgb565(bgra);

    /// <summary>BGRA32 pixels as an image (for the page preview).</summary>
    public static RasterImage ToBitmap(byte[] bgra, int w, int h) => new(w, h, bgra);
}

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HwSuite.Hardware.Games;

namespace HwSuite.Hardware.Al80;

/// <summary>Readings drawn on the live panel; all optional.</summary>
public sealed record Al80Readings(double? CpuLoad, double? CpuTemp, double? GpuLoad, double? GpuTemp, double? RamPct, double? RamUsedGb, DateTime Time);

/// <summary>
/// Drawing for the 96x160 screen with WPF (DrawingVisual + RenderTargetBitmap); call from an STA thread. Text is sized for a
/// panel about 2.4 cm wide: bold, high contrast, one reading per row (docs/PLAN.md in the yunzii repo).
/// </summary>
public static class Al80Imaging
{
    static readonly Typeface Bold = new(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Bold, FontStretches.SemiCondensed);
    static readonly Typeface Regular = new(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Normal, FontStretches.SemiCondensed);

    static Brush B(int rgb) { var b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)); b.Freeze(); return b; }
    static readonly Brush Bg = B(0x05070D), Track = B(0x1C2333), Label = B(0x8C97B0), Value = B(0xF2F5FA), Accent = B(0x6FA8FF), Warm = B(0xFFB347), Hot = B(0xFF5A4E);

    static void Text(DrawingContext dc, string s, Typeface tf, double size, Brush b, double x, double y, TextAlignment align = TextAlignment.Left)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, b, 1.0) { TextAlignment = align };
        dc.DrawText(ft, new Point(x, y));
    }

    static Brush LoadBrush(double? pct) => pct is >= 90 ? Hot : pct is >= 70 ? Warm : Accent;
    static Brush TempBrush(double? c) => c is >= 85 ? Hot : c is >= 70 ? Warm : Accent;

    static void Row(DrawingContext dc, double y, string label, double? pct, string value, Brush bar)
    {
        Text(dc, label, Bold, 11, Label, 6, y);
        Text(dc, value, Bold, 17, Value, 90, y - 5, TextAlignment.Right);
        dc.DrawRoundedRectangle(Track, null, new Rect(6, y + 17, 84, 4), 2, 2);
        if (pct is double p) dc.DrawRoundedRectangle(bar, null, new Rect(6, y + 17, Math.Max(2, 84 * Math.Clamp(p, 0, 100) / 100), 4), 2, 2);
    }

    static string Pct(double? v) => v is double d ? $"{d:0}%" : "--";
    static string Deg(double? v) => v is double d ? $"{d:0}°" : "--";

    /// <summary>The "System" panel: clock, CPU, GPU, RAM, each with a bar. Returns BGRA32 pixels, 96x160.</summary>
    public static byte[] RenderSystem(Al80Readings r)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Bg, null, new Rect(0, 0, Al80Protocol.Width, Al80Protocol.Height));
            Text(dc, r.Time.ToString("h:mm", CultureInfo.InvariantCulture), Bold, 26, Value, 48, 0, TextAlignment.Center);
            Text(dc, r.Time.ToString("ddd d MMM", CultureInfo.InvariantCulture).ToUpperInvariant(), Regular, 10, Label, 48, 29, TextAlignment.Center);
            Row(dc, 46, "CPU", r.CpuLoad, Pct(r.CpuLoad), LoadBrush(r.CpuLoad));
            Row(dc, 72, "CPU °C", r.CpuTemp, Deg(r.CpuTemp), TempBrush(r.CpuTemp));
            Row(dc, 98, "GPU", r.GpuLoad, Pct(r.GpuLoad), LoadBrush(r.GpuLoad));
            Row(dc, 124, "GPU °C", r.GpuTemp, Deg(r.GpuTemp), TempBrush(r.GpuTemp));
            Text(dc, r.RamPct is double m ? $"RAM {m:0}%" + (r.RamUsedGb is double g ? $" · {g:0.0} GB" : "") : "RAM --", Regular, 10, Label, 48, 146, TextAlignment.Center);
        }
        return Pixels(dv, Al80Protocol.Width, Al80Protocol.Height);
    }

    static FormattedText Wrapped(string s, Typeface tf, double size, Brush b, double maxW, int maxLines)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, b, 1.0)
        { TextAlignment = TextAlignment.Center, MaxTextWidth = maxW, Trimming = TextTrimming.CharacterEllipsis };
        ft.MaxTextHeight = size * 1.25 * maxLines + 1;   // about maxLines lines, the last one ends in "…"
        return ft;
    }

    /// <summary>Average colour of the art, darkened: a backdrop that matches the cover.</summary>
    static Color Backdrop(BitmapSource art)
    {
        var small = new TransformedBitmap(new FormatConvertedBitmap(art, PixelFormats.Bgra32, null, 0),
            new ScaleTransform(8.0 / art.PixelWidth, 8.0 / art.PixelHeight));
        var px = new byte[small.PixelWidth * small.PixelHeight * 4];
        small.CopyPixels(px, small.PixelWidth * 4, 0);
        long r = 0, g = 0, b = 0; int n = px.Length / 4;
        for (int i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; }
        if (n == 0) return Color.FromRgb(5, 7, 13);
        return Color.FromRgb((byte)(r / n * 0.22), (byte)(g / n * 0.22), (byte)(b / n * 0.22));
    }

    public static BitmapSource? Decode(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(data); bmp.EndInit(); bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>
    /// 1.21 "Now playing": the cover as a 96x96 square at the top, the title (bold, up to 2 lines) and artist (up to 2 lines)
    /// below on a backdrop taken from the cover. A pause mark sits on the cover while paused. Returns BGRA32, 96x160.
    /// </summary>
    public static byte[] RenderNowPlaying(string title, string artist, bool playing, BitmapSource? art)
    {
        const int W = Al80Protocol.Width, H = Al80Protocol.Height, A = 96;
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            var bg = art != null ? Backdrop(art) : Color.FromRgb(5, 7, 13);
            var bgb = new SolidColorBrush(bg); bgb.Freeze();
            dc.DrawRectangle(bgb, null, new Rect(0, 0, W, H));
            if (art != null)
            {
                double s = Math.Max((double)A / art.PixelWidth, (double)A / art.PixelHeight);
                double dw = art.PixelWidth * s, dh = art.PixelHeight * s;
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, A, A)));
                dc.DrawImage(art, new Rect((A - dw) / 2, (A - dh) / 2, dw, dh));
                dc.Pop();
            }
            else
            {
                dc.DrawRectangle(Track, null, new Rect(0, 0, A, A));
                Text(dc, "♪", Bold, 48, Label, A / 2.0, 14, TextAlignment.Center);
            }
            if (!playing)
            {
                var shade = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)); shade.Freeze();
                dc.DrawRectangle(shade, null, new Rect(0, 0, A, A));
                dc.DrawRoundedRectangle(Value, null, new Rect(36, 34, 8, 28), 2, 2);
                dc.DrawRoundedRectangle(Value, null, new Rect(52, 34, 8, 28), 2, 2);
            }
            var t = Wrapped(title, Bold, 13, Value, W - 6, 2);
            dc.DrawText(t, new Point(3, A + 3));
            double y = A + 3 + Math.Min(t.Height, t.MaxTextHeight) + 1;
            if (!string.IsNullOrWhiteSpace(artist) && y < H - 10)
            {
                var a = Wrapped(artist, Regular, 11, Label, W - 6, Math.Max(1, Math.Min(2, (int)((H - y) / (11 * 1.25)))));
                dc.DrawText(a, new Point(3, y));
            }
        }
        return Pixels(dv, W, H);
    }

    static readonly Brush Green = B(0x4CC27F), Red = B(0xEF6A62), Gray = B(0x39425A);

    /// <summary>A tier colour that stays readable on the dark panel (near-black tiers such as Unranked use the accent colour instead).</summary>
    static Brush Readable(int rgb)
    {
        if (rgb < 0) return Accent;
        int r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
        double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        if (lum < 70) return Accent;
        if (lum < 120) { r += (255 - r) / 3; g += (255 - g) / 3; b += (255 - b) / 3; }
        return B((r << 16) | (g << 8) | b);
    }

    static string Ago(DateTime utc, DateTime now)
    {
        var d = now - utc;
        return d.TotalMinutes < 60 ? $"{Math.Max(1, (int)d.TotalMinutes)}m" : d.TotalHours < 48 ? $"{(int)d.TotalHours}h" : $"{(int)d.TotalDays}d";
    }

    /// <summary>
    /// 1.22 Deadlock panel, 96x160: rank (tier colour), win rate over the last scored matches, wins and losses, average K / D / A with
    /// KDA, ten form squares (green win, red loss), and the last match (hero, result, K/D/A, how long ago). Without data it says
    /// "Loading" or shows why there are no stats; a small red dot marks data whose latest refresh failed. Returns BGRA32.
    /// </summary>
    public static byte[] RenderDeadlock(DeadlockPanel p, DateTime? nowUtc = null)
    {
        const int W = Al80Protocol.Width, H = Al80Protocol.Height;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) DrawDeadlock(dc, p, nowUtc ?? DateTime.UtcNow);
        return Pixels(dv, W, H);
    }

    static void DrawDeadlock(DrawingContext dc, DeadlockPanel p, DateTime now)
    {
        const int W = Al80Protocol.Width, H = Al80Protocol.Height;
        double cx = W / 2.0;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, W, H));
        Text(dc, "DEADLOCK", Regular, 9, Label, cx, 1, TextAlignment.Center);
        if (p.Summary is not { } s)
        {
            if (p.Error != null)
            {
                Text(dc, "No stats", Bold, 15, Value, cx, 30, TextAlignment.Center);
                dc.DrawText(Wrapped(p.Error, Regular, 10, Hot, W - 10, 7), new Point(5, 54));
            }
            else Text(dc, "Loading…", Bold, 15, Value, cx, 62, TextAlignment.Center);
            return;
        }
        string rank = s.Rank;
        Text(dc, rank, Bold, rank.Length > 10 ? 14 : 17, Readable(s.TierColor), cx, 12, TextAlignment.Center);
        int scored = s.Wins + s.Losses;
        Text(dc, scored == 0 ? "--" : $"{s.WinPct:0}%", Bold, 30, Value, cx, 30, TextAlignment.Center);
        Text(dc, scored == 0 ? "NO SCORED MATCHES" : $"WIN RATE · LAST {scored}", Regular, 8, Label, cx, 66, TextAlignment.Center);
        Text(dc, $"{s.Wins}W", Bold, 14, Green, 28, 78, TextAlignment.Center);
        Text(dc, $"{s.Losses}L", Bold, 14, Red, 68, 78, TextAlignment.Center);
        Text(dc, $"{s.AvgKills:0.0} / {s.AvgDeaths:0.0} / {s.AvgAssists:0.0}", Bold, 12, Value, cx, 96, TextAlignment.Center);
        Text(dc, $"KDA {s.Kda:0.0}", Regular, 9, Label, cx, 108, TextAlignment.Center);
        int n = s.Form.Count;
        double x0 = (W - (n * 8 + Math.Max(0, n - 1) * 1.5)) / 2;
        for (int i = 0; i < n; i++)
            dc.DrawRoundedRectangle(s.Form[i] == 1 ? Green : s.Form[i] == 2 ? Red : Gray, null, new Rect(x0 + i * 9.5, 122, 8, 8), 1.5, 1.5);
        if (s.Last is { } m)
        {
            dc.DrawText(Wrapped(m.Hero, Bold, 11, Value, W - 6, 1), new Point(3, 133));
            Text(dc, $"{(m.Won ? "W" : "L")} {m.Kills}/{m.Deaths}/{m.Assists} · {Ago(m.StartUtc, now)}", Regular, 9, m.Won ? Green : Red, cx, 146, TextAlignment.Center);
        }
        if (p.Error != null) dc.DrawEllipse(Hot, null, new Point(W - 6, 5), 2.5, 2.5);
    }

    /// <summary>Nothing is playing (Spotify closed or no track).</summary>
    public static byte[] RenderNothingPlaying() => RenderNowPlaying("Nothing playing", "Start a song in Spotify", true, null);

    public static byte[] Pixels(Visual v, int w, int h)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        return px;
    }

    /// <summary>Scale to fill w x h, cropping the overflow (cover), and return BGRA32.</summary>
    public static byte[] Cover(BitmapSource src, int w, int h)
    {
        var dv = new DrawingVisual();
        double s = Math.Max((double)w / src.PixelWidth, (double)h / src.PixelHeight);
        double dw = src.PixelWidth * s, dh = src.PixelHeight * s;
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
            dc.DrawImage(src, new Rect((w - dw) / 2, (h - dh) / 2, dw, dh));
        }
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        return Pixels(dv, w, h);
    }

    public static BitmapSource Load(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(Path.GetFullPath(path)); bmp.EndInit(); bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Decode a GIF into full composited frames (frame offsets and "restore to background" disposal handled), resized to the
    /// target, plus the playback rate from the first frame delay. Frames beyond the target's limit are evenly dropped.
    /// </summary>
    public static (List<byte[]> rgb565, int fps) LoadGif(string path, Al80Protocol.GifTarget target, int? fpsOverride = null)
    {
        using var fs = File.OpenRead(path);
        var dec = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        int cw = dec.Frames[0].PixelWidth, ch = dec.Frames[0].PixelHeight;
        try
        {
            if (dec.Metadata is BitmapMetadata gm)
            {
                if (gm.GetQuery("/logscrdesc/Width") is ushort lw && lw > 0) cw = lw;
                if (gm.GetQuery("/logscrdesc/Height") is ushort lh && lh > 0) ch = lh;
            }
        }
        catch { }
        var canvas = new RenderTargetBitmap(cw, ch, 96, 96, PixelFormats.Pbgra32);
        var composed = new List<BitmapSource>();
        int delayCs = 0;
        foreach (var f in dec.Frames)
        {
            int left = 0, top = 0, disposal = 0;
            if (f.Metadata is BitmapMetadata m)
            {
                try { left = Convert.ToInt32(m.GetQuery("/imgdesc/Left") ?? 0); top = Convert.ToInt32(m.GetQuery("/imgdesc/Top") ?? 0); } catch { }
                try { disposal = Convert.ToInt32(m.GetQuery("/grctlext/Disposal") ?? 0); } catch { }
                try { if (delayCs == 0) delayCs = Convert.ToInt32(m.GetQuery("/grctlext/Delay") ?? 0); } catch { }
            }
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawImage(f, new Rect(left, top, f.PixelWidth, f.PixelHeight));
            canvas.Render(dv);
            var snap = new WriteableBitmap(canvas); snap.Freeze();
            composed.Add(snap);
            if (disposal == 2)
            {
                // restore to background: clear the frame's area for the next one
                var px = new byte[cw * ch * 4];
                canvas.CopyPixels(px, cw * 4, 0);
                for (int y = Math.Max(0, top); y < Math.Min(ch, top + f.PixelHeight); y++)
                    Array.Clear(px, (y * cw + Math.Max(0, left)) * 4, Math.Max(0, Math.Min(cw, left + f.PixelWidth) - Math.Max(0, left)) * 4);
                var wb = new WriteableBitmap(cw, ch, 96, 96, PixelFormats.Pbgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, cw, ch), px, cw * 4, 0);
                canvas = new RenderTargetBitmap(cw, ch, 96, 96, PixelFormats.Pbgra32);
                var bdv = new DrawingVisual();
                using (var dc = bdv.RenderOpen()) dc.DrawImage(wb, new Rect(0, 0, cw, ch));
                canvas.Render(bdv);
            }
        }
        int fps = fpsOverride ?? (delayCs > 0 ? (int)Math.Round(100.0 / delayCs) : 20);
        if (composed.Count > target.MaxFrames)
        {
            double step = (double)composed.Count / target.MaxFrames;
            fps = Math.Max(1, (int)Math.Round(fps / step));
            composed = Enumerable.Range(0, target.MaxFrames).Select(i => composed[(int)(i * step)]).ToList();
        }
        var frames = composed.Select(c => Al80Protocol.Bgra32ToRgb565(Cover(c, Al80Protocol.Width, target.Height))).ToList();
        return (frames, Math.Clamp(fps, 1, 60));
    }

    public static byte[] ToRgb565(byte[] bgra) => Al80Protocol.Bgra32ToRgb565(bgra);

    /// <summary>BGRA32 pixels as a frozen WPF image (for the page preview).</summary>
    public static BitmapSource ToBitmap(byte[] bgra, int w, int h)
    {
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        b.Freeze();
        return b;
    }
}

using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HwSuite.Core;

/// <summary>One suggested colour from an image.</summary>
/// <param name="Color">The colour offered (0xRRGGBB), lifted when the image colour was too dark for LEDs.</param>
/// <param name="Source">The image colour it came from.</param>
/// <param name="Share">Fraction of the analysed (opaque) pixels it represents.</param>
public sealed record PaletteSwatch(int Color, int Source, double Share, bool Lifted)
{
    public string Hex => $"#{Color:X6}";
}

public sealed record PaletteResult(IReadOnlyList<PaletteSwatch> Swatches, int SuggestedPrimary, int? SuggestedSecondary, int ImageWidth, int ImageHeight, int AnalysedPixels, string? Note)
{
    /// <summary>Small copy of the image for display only (frozen); the original file is not kept.</summary>
    public BitmapSource? Thumbnail { get; init; }
}

/// <summary>A problem reading or analysing an image, in the owner's words.</summary>
public sealed class PaletteException : Exception { public PaletteException(string message) : base(message) { } }

/// <summary>
/// A small palette from a still image (1.14, "Colours from image"). Local only: WPF's own decoders (PNG, JPEG, BMP, GIF,
/// TIFF, ICO, and WebP/HEIC when Windows has the codec), no network, no new dependency. Bounded: the file size and pixel
/// count are checked from the header before decoding, the image is decoded straight to at most <see cref="WorkSize"/>
/// pixels on its longer side, and the analysis is a fixed amount of work on at most ~100 000 pixels. Deterministic (no
/// randomness), so the same image always gives the same swatches.
///
/// Method: opaque pixels only (alpha &lt; 50 % ignored) are binned (5 bits per channel) and clustered with a weighted
/// k-means in OKLab (perceptual), seeded deterministically. Clusters closer than a just-noticeable step are merged; 4-6
/// swatches are chosen greedily by coverage and distinctness, so small highlights (moonlight, mist) are represented, and
/// at most one near-black shadow is offered (a dark image does not make black the main suggestion). Saturation is never
/// boosted; only tones too dark for LEDs to show are lifted to a visible lightness, keeping their hue (marked as lifted).
/// </summary>
public static class ImagePalette
{
    public const long MaxFileBytes = 64L * 1024 * 1024;
    public const long MaxPixels = 80_000_000;
    public const int WorkSize = 320;
    public const int MaxSwatches = 6, MinSwatches = 4;
    /// <summary>OKLab distance below which two colours count as nearly the same.</summary>
    public const double SameColour = 0.055, MinDistinct = 0.08;
    /// <summary>LEDs show little below this OKLab lightness at everyday brightness; darker swatches are lifted to it.</summary>
    public const double MinLedLightness = 0.30, MaxLightness = 0.95;

    public static readonly string FileFilter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.ico|All files|*.*";

    /// <summary>Read and analyse an image file. Call off the UI thread. Throws <see cref="PaletteException"/> with a readable message.</summary>
    public static PaletteResult FromFile(string path, CancellationToken ct = default)
    {
        FileInfo fi;
        try { fi = new FileInfo(path); } catch { throw new PaletteException("That path cannot be read."); }
        if (!fi.Exists) throw new PaletteException("The file no longer exists.");
        if (fi.Length == 0) throw new PaletteException("The file is empty.");
        if (fi.Length > MaxFileBytes) throw new PaletteException($"The file is {fi.Length / 1048576.0:0} MB; choose an image under {MaxFileBytes / 1048576} MB.");
        int fullW, fullH;
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var dec = BitmapDecoder.Create(s, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            if (dec.Frames.Count == 0) throw new PaletteException("The image has no pictures in it.");
            fullW = dec.Frames[0].PixelWidth; fullH = dec.Frames[0].PixelHeight;
        }
        catch (PaletteException) { throw; }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or OverflowException)
        { throw new PaletteException("This file is not an image VANTA can read. Use PNG, JPEG, BMP, GIF or TIFF (WebP and HEIC need the Windows codec)."); }
        catch (IOException) { throw new PaletteException("The file could not be opened (it may be in use or unavailable)."); }
        catch (UnauthorizedAccessException) { throw new PaletteException("VANTA is not allowed to read that file."); }
        if (fullW <= 0 || fullH <= 0) throw new PaletteException("The image has no pixels.");
        if ((long)fullW * fullH > MaxPixels) throw new PaletteException($"The image is {fullW} × {fullH} pixels; choose one under {MaxPixels / 1_000_000} megapixels.");
        ct.ThrowIfCancellationRequested();

        BitmapSource small;
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = s;
            bi.CacheOption = BitmapCacheOption.OnLoad;   // read now; the stream (and file) is released at once
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (fullW >= fullH) bi.DecodePixelWidth = Math.Min(fullW, WorkSize); else bi.DecodePixelHeight = Math.Min(fullH, WorkSize);
            bi.EndInit();
            bi.Freeze();
            small = bi;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or IOException or OverflowException)
        { throw new PaletteException("The image could not be decoded (the file may be damaged)."); }
        ct.ThrowIfCancellationRequested();
        return FromBitmap(small, fullW, fullH);
    }

    /// <summary>Analyse an already decoded (small) bitmap; also used by fixtures and tests.</summary>
    public static PaletteResult FromBitmap(BitmapSource bmp, int? fullWidth = null, int? fullHeight = null)
    {
        BitmapSource src = bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        if (w <= 0 || h <= 0) throw new PaletteException("The image has no pixels.");
        if ((long)w * h > (long)WorkSize * WorkSize * 4) throw new PaletteException("internal: bitmap not reduced before analysis");
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        var r = FromPixels(px, w, h);
        var thumb = src.IsFrozen ? src : (BitmapSource)src.GetAsFrozen();
        return r with { ImageWidth = fullWidth ?? w, ImageHeight = fullHeight ?? h, Thumbnail = thumb };
    }

    // ---- colour maths (sRGB <-> OKLab) -------------------------------------------------------------------------------
    static readonly double[] ToLinear = Enumerable.Range(0, 256).Select(i => { double c = i / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }).ToArray();

    public static (double L, double A, double B) ToOkLab(int rgb)
    {
        double r = ToLinear[(rgb >> 16) & 255], g = ToLinear[(rgb >> 8) & 255], b = ToLinear[rgb & 255];
        return OkLab(r, g, b);
    }

    static (double, double, double) OkLab(double r, double g, double b)
    {
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s, 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s, 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    public static int FromOkLab(double L, double A, double B)
    {
        double l = L + 0.3963377774 * A + 0.2158037573 * B, m = L - 0.1055613458 * A - 0.0638541728 * B, s = L - 0.0894841775 * A - 1.2914855480 * B;
        l *= l * l; m *= m * m; s *= s * s;
        double r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s, g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s, b = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
        static int Enc(double c) { c = Math.Clamp(c, 0, 1); double v = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055; return (int)Math.Round(Math.Clamp(v, 0, 1) * 255); }
        return (Enc(r) << 16) | (Enc(g) << 8) | Enc(b);
    }

    public static double Distance((double L, double A, double B) x, (double L, double A, double B) y) => Math.Sqrt((x.L - y.L) * (x.L - y.L) + (x.A - y.A) * (x.A - y.A) + (x.B - y.B) * (x.B - y.B));

    // ---- analysis -----------------------------------------------------------------------------------------------------
    /// <summary>Analyse BGRA pixels (pure, deterministic). Throws <see cref="PaletteException"/> when no pixel is opaque.</summary>
    public static PaletteResult FromPixels(byte[] bgra, int width, int height)
    {
        // 1. bin opaque pixels (5 bits per channel); each bin's mean colour is converted to OKLab once
        const int Bins = 32 * 32 * 32;
        var count = new int[Bins]; var sumR = new long[Bins]; var sumG = new long[Bins]; var sumBl = new long[Bins];
        int opaque = 0;
        int n = Math.Min(bgra.Length / 4, width * height);
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            if (bgra[o + 3] < 128) continue;   // transparent or mostly transparent: not part of the picture
            int b = bgra[o], g = bgra[o + 1], r = bgra[o + 2];
            int k = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
            count[k]++; sumR[k] += r; sumG[k] += g; sumBl[k] += b;
            opaque++;
        }
        if (opaque == 0) throw new PaletteException("The image is fully transparent, so it has no colours to offer.");

        var pts = new List<(double L, double A, double B, double W)>();
        for (int k = 0; k < Bins; k++)
            if (count[k] > 0)
            {
                int rgb = (int)(sumR[k] / count[k]) << 16 | (int)(sumG[k] / count[k]) << 8 | (int)(sumBl[k] / count[k]);
                var lab = ToOkLab(rgb);
                pts.Add((lab.L, lab.A, lab.B, count[k]));
            }

        // 2. weighted k-means in OKLab, deterministic seeding: the heaviest bin, then farthest points weighted by sqrt(weight)
        int K = Math.Min(10, pts.Count);
        var centres = new List<(double L, double A, double B)> { Seed(pts) };
        while (centres.Count < K)
        {
            int best = -1; double bestScore = -1;
            for (int i = 0; i < pts.Count; i++)
            {
                double d = double.MaxValue;
                foreach (var c in centres) d = Math.Min(d, Dist2(pts[i], c));
                double sc = d * Math.Sqrt(pts[i].W);
                if (sc > bestScore) { bestScore = sc; best = i; }
            }
            if (best < 0 || bestScore <= 1e-12) break;
            centres.Add((pts[best].L, pts[best].A, pts[best].B));
        }
        var assign = new int[pts.Count];
        double[] w = Array.Empty<double>();
        for (int iter = 0; iter < 12; iter++)
        {
            var aL = new double[centres.Count]; var aA = new double[centres.Count]; var aB = new double[centres.Count]; w = new double[centres.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                int bi = 0; double bd = double.MaxValue;
                for (int c = 0; c < centres.Count; c++) { double d = Dist2(pts[i], centres[c]); if (d < bd) { bd = d; bi = c; } }
                assign[i] = bi; aL[bi] += pts[i].L * pts[i].W; aA[bi] += pts[i].A * pts[i].W; aB[bi] += pts[i].B * pts[i].W; w[bi] += pts[i].W;
            }
            for (int c = 0; c < centres.Count; c++) if (w[c] > 0) centres[c] = (aL[c] / w[c], aA[c] / w[c], aB[c] / w[c]);
        }

        // 3. clusters with their share; merge near-identical ones
        var clusters = centres.Select((c, i) => (Lab: c, Share: w.Length > i ? w[i] / opaque : 0)).Where(c => c.Share > 0).OrderByDescending(c => c.Share).ToList();
        for (int i = 0; i < clusters.Count; i++)
            for (int j = clusters.Count - 1; j > i; j--)
                if (Distance(clusters[i].Lab, clusters[j].Lab) < SameColour)
                {
                    double s = clusters[i].Share + clusters[j].Share;
                    var a = clusters[i].Lab; var b = clusters[j].Lab;
                    clusters[i] = ((((a.L * clusters[i].Share + b.L * clusters[j].Share) / s), (a.A * clusters[i].Share + b.A * clusters[j].Share) / s, (a.B * clusters[i].Share + b.B * clusters[j].Share) / s), s);
                    clusters.RemoveAt(j);
                }

        // 4. choose distinct swatches: coverage (sqrt, so small highlights count) x distance from those already chosen
        static bool Dark((double L, double A, double B) c) => c.L < 0.18;
        double Score((double L, double A, double B) c, double share) => Math.Sqrt(share) * (Dark(c) ? 0.35 : 1) * (c.L > 0.7 ? 1.4 : 1);
        var chosen = new List<((double L, double A, double B) Lab, double Share)>();
        var pool = clusters.Where(c => c.Share >= 0.004).ToList();
        if (pool.Count == 0) pool = clusters.ToList();
        var first = pool.Where(c => !Dark(c.Lab)).OrderByDescending(c => Score(c.Lab, c.Share)).Concat(pool.OrderByDescending(c => c.Share)).First();
        chosen.Add(first);
        foreach (double minD in new[] { MinDistinct, SameColour })
        {
            while (chosen.Count < MaxSwatches)
            {
                (double L, double A, double B)? pick = null; double share = 0, best = 0;
                foreach (var c in pool)
                {
                    if (chosen.Any(x => x.Lab == c.Lab)) continue;
                    if (Dark(c.Lab) && chosen.Any(x => Dark(x.Lab))) continue;   // at most one near-black shadow
                    double dmin = chosen.Min(x => Distance(x.Lab, c.Lab));
                    if (dmin < minD) continue;
                    double sc = Score(c.Lab, c.Share) * Math.Min(1, dmin / 0.25);
                    if (sc > best) { best = sc; pick = c.Lab; share = c.Share; }
                }
                if (pick == null) break;
                chosen.Add((pick.Value, share));
            }
            if (chosen.Count >= MinSwatches) break;
        }

        // 5. LED-ready colours (lift only what LEDs cannot show), darkest first
        var swatches = chosen.Select(c =>
        {
            int source = FromOkLab(c.Lab.L, c.Lab.A, c.Lab.B);
            double L = Math.Clamp(c.Lab.L, MinLedLightness, MaxLightness);
            bool lifted = c.Lab.L < MinLedLightness - 0.005;
            return (L: c.Lab.L, S: new PaletteSwatch(FromOkLab(L, c.Lab.A, c.Lab.B), source, Math.Round(c.Share, 4), lifted));
        }).OrderBy(x => x.L).Select(x => x.S).ToList();
        // two swatches can coincide after lifting: keep the first of a near-identical pair
        for (int i = swatches.Count - 1; i > 0; i--)
            for (int j = 0; j < i; j++)
                if (Distance(ToOkLab(swatches[i].Color), ToOkLab(swatches[j].Color)) < SameColour * 0.6) { swatches.RemoveAt(i); break; }

        // 6. suggestions: the lightest clear colour as the highlight (primary), a coverage-weighted mid tone as the resting colour
        var labs = swatches.Select(s => ToOkLab(s.Color)).ToList();
        int pIdx = Enumerable.Range(0, swatches.Count).OrderByDescending(i => labs[i].L).First();
        int? sIdx = Enumerable.Range(0, swatches.Count).Where(i => i != pIdx).OrderByDescending(i => labs[i].L is >= 0.3 and <= 0.62 ? swatches[i].Share + 1 : swatches[i].Share).Cast<int?>().FirstOrDefault();
        string? note = swatches.Count < MinSwatches ? "This image has few distinct colours." : null;
        return new PaletteResult(swatches, swatches[pIdx].Color, sIdx is int si ? swatches[si].Color : null, width, height, opaque, note);
    }

    static (double L, double A, double B) Seed(List<(double L, double A, double B, double W)> pts)
    {
        var p = pts.OrderByDescending(x => x.W).ThenBy(x => x.L).First();
        return (p.L, p.A, p.B);
    }

    static double Dist2((double L, double A, double B, double W) p, (double L, double A, double B) c) => (p.L - c.L) * (p.L - c.L) + (p.A - c.A) * (p.A - c.A) + (p.B - c.B) * (p.B - c.B);
}

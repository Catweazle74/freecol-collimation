using OpenCvSharp;

namespace FreeCol.Core.Imaging;

/// <summary>
/// Bereitet ein rohes Sterntest-Frame (16-bit-Bayer-FITS der ASI oder ein
/// einkanaliges Graubild) für Anzeige und Donut-Erkennung auf: 2×2-Binning
/// (entfernt das Bayer-Muster und halbiert die Auflösung) und robuste Streckung
/// auf 8 bit (unteres Plateau .. oberes Perzentil, gegen Hotpixel). Anzeige und
/// <see cref="DonutDetector"/> arbeiten danach im selben Koordinatenraum.
/// </summary>
public static class StarFramePrep
{
    public static Mat ToDisplayGray8(Mat src)
    {
        using var binned = new Mat();
        Cv2.Resize(src, binned, new Size(src.Width / 2, src.Height / 2), 0, 0, InterpolationFlags.Area);

        binned.MinMaxLoc(out double min, out double max);
        double lo = min, hi = max;
        if (max > min)
        {
            using var hist = new Mat();
            Cv2.CalcHist(new[] { binned }, new[] { 0 }, null, hist, 1, new[] { 1024 },
                new[] { new Rangef((float)min, (float)(max + 1)) });
            long total = (long)binned.Rows * binned.Cols;
            long cumLo = 0, cumHi = 0;
            // Unteres Plateau = Median (Himmel), obere Grenze = 99.95-Perzentil.
            for (int b = 0; b < 1024; b++) { cumLo += (long)hist.At<float>(b); if (cumLo >= total * 0.50) { lo = min + (max - min) * b / 1024.0; break; } }
            for (int b = 1023; b >= 0; b--) { cumHi += (long)hist.At<float>(b); if (cumHi >= total * 0.0005) { hi = min + (max - min) * b / 1024.0; break; } }
            if (hi <= lo) hi = lo + 1;
        }

        using var f = new Mat();
        binned.ConvertTo(f, MatType.CV_32F);
        Cv2.Subtract(f, new Scalar(lo), f);
        Cv2.Multiply(f, new Scalar(255.0 / (hi - lo)), f);
        var gray = new Mat();
        f.ConvertTo(gray, MatType.CV_8U);
        return gray;
    }
}

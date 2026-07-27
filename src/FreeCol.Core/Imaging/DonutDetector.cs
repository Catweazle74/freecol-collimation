using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace FreeCol.Core.Imaging;

public sealed record DonutResult(
    Point2f OuterCenter, double OuterRadius,
    Point2f InnerCenter, double InnerRadius,
    Point2f BrightnessDarkDirection, double BrightnessImbalance)
{
    /// <summary>Versatz Obstruktions-Mitte ↔ Scheibchen-Mitte (Kollimations-Signal).
    /// Bei perfekter Kollimation ≈ 0; eine Dejustage schiebt die zentrale
    /// Fangspiegel-Obstruktion aus der Mitte der defokussierten Scheibe.</summary>
    public Point2f Offset => new(InnerCenter.X - OuterCenter.X, InnerCenter.Y - OuterCenter.Y);
    public double OffsetMagnitude => Math.Sqrt(Offset.X * Offset.X + Offset.Y * Offset.Y);
    /// <summary>Obstruktionsgrad (innen/außen). Beim 150PDS physisch ~0.3;
    /// dient als Plausibilitätsmaß für die Donut-Erkennung.</summary>
    public double Obstruction => OuterRadius > 0 ? InnerRadius / OuterRadius : 0;
}

/// <summary>
/// Findet den defokussierten Stern-Donut (helle Außenscheibe + zentrale
/// Fangspiegel-Obstruktion) im Sterntest-Bild. Grobortung über die hellste
/// zusammenhängende Komponente (verwirft schwächere Begleiter/Geister-Reflexe),
/// dann Radial-Scan vom Schwerpunkt: pro Strahl das helle Ringsegment per
/// Halbwert-Schwelle (FWHM-Stil) suchen — innerer Rand = Obstruktions-Grenze,
/// äußerer Rand = Scheibchenrand. Kåsa-Fit über die gesammelten Punkte (Median-
/// Filter wirft die durch die 4 Spinnen-Speichen gerissenen Lücken raus).
///
/// Derselbe Radial-Scan liefert zusätzlich ein offset-robustes Kollimationsmaß:
/// die Randhelligkeit des Ringsegments je Strahl-Winkel. Der geometrische Versatz
/// (<see cref="DonutResult.Offset"/>) ist beim Newton kein zuverlässiges Maß, weil
/// der Fangspiegel dort absichtlich versetzt sitzt — der „Photonewton Collimation
/// Primer" nennt stattdessen die Gleichmäßigkeit der Randhelligkeit als robustes
/// Signal: eine dunklere Ringzone zeigt die Koma-Richtung, in die zu korrigieren
/// ist (<see cref="DonutResult.BrightnessDarkDirection"/>,
/// <see cref="DonutResult.BrightnessImbalance"/>).
/// </summary>
public sealed class DonutDetector
{
    public int BlurKernelPx { get; init; } = 3;
    public int RayCount { get; init; } = 144;

    /// <summary>Mindest-Fläche der Donut-Komponente (px²) — filtert Rausch-/Geister-
    /// Punkte. Default für die 2×2-gebinnten ASI2600-Frames.</summary>
    public double MinComponentArea { get; init; } = 1500;

    /// <summary>Anteil getroffener Strahlen für ein verlässliches Resultat.</summary>
    public double MinHitFraction { get; init; } = 0.4;

    public double OutlierFraction { get; init; } = 0.25;

    /// <summary>Plausible Donut-Geometrie: Obstruktionsgrad innen/außen. Außerhalb
    /// gilt es als Fehltreffer (fokusnaher Stern ⇒ ~0, zu groß ⇒ kein Donut).</summary>
    public double MinObstruction { get; init; } = 0.15;
    public double MaxObstruction { get; init; } = 0.60;

    /// <summary>Mindest-Außenradius (px) — verwirft fokusnahe Sternpunkte ohne
    /// auflösbaren Donut.</summary>
    public double MinOuterRadius { get; init; } = 12;

    public DonutResult? Detect(Mat gray)
    {
        if (gray.Empty()) return null;

        using var blurred = new Mat();
        var k = BlurKernelPx > 0 ? BlurKernelPx * 2 + 1 : 1;
        Cv2.GaussianBlur(gray, blurred, new Size(k, k), 0);

        // Halbwert-Schwelle aus Himmel (Eck-Patches) und Ring-Peak: isoliert den
        // hellen Donut-Ring unabhängig von einem diffusen Streulicht-Halo (der
        // unter dem Halbwert bleibt). Otsu zog bei den großen Donuts den Halo mit
        // herein und verschob den Schwerpunkt.
        double sky = EstimateSky(blurred);
        blurred.MinMaxLoc(out _, out double peak);
        if (peak - sky < 12) return null;
        double half = sky + (peak - sky) * 0.5;

        using var mask = new Mat();
        Cv2.Threshold(blurred, mask, half, 255, ThresholdTypes.Binary);
        // Die 4 Spinnen-Speichen zerschneiden den Ring → Close überbrückt die
        // Lücken, damit die Komponente zusammenhängt und der Schwerpunkt mittig sitzt.
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(15, 15));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

        // Hellste/größte Komponente wählen (Donut, nicht Begleiter/Geist).
        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0) return null;
        Point[]? best = null;
        double bestScore = 0;
        foreach (var ct in contours)
        {
            var area = Cv2.ContourArea(ct);
            if (area < MinComponentArea) continue;
            // Score = Fläche × mittlere Helligkeit im Bounding-Rect (bevorzugt den
            // hellen Donut gegenüber einem flächigen, aber blassen Halo).
            var r = Cv2.BoundingRect(ct);
            r &= new Rect(0, 0, blurred.Width, blurred.Height);
            using var sub = new Mat(blurred, r);
            var meanVal = Cv2.Mean(sub).Val0;
            var score = area * meanVal;
            if (score > bestScore) { bestScore = score; best = ct; }
        }
        if (best is null) return null;

        var m = Cv2.Moments(best);
        if (m.M00 <= 0) return null;
        var c0 = new Point2f((float)(m.M10 / m.M00), (float)(m.M01 / m.M00));
        var bbox = Cv2.BoundingRect(best);
        double rOuterGuess = 0.5 * Math.Max(bbox.Width, bbox.Height);
        double rMax = rOuterGuess * 1.35;

        var indexer = blurred.GetGenericIndexer<byte>();
        var innerPts = new List<Point2f>(RayCount);
        var outerPts = new List<Point2f>(RayCount);
        var ringBrightness = new List<(double Theta, double Mean)>(RayCount);
        for (int i = 0; i < RayCount; i++)
        {
            double th = 2 * Math.PI * i / RayCount;
            double cosT = Math.Cos(th), sinT = Math.Sin(th);

            double? rIn = null, rOut = null;
            double brightSum = 0;
            int brightCount = 0;
            bool dipBelowHalf = false;
            // Läuft ein Abfall unter die Halbwert-Schwelle später wieder in
            // einen Anstieg (rOut wird erneut gesetzt), war es ein Einbruch
            // MITTEN im Ring (Spinnen-Speiche) statt des normalen Abfalls
            // hinter dem äußeren Rand ins Himmel-Niveau (der bleibt bis rMax
            // unten und darf NICHT als Speiche gewertet werden).
            bool pendingDip = false;
            for (double r = 1; r < rMax; r += 1.0)
            {
                int x = (int)Math.Round(c0.X + r * cosT);
                int y = (int)Math.Round(c0.Y + r * sinT);
                if (x < 0 || y < 0 || x >= blurred.Width || y >= blurred.Height) break;
                int v = indexer[y, x];
                if (rIn is null) { if (v >= half) rIn = r; }       // erster Anstieg über Halbwert
                else
                {
                    // Ringsegment zwischen innerem und äußerem Rand: mittlere
                    // Helligkeit für die Fourier-Auswertung mitschreiben.
                    if (v >= half)
                    {
                        if (pendingDip) { dipBelowHalf = true; pendingDip = false; }
                        rOut = r; brightSum += v; brightCount++;
                    }
                    else { pendingDip = true; }
                }
            }
            if (rIn is double ri && rOut is double ro && ro > ri)
            {
                innerPts.Add(new Point2f((float)(c0.X + ri * cosT), (float)(c0.Y + ri * sinT)));
                outerPts.Add(new Point2f((float)(c0.X + ro * cosT), (float)(c0.Y + ro * sinT)));
                if (!dipBelowHalf && brightCount > 0)
                    ringBrightness.Add((th, brightSum / brightCount));
            }
        }

        var outer = FitFiltered(outerPts, c0);
        var inner = FitFiltered(innerPts, c0);
        if (outer is null || inner is null) return null;

        var (darkDirection, imbalance) = ComputeBrightnessAsymmetry(ringBrightness);
        var result = new DonutResult(outer.Value.Center, outer.Value.Radius,
                                     inner.Value.Center, inner.Value.Radius,
                                     darkDirection, imbalance);
        // Plausibilität: fokusnaher Stern (Obstr→0, winziger Radius) oder
        // entartete Fits verwerfen.
        if (result.OuterRadius < MinOuterRadius) return null;
        if (result.Obstruction < MinObstruction || result.Obstruction > MaxObstruction) return null;
        return result;
    }

    private static double EstimateSky(Mat gray)
    {
        // Median der vier Eck-Patches (Himmel-Hintergrund).
        int w = gray.Width, h = gray.Height, s = Math.Min(w, h) / 10;
        var rects = new[]
        {
            new Rect(0, 0, s, s), new Rect(w - s, 0, s, s),
            new Rect(0, h - s, s, s), new Rect(w - s, h - s, s, s),
        };
        double sum = 0;
        foreach (var r in rects) using (var sub = new Mat(gray, r)) sum += Cv2.Mean(sub).Val0;
        return sum / rects.Length;
    }

    /// <summary>
    /// Erste Fourier-Komponente der Randhelligkeit über dem Strahl-Winkel:
    /// f(θ) ≈ Mittelwert + a1·cos(θ) + b1·sin(θ), mit a1/b1 als diskrete
    /// Fourier-Koeffizienten (2/N-Normierung, Standardformel für gleichverteilt
    /// abgetastete periodische Signale). Amplitude √(a1²+b1²) und Phase
    /// atan2(b1, a1) zeigen die HELLSTE Ringzone; die zurückgegebene Richtung
    /// zeigt genau entgegengesetzt zur DUNKELSTEN Zone (Phase + π), passend zum
    /// Primer: die Koma-Korrekturrichtung ist die Richtung der dunkelsten Kante.
    ///
    /// Normierung von <see cref="DonutResult.BrightnessImbalance"/>: Amplitude
    /// relativ zur mittleren Ringhelligkeit. Beide Größen skalieren linear mit
    /// der absoluten Bildhelligkeit (Belichtungszeit, Gain, Himmelshelligkeit) —
    /// das Verhältnis kürzt diesen Skalierungsfaktor heraus und bleibt damit ein
    /// reines Formmaß, vergleichbar zwischen Aufnahmen unterschiedlicher Belichtung.
    /// </summary>
    private static (Point2f DarkDirection, double Imbalance) ComputeBrightnessAsymmetry(
        List<(double Theta, double Mean)> ringBrightness)
    {
        if (ringBrightness.Count == 0) return (new Point2f(0, 0), 0);

        double meanBrightness = 0;
        foreach (var (_, mean) in ringBrightness) meanBrightness += mean;
        meanBrightness /= ringBrightness.Count;
        if (meanBrightness <= 0) return (new Point2f(0, 0), 0);

        double a1 = 0, b1 = 0;
        foreach (var (theta, mean) in ringBrightness)
        {
            a1 += mean * Math.Cos(theta);
            b1 += mean * Math.Sin(theta);
        }
        var n = ringBrightness.Count;
        a1 = 2.0 * a1 / n;
        b1 = 2.0 * b1 / n;

        var amplitude = Math.Sqrt(a1 * a1 + b1 * b1);
        if (amplitude <= 1e-9) return (new Point2f(0, 0), 0);

        // Richtung der hellsten Zone ist (a1, b1)/Amplitude; die dunkelste Zone
        // liegt genau gegenüber (Phase + π) → Vorzeichen umkehren.
        var darkDirection = new Point2f((float)(-a1 / amplitude), (float)(-b1 / amplitude));
        var imbalance = amplitude / meanBrightness;
        return (darkDirection, imbalance);
    }

    private (Point2f Center, double Radius)? FitFiltered(List<Point2f> pts, Point2f c0)
    {
        if (pts.Count < Math.Max(5, MinHitFraction * RayCount)) return null;
        var radii = new double[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            var dx = pts[i].X - c0.X; var dy = pts[i].Y - c0.Y;
            radii[i] = Math.Sqrt(dx * dx + dy * dy);
        }
        var sorted = (double[])radii.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        var tol = Math.Max(6.0, median * OutlierFraction);
        var keep = new List<Point2f>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
            if (Math.Abs(radii[i] - median) <= tol) keep.Add(pts[i]);
        if (keep.Count < Math.Max(5, MinHitFraction * RayCount)) return null;
        try { var c = CircleFit.Fit(keep); return (c.Center, c.Radius); }
        catch { return null; }
    }
}

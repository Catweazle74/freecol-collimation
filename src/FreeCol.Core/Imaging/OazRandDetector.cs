using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace FreeCol.Core.Imaging;

public sealed record OazRandResult(Point2f Center, double Radius);

/// <summary>
/// Findet das untere Ende des OAZ-Rohrs (die hellste, runde Kante in der OCAL-
/// Ansicht) über die Hough-Kreis-Transformation. Im Code historisch
/// „Tubusrand" — physikalisch ist das aber das Drawtube-Ende des Okular-
/// auszugs, nicht der Außenkörper des Teleskops (Tubus = OTA-Körper).
/// Hough ist robust gegen einseitige Reflexionen und unvollständige Kanten,
/// weil es Edge-Pixel über das gesamte Bild auf gemeinsame Kreis-Parameter
/// abstimmen lässt — anders als <c>FitEllipse</c> auf einer einzelnen Kontur,
/// das bei einseitiger Helligkeit den Mittelpunkt zur kontur-stärkeren Seite
/// zieht.
/// </summary>
public sealed class OazRandDetector
{
    public int BlurKernelPx { get; init; } = 3;

    /// <summary>Canny-Oberschwelle für HOUGH_GRADIENT_ALT.</summary>
    public double CannyHighThreshold { get; init; } = 300;

    /// <summary>
    /// HOUGH_GRADIENT_ALT-„Perfectness": Mindest-Anteil der Kreis-Kontur, der
    /// durch Edge-Punkte gestützt sein muss (0..1). Hoch → nur voll-runde Kreise.
    /// 0.80 (vorher 0.90): die FS-Halterung an einer Seite des Tubus drückt den
    /// Außenrand-Roundness-Score gerade unter 0.85 — bei 0.9 fiel der Tubus
    /// komplett raus, bei 0.80 wird er stabil gefunden. Der Floor 0.25 fängt die
    /// vorher per Roundness verworfene Velour-/FS-Innenkante (r ≈ 0.18·shortDim)
    /// immer noch sauber ab.
    /// </summary>
    public double Roundness { get; init; } = 0.80;

    public double Dp { get; init; } = 1.5;

    /// <summary>
    /// Mindestradius relativ zur kürzeren Bildkante. Bewusst hoch (0.25): der
    /// Tubus-Außenrand füllt die OCAL-Ansicht fast aus (~0.41), während der
    /// innere Velour-/FS-Rand bei ~0.18 liegt. Der hohe Floor schließt den
    /// Innenrand aus — ein unscharfes Frame liefert dann lieber null als den
    /// falschen kleinen Kreis, der sonst die Mehrframe-Mittelung verfälscht.
    /// Wird als Post-Filter angewendet, nicht als HoughCircles-Eingabe:
    /// HOUGH_GRADIENT_ALT räumt bei minRadius ≥ ~0.18·shortDim den Außenrand-
    /// Kandidaten aus dem Accumulator (verifiziert an einer 1920×1080-Fixture),
    /// d.h. ein hoher Hough-minRadius löscht den eigentlich gewünschten Kreis.
    /// </summary>
    public double MinRadiusFraction { get; init; } = 0.25;
    public double MaxRadiusFraction { get; init; } = 0.55;

    /// <summary>
    /// HoughCircles-Suchunter­grenze relativ zur kürzeren Bildkante. Niedrig
    /// genug, dass der Algorithmus den Außenrand auf jeden Fall in den
    /// Accumulator aufnimmt; die Auswahl filtert dann mit <see cref="MinRadiusFraction"/>.
    /// </summary>
    public double HoughMinRadiusFraction { get; init; } = 0.10;

    /// <summary>
    /// Anzahl Refine-Strahlen rund um den Hough-Kreis. 72 (alle 5°) gibt eine
    /// gute Über-Abdeckung — wenn die FS-Halterung 20-30° verdeckt, bleiben
    /// noch 60+ Strahlen für den Fit.
    /// </summary>
    public int RefineRayCount { get; init; } = 72;

    /// <summary>
    /// Mindest-Gradient (hell → dunkel über 6 Pixel) der als Tubus-Außenkante
    /// gewertet wird. Höher = mehr Strahlen werden verworfen, dafür kein
    /// Sub-Highlight (FS-Gehäuse, Reflexe) als Kante mitgemittelt.
    /// </summary>
    public int RefineMinGradient { get; init; } = 30;

    /// <summary>
    /// Suchradius um den Hough-Radius, in dem pro Strahl der stärkste Gradient
    /// gesucht wird (px ≈ max(20, 0.10 × R)). Großzügig, damit auch eine
    /// 30-40 px-Verschiebung des Hough-Center durch Asymmetrie eingefangen wird.
    /// </summary>
    public double RefineSearchFraction { get; init; } = 0.10;

    /// <summary>
    /// Mindestanteil getroffener Strahlen, ohne den der Refine-Schritt verworfen
    /// und das ungerefinete Hough-Ergebnis zurückgegeben wird.
    /// </summary>
    public double RefineMinHitFraction { get; init; } = 0.5;

    public OazRandResult? Detect(Mat gray)
    {
        if (gray.Empty()) return null;

        using var blurred = new Mat();
        var k = BlurKernelPx > 0 ? BlurKernelPx * 2 + 1 : 1;
        Cv2.GaussianBlur(gray, blurred, new Size(k, k), 0);

        var shortDim = Math.Min(gray.Width, gray.Height);
        var houghMinRadius = (int)(HoughMinRadiusFraction * shortDim);
        var acceptMinRadius = (int)(MinRadiusFraction * shortDim);
        var maxRadius = (int)(MaxRadiusFraction * shortDim);

        var circles = Cv2.HoughCircles(
            blurred, HoughModes.GradientAlt,
            dp: Dp,
            minDist: shortDim / 2.0,
            param1: CannyHighThreshold,
            param2: Roundness,
            minRadius: houghMinRadius,
            maxRadius: maxRadius);
        if (circles.Length == 0) return null;

        CircleSegment? best = null;
        foreach (var c in circles)
        {
            // Innenrand/Velour-Kandidaten per Floor verwerfen, größten Außenrand
            // behalten — damit ein einzelner Frame ohne Außenrand-Treffer null
            // liefert statt die Mehrframe-Mittelung mit einem Innenrand zu gift­en.
            if (c.Radius < acceptMinRadius) continue;
            if (best is null || c.Radius > best.Value.Radius)
            {
                best = c;
            }
        }

        if (best is null) return null;

        // HOUGH_GRADIENT_ALT schätzt den Radius gut, aber wenn die FS-Halterung an
        // einer Seite den Außenrand verdeckt, zieht sie das Zentrum systematisch
        // dorthin. Strahl-Refine: pro Strahl die echte Hell→Dunkel-Transition
        // (max v(r-3) − v(r+3) über ein Fenster) lokalisieren, occludierte
        // Strahlen (Gradient zu schwach) verwerfen, einen Kreis durch die
        // sauberen Punkte fitten. Auf einer 1920×1080-Fixture senkt das Δc von
        // 34 px (FS-Halterung-Bias) auf ~6 px.
        var refined = RefineByEdgeRays(blurred, best.Value.Center, best.Value.Radius);
        if (refined is { } rr) return new OazRandResult(rr.Center, rr.Radius);

        return new OazRandResult(best.Value.Center, best.Value.Radius);
    }

    private (Point2f Center, double Radius)? RefineByEdgeRays(Mat blurred, Point2f initialCenter, double initialRadius)
    {
        var search = Math.Max(20.0, initialRadius * RefineSearchFraction);
        var w = blurred.Width;
        var h = blurred.Height;
        var points = new List<Point2f>(RefineRayCount);

        for (int i = 0; i < RefineRayCount; i++)
        {
            double theta = 2 * Math.PI * i / RefineRayCount;
            double co = Math.Cos(theta);
            double si = Math.Sin(theta);
            double bestEdgeR = double.NaN;
            int bestMag = 0;
            for (double r = initialRadius - search; r <= initialRadius + search; r += 1.0)
            {
                int xi = (int)Math.Round(initialCenter.X + co * (r - 3));
                int yi = (int)Math.Round(initialCenter.Y + si * (r - 3));
                int xo = (int)Math.Round(initialCenter.X + co * (r + 3));
                int yo = (int)Math.Round(initialCenter.Y + si * (r + 3));
                if (xi < 0 || yi < 0 || xi >= w || yi >= h) continue;
                if (xo < 0 || yo < 0 || xo >= w || yo >= h) continue;
                int vIn = blurred.At<byte>(yi, xi);
                int vOut = blurred.At<byte>(yo, xo);
                int mag = vIn - vOut; // Tubus innen hell, außen dunkel → positiv
                if (mag > bestMag) { bestMag = mag; bestEdgeR = r; }
            }
            if (bestMag >= RefineMinGradient && !double.IsNaN(bestEdgeR))
            {
                points.Add(new Point2f(
                    (float)(initialCenter.X + co * bestEdgeR),
                    (float)(initialCenter.Y + si * bestEdgeR)));
            }
        }

        var minHits = (int)Math.Max(8, RefineRayCount * RefineMinHitFraction);
        if (points.Count < minHits) return null;
        try
        {
            var fit = CircleFit.Fit(points);
            return (fit.Center, fit.Radius);
        }
        catch
        {
            return null;
        }
    }
}

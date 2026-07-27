using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace FreeCol.Core.Imaging;

public sealed record SekundaerSilhouetteResult(
    Point2f Center,
    double RadiusX,
    double RadiusY,
    double AngleDeg);

/// <summary>
/// Findet die äußere Kante der Sekundärsilhouette per Radial-Scan vom
/// Hauptspiegel-Reflex-Zentrum nach außen. In jeder Strahlrichtung vom HSR-
/// Rand bis zum OAZ-Rand wird die erste signifikante Dunkel→Hell-Transition
/// erfasst — dort beginnt der helle Außenbereich, also endet die Sekundär-
/// silhouette. Aus den gefundenen Punkten fitten wir eine Ellipse.
/// </summary>
public sealed class SekundaerSilhouetteDetector
{
    public int BlurKernelPx { get; init; } = 3;

    /// <summary>Anzahl Strahlen — 72 entspricht alle 5°.</summary>
    public int RayCount { get; init; } = 72;

    /// <summary>
    /// Minimaler Helligkeits-Gradient (über 4 Pixel) der als Sekundär-Außenkante
    /// gewertet wird. Bewusst hoch (30): das polierte FS-Gehäuse zwischen HSR
    /// und OAZ-Rand erzeugt eine matte Dark→Bright-Stufe mit Δ≈20-25, die den
    /// Außenrand-Treffer (Δ≥30, oft Δ≥60) maskiert. Höhere Schwelle springt
    /// über das Sub-Highlight des Gehäuses und trifft den echten Silhouette-Rand.
    /// </summary>
    public int MinGradient { get; init; } = 30;

    /// <summary>
    /// Mindest-Anteil getroffener Strahlen — wenn weniger als so viel Strahlen
    /// eine Kante finden, ist das Resultat unzuverlässig.
    /// </summary>
    public double MinHitFraction { get; init; } = 0.5;

    /// <summary>
    /// Maximaler Abstand eines Strahlpunkts vom Median-Radius (als Bruchteil
    /// des Median). Punkte außerhalb dieser Toleranz gelten als Spider-Arm-
    /// oder Reflex-Ausreißer und fließen nicht in den Ellipsen-Fit ein.
    /// </summary>
    public double OutlierFraction { get; init; } = 0.20;

    public SekundaerSilhouetteResult? Detect(
        Mat gray,
        OazRandResult? outerHint = null,
        HauptspiegelReflexResult? innerHint = null)
    {
        if (gray.Empty() || outerHint is null || innerHint is null) return null;

        using var blurred = new Mat();
        var k = BlurKernelPx > 0 ? BlurKernelPx * 2 + 1 : 1;
        Cv2.GaussianBlur(gray, blurred, new Size(k, k), 0);

        var indexer = blurred.GetGenericIndexer<byte>();
        var center = innerHint.Center;
        var startR = innerHint.Radius + 3;
        var maxR = outerHint.Radius - 2;
        if (maxR <= startR) return null;

        var points = new List<Point2f>(RayCount);
        for (int i = 0; i < RayCount; i++)
        {
            double theta = 2 * Math.PI * i / RayCount;
            double cosT = Math.Cos(theta);
            double sinT = Math.Sin(theta);

            for (double r = startR; r + 4 < maxR; r += 1.0)
            {
                int x0 = (int)Math.Round(center.X + r * cosT);
                int y0 = (int)Math.Round(center.Y + r * sinT);
                int x1 = (int)Math.Round(center.X + (r + 4) * cosT);
                int y1 = (int)Math.Round(center.Y + (r + 4) * sinT);
                if (x0 < 0 || y0 < 0 || x0 >= blurred.Width || y0 >= blurred.Height) break;
                if (x1 < 0 || y1 < 0 || x1 >= blurred.Width || y1 >= blurred.Height) break;

                int v0 = indexer[y0, x0];
                int v1 = indexer[y1, x1];
                if (v1 - v0 >= MinGradient)
                {
                    points.Add(new Point2f((float)(center.X + r * cosT), (float)(center.Y + r * sinT)));
                    break;
                }
            }
        }

        if (points.Count < Math.Max(5, MinHitFraction * RayCount)) return null;

        // Spider-Arme verfärben den Bereich entlang ihrer Richtung dunkel — der
        // Radialscan findet dort die "Kante" erst am OAZ-Rand und produziert
        // einzelne Ausreißer-Punkte, die FitEllipse zu einer schief verzerrten
        // Ellipse machen. Median-Filterung der Radien wirft die Ausreißer raus.
        var radii = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            var dx = points[i].X - center.X;
            var dy = points[i].Y - center.Y;
            radii[i] = Math.Sqrt(dx * dx + dy * dy);
        }
        var sorted = (double[])radii.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        var tolerance = Math.Max(8.0, median * OutlierFraction);

        var filtered = new List<Point2f>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            if (Math.Abs(radii[i] - median) <= tolerance) filtered.Add(points[i]);
        }

        if (filtered.Count < Math.Max(5, MinHitFraction * RayCount)) return null;

        // Kåsa-Circle-Fit ist gegenüber Winkel-Lücken (Spider-Arme, fehlende
        // Kanten) deutlich stabiler als FitEllipse, das bei unvollständigem
        // Punktegürtel die Achsen verzerrt. Für die Auto-Markierung reicht ein
        // Kreis — Exzentrizität kann der Nutzer per Drag/Nudge nachträglich
        // setzen, wenn die Sekundär wirklich elliptisch wirkt.
        try
        {
            var circle = CircleFit.Fit(filtered);
            return new SekundaerSilhouetteResult(circle.Center, circle.Radius, circle.Radius, 0);
        }
        catch
        {
            return null;
        }
    }
}

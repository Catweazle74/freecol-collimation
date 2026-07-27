using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace FreeCol.Core.Imaging;

public sealed record FangspiegelReflexResult(Point2f Center, double Radius);

/// <summary>
/// Findet die dunkle Fangspiegel-Reflexionsscheibe innerhalb des hellen
/// Hauptspiegel-Reflexes (HSR). Im OCAL-Zentralbild hängt der Fangspiegel als
/// runde dunkle Scheibe an der Spinne; in ihr liegen Kameragehäuse, Marker-Ring
/// und Linse. Marker und Linse befinden sich IMMER innerhalb dieser Scheibe —
/// der Rand taugt damit als äußere Such-/Plausibilitätsgrenze für beide.
///
/// Strategie analog zum <see cref="SekundaerSilhouetteDetector"/>, nur eine
/// Größenordnung kleiner: Radial-Scan vom HSR-Zentrum nach außen. Der Scan
/// startet jenseits des hellen Marker-Rings (im dunklen Scheibenkörper) und
/// sucht je Strahl die erste signifikante Dunkel→Hell-Transition — dort endet
/// die FS-Scheibe und beginnt wieder der helle HSR. Median-Filter wirft die
/// Spinnen-Arm-/Abtropf-Ausreißer raus, Kåsa-Fit liefert den Kreis.
/// </summary>
public sealed class FangspiegelReflexDetector
{
    public int BlurKernelPx { get; init; } = 3;

    /// <summary>Anzahl Strahlen — 72 entspricht alle 5°.</summary>
    public int RayCount { get; init; } = 72;

    /// <summary>
    /// Start-Radius des Scans relativ zum HSR-Radius. Muss den hellen Marker-Ring
    /// (samt rötlichem Gehäuse-Zentrum) überspringen, damit der Scan im dunklen
    /// Scheibenkörper beginnt — sonst triggert schon der Ring→Gehäuse-Übergang.
    /// HSR-Radius ≈ 150 px, Marker-Ring-Außenradius ≈ 12-15 px ⇒ 0.13 ≈ 20 px.
    /// </summary>
    public double InnerStartFractionOfHsr { get; init; } = 0.13;

    /// <summary>
    /// Maximaler Scan-Radius relativ zum HSR-Radius. Die FS-Scheibe füllt den HSR
    /// nie aus (beobachtet r/HSR ≈ 0.45); 0.75 lässt Reserve, ohne bis zum HSR-
    /// Rand selbst zu scannen.
    /// </summary>
    public double OuterEndFractionOfHsr { get; init; } = 0.75;

    /// <summary>
    /// Minimaler Helligkeits-Gradient (über 4 Pixel) der als FS-Außenkante gilt.
    /// Der Sprung von der dunklen Scheibe zum hellen HSR ist kräftig (Δ ≫ 60);
    /// 30 filtert schwache Innen-Strukturen (Gehäusekante) zuverlässig weg.
    /// </summary>
    public int MinGradient { get; init; } = 30;

    /// <summary>
    /// Mindest-Anteil getroffener Strahlen für ein verlässliches Resultat.
    /// </summary>
    public double MinHitFraction { get; init; } = 0.4;

    /// <summary>
    /// Maximaler Abstand eines Strahlpunkts vom Median-Radius (Bruchteil des
    /// Median). Punkte außerhalb gelten als Spinnen-Arm-/Abtropf-Ausreißer.
    /// </summary>
    public double OutlierFraction { get; init; } = 0.25;

    public FangspiegelReflexResult? Detect(Mat gray, HauptspiegelReflexResult? hsrHint)
    {
        if (gray.Empty() || hsrHint is null) return null;

        using var blurred = new Mat();
        var k = BlurKernelPx > 0 ? BlurKernelPx * 2 + 1 : 1;
        Cv2.GaussianBlur(gray, blurred, new Size(k, k), 0);

        var indexer = blurred.GetGenericIndexer<byte>();
        var center = hsrHint.Center;
        var startR = hsrHint.Radius * InnerStartFractionOfHsr;
        var maxR = hsrHint.Radius * OuterEndFractionOfHsr;
        if (maxR <= startR + 4) return null;

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
        var tolerance = Math.Max(6.0, median * OutlierFraction);

        var filtered = new List<Point2f>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            if (Math.Abs(radii[i] - median) <= tolerance) filtered.Add(points[i]);
        }

        if (filtered.Count < Math.Max(5, MinHitFraction * RayCount)) return null;

        try
        {
            var circle = CircleFit.Fit(filtered);
            return new FangspiegelReflexResult(circle.Center, circle.Radius);
        }
        catch
        {
            return null;
        }
    }
}

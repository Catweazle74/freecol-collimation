using System;
using OpenCvSharp;

namespace FreeCol.Core.Imaging;

public sealed record MarkerRingResult(Point2f Center, float Radius);

/// <summary>
/// Findet den weißen, matten Mittelmarker-Ring (10/6&#160;mm) auf dem Hauptspiegel.
/// Im OCAL-Bild erscheint dieser Ring NICHT als helles, sondern als
/// kontrastarmes, oliv getöntes Ringfeature im orangen Hauptspiegel-Reflex —
/// die helle, scharfe „weiße Ring + dunkle Linse"-Struktur daneben ist der
/// Linsen-Reflex und muss ignoriert werden.
///
/// Strategie: enger ROI um einen Hinweis-Punkt (Klick oder HSR-Zentrum),
/// Kontrastanhebung (CLAHE) und Hough-Kreis mit kleinem Radiusband. Von den
/// Kandidaten gewinnt der dem Hinweis NÄCHSTE Kreis — der echte Marker sitzt
/// nahe am optischen Zentrum, der Linsen-Reflex ist um den Kollimationsfehler
/// versetzt. Das verwirft den Reflex zuverlässig.
/// </summary>
public sealed class MarkerRingDetector
{
    /// <summary>Halbe Kantenlänge des Such-ROI um den Hinweis (Pixel).</summary>
    public int SearchRadiusPx { get; init; } = 30;

    public int MinRadiusPx { get; init; } = 6;
    public int MaxRadiusPx { get; init; } = 18;

    public double Dp { get; init; } = 1.0;
    public double CannyHighThreshold { get; init; } = 120;

    /// <summary>
    /// Hough-Akkumulator-Schwelle. Niedriger = mehr (auch schwache) Kreise;
    /// ~25 isoliert den blassen Marker sauber, ohne den Reflex einzusammeln.
    /// </summary>
    public double AccumulatorThreshold { get; init; } = 25;

    public double ClaheClipLimit { get; init; } = 2.0;

    /// <summary>
    /// Sucht den Marker-Ring nahe <paramref name="hintX"/>/<paramref name="hintY"/>.
    /// Liefert Mittelpunkt und (Außen-)Radius in Bildkoordinaten, oder null.
    /// </summary>
    public MarkerRingResult? Detect(Mat gray, double hintX, double hintY)
    {
        if (gray.Empty()) return null;

        var win = SearchRadiusPx;
        var roi = new Rect((int)hintX - win, (int)hintY - win, 2 * win, 2 * win)
                  & new Rect(0, 0, gray.Width, gray.Height);
        if (roi.Width < 2 * MinRadiusPx || roi.Height < 2 * MinRadiusPx) return null;

        using var crop = new Mat(gray, roi);
        using var eq = new Mat();
        using (var clahe = Cv2.CreateCLAHE(ClaheClipLimit, new Size(8, 8)))
        {
            clahe.Apply(crop, eq);
        }
        Cv2.GaussianBlur(eq, eq, new Size(3, 3), 0);

        var circles = Cv2.HoughCircles(
            eq, HoughModes.Gradient,
            dp: Dp,
            minDist: Math.Max(8, MinRadiusPx),
            param1: CannyHighThreshold,
            param2: AccumulatorThreshold,
            minRadius: MinRadiusPx,
            maxRadius: MaxRadiusPx);
        if (circles is null || circles.Length == 0) return null;

        var click = new Point2f((float)(hintX - roi.X), (float)(hintY - roi.Y));
        var best = circles[0];
        var bestDist = double.MaxValue;
        foreach (var c in circles)
        {
            var dx = c.Center.X - click.X;
            var dy = c.Center.Y - click.Y;
            var d = dx * dx + dy * dy;
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }

        return new MarkerRingResult(
            new Point2f(roi.X + best.Center.X, roi.Y + best.Center.Y),
            best.Radius);
    }
}

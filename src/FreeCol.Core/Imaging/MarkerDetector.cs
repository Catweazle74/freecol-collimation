using System;
using OpenCvSharp;

namespace FreeCol.Core.Imaging;

public sealed record MarkerResult(Point2f Center, float Radius);

/// <summary>
/// Findet die OCAL-Eigenlinse als runden dunklen Kreis innerhalb des
/// Hauptspiegel-Reflexes — die schwarze Linsen-Öffnung der Kamera, die als
/// runder Schatten im hellen Spiegelreflex erscheint. Sie ist das IST der
/// Hauptspiegel-Kipp-Phase: durch Kippen wird sie in den Marker-Punkt gezogen
/// (der Marker-Ring selbst kommt aus <see cref="MarkerRingDetector"/>).
/// Hough-Kreis-Transformation ist dafür robuster als <c>MinMaxLoc</c>, weil sie
/// über mehrere Edge-Pixel abstimmt und nicht von einzelnen Rand-Artefakten am
/// OCAL-Gehäuse fehlgeleitet wird.
///
/// Die Suche wird auf einen Radiusbereich begrenzt: die Linse ist etwa so groß
/// wie der Marker-Ring. Ohne diese Schranke greift Hough sonst die viel größere
/// Fangspiegel-Reflexion ab. Der Aufrufer leitet den Bereich aus dem bereits
/// platzierten Marker-Radius ab (siehe DetectLinseMarking).
/// </summary>
public sealed class MarkerDetector
{
    public int BlurKernelPx { get; init; } = 3;
    public double CannyHighThreshold { get; init; } = 100;
    public double AccumulatorThreshold { get; init; } = 12;
    public double Dp { get; init; } = 1.0;

    /// <summary>
    /// Sucht die dunkle Linsen-Scheibe mit Radius in [<paramref name="minRadius"/>,
    /// <paramref name="maxRadius"/>] und nimmt den Kandidaten, der dem Suchzentrum
    /// (<paramref name="centerX"/>, <paramref name="centerY"/>) am nächsten liegt,
    /// sofern er innerhalb <paramref name="maxCenterOffset"/> davon liegt.
    /// </summary>
    public MarkerResult? Detect(
        Mat gray,
        double centerX, double centerY,
        double minRadius, double maxRadius,
        double maxCenterOffset)
    {
        if (gray.Empty()) return null;

        using var blurred = new Mat();
        var k = BlurKernelPx > 0 ? BlurKernelPx * 2 + 1 : 1;
        Cv2.GaussianBlur(gray, blurred, new Size(k, k), 0);

        int minR = (int)Math.Max(2, Math.Round(minRadius));
        int maxR = (int)Math.Max(minR + 1, Math.Round(maxRadius));
        var circles = Cv2.HoughCircles(
            blurred, HoughModes.Gradient,
            dp: Dp,
            minDist: Math.Max(10, minR),
            param1: CannyHighThreshold,
            param2: AccumulatorThreshold,
            minRadius: minR,
            maxRadius: maxR);
        if (circles.Length == 0) return null;

        CircleSegment? best = null;
        var bestDist = double.MaxValue;
        foreach (var c in circles)
        {
            var dx = c.Center.X - centerX;
            var dy = c.Center.Y - centerY;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > maxCenterOffset) continue;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = c;
            }
        }
        return best is { } b ? new MarkerResult(b.Center, b.Radius) : null;
    }
}

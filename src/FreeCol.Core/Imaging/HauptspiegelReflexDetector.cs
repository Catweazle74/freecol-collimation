using System;
using OpenCvSharp;

namespace FreeCol.Core.Imaging;

public sealed record HauptspiegelReflexResult(Point2f Center, double Radius);

/// <summary>
/// Findet die helle Hauptspiegel-Reflex-Disc per Hough-Kreis-Transformation
/// innerhalb des OAZ-Rohr-Endes. Hough wählt das Zentrum stabiler als ein
/// FitEllipse auf der hellsten Disc-Kontur, vor allem wenn der Reflex einseitig
/// glänzt. Radius wird auf einen Bruchteil des OAZ-Rand-Radius beschränkt —
/// der Hauptspiegel-Reflex füllt das OAZ-Sichtfeld nie ganz aus.
/// </summary>
public sealed class HauptspiegelReflexDetector
{
    public int BlurKernelPx { get; init; } = 3;
    public double CannyHighThreshold { get; init; } = 100;
    public double AccumulatorThreshold { get; init; } = 25;

    /// <summary>
    /// Falls die strenge Schwelle keinen Kandidaten liefert, wird ein zweiter
    /// Durchgang mit dieser gelockerten Schwelle versucht. Verhindert Kaskaden-
    /// Fehler (Sekundär/Marker bleiben aus), wenn der HSR-Reflex in einem
    /// einzelnen Frame nur schwache Edges hat.
    /// </summary>
    public double FallbackAccumulatorThreshold { get; init; } = 15;

    public double Dp { get; init; } = 1.0;

    /// <summary>
    /// 0.40 (vorher 0.35): seit OazRandDetector den Hough-Radius per Edge-
    /// Refine auf die tatsächliche Bright→Dark-Transition zieht (typischerweise
    /// 4-8% kleiner als die zuvor genannten Werte), liegt der reale HSR-Anteil
    /// (~0.33) bei 0.35 borderline — beim ersten Frame-Test wurde der r=146-
    /// Kandidat hart abgeschnitten. 0.40 gibt eine gesunde Reserve, ohne den
    /// FS-Sekundärspiegel-Reflex einzufangen (würde Verhältnisse > 0.5 brauchen).
    /// </summary>
    public double MaxRadiusFractionOfHint { get; init; } = 0.40;

    /// <summary>
    /// Mindestradius relativ zum OAZ-Rand-Radius. Analog zum OAZ-Floor: schließt
    /// kleine Sub-Strukturen innerhalb des HSR-Reflexes (Marker, Linse, Glanz-
    /// punkte) aus, die Hough bei gelockertem Akkumulator gerne als eigene Kreise
    /// findet. Der echte HSR liegt bei r/OAZ ≈ 0.33; 0.20 lässt Halbierungs-
    /// Reserve und entfernt Treffer mit r &lt; ~0.2 × OAZ zuverlässig.
    /// </summary>
    public double MinRadiusFractionOfHint { get; init; } = 0.20;

    /// <summary>
    /// Maximaler Abstand des HSR-Zentrums vom OAZ-Rand-Zentrum, relativ zum
    /// OAZ-Rand-Radius. Verhindert, dass ein zufälliger heller Kreis am Bildrand
    /// (Streureflex an der OAZ-Innenwand) als HSR durchgeht — der HSR sitzt
    /// optisch nahe der OAZ-Achse.
    /// </summary>
    public double MaxCenterOffsetFractionOfHint { get; init; } = 0.3;

    public HauptspiegelReflexResult? Detect(Mat gray, OazRandResult? hint = null)
    {
        if (gray.Empty()) return null;

        var primary = DetectAt(gray, hint, AccumulatorThreshold);
        if (primary is not null) return primary;
        if (FallbackAccumulatorThreshold < AccumulatorThreshold)
        {
            return DetectAt(gray, hint, FallbackAccumulatorThreshold);
        }
        return null;
    }

    private HauptspiegelReflexResult? DetectAt(Mat gray, OazRandResult? hint, double accumulator)
    {
        using var blurred = new Mat();
        var k = BlurKernelPx > 0 ? BlurKernelPx * 2 + 1 : 1;
        Cv2.GaussianBlur(gray, blurred, new Size(k, k), 0);

        var shortDim = Math.Min(gray.Width, gray.Height);
        int minRadius = hint is not null
            ? (int)(hint.Radius * MinRadiusFractionOfHint)
            : 10;
        int maxRadius = hint is not null
            ? (int)(hint.Radius * MaxRadiusFractionOfHint)
            : (int)(shortDim * 0.3);
        if (maxRadius <= minRadius) return null;

        var circles = Cv2.HoughCircles(
            blurred, HoughModes.Gradient,
            dp: Dp,
            minDist: shortDim / 4.0,
            param1: CannyHighThreshold,
            param2: accumulator,
            minRadius: minRadius,
            maxRadius: maxRadius);
        if (circles.Length == 0) return null;

        foreach (var c in circles)
        {
            if (hint is not null)
            {
                var dx = c.Center.X - hint.Center.X;
                var dy = c.Center.Y - hint.Center.Y;
                var centerDist = Math.Sqrt(dx * dx + dy * dy);
                if (centerDist + c.Radius > hint.Radius) continue;
                if (centerDist > hint.Radius * MaxCenterOffsetFractionOfHint) continue;
            }
            return new HauptspiegelReflexResult(c.Center, c.Radius);
        }
        return null;
    }
}

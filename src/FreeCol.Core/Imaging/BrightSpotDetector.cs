using System;
using OpenCvSharp;

namespace FreeCol.Core.Imaging;

/// <summary>
/// Detektiert das hellste Punkt-Feature in einem Graustufenbild über einen
/// adaptiven Histogramm-Schwellenwert (Top-Perzentil) gefolgt von Schwerpunkts-
/// berechnung (Momente). Robuster als Canny-Konturen bei Bewegungs-
/// unschärfe, weil weder geschlossene Kanten noch eine bestimmte Form nötig sind.
/// </summary>
public static class BrightSpotDetector
{
    /// <summary>
    /// Diagnose-Resultat: ausgewählte Position (oder null), tatsächlich angewandter
    /// Schwellenwert und Anzahl gültiger Kandidaten nach Filterung.
    /// </summary>
    public sealed record Result(Point2f? Position, double Threshold, int CandidateCount);

    /// <summary>
    /// Findet die Schwerpunkts-Position des hellsten zusammenhängenden Blobs,
    /// dessen Fläche im erlaubten Bereich liegt und (falls angegeben) der innerhalb
    /// von <paramref name="insideEllipse"/> liegt. Schwelle wird per Otsu automatisch
    /// gewählt.
    /// </summary>
    public static Result Find(
        Mat grayscale,
        int minArea = 8,
        int maxArea = 6000,
        EllipseFit? insideEllipse = null)
    {
        if (grayscale.Channels() != 1)
        {
            throw new ArgumentException("Eingabe muss einkanalig sein.", nameof(grayscale));
        }

        using var thresh = new Mat();
        var otsuThreshold = Cv2.Threshold(
            grayscale, thresh, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

        Cv2.FindContours(thresh, out var contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        Point2f? best = null;
        double bestArea = 0;
        var candidateCount = 0;
        foreach (var c in contours)
        {
            var area = Cv2.ContourArea(c);
            if (area < minArea || area > maxArea) continue;

            var moments = Cv2.Moments(c);
            if (moments.M00 == 0) continue;
            var cx = (float)(moments.M10 / moments.M00);
            var cy = (float)(moments.M01 / moments.M00);

            if (insideEllipse is not null && !IsInside(cx, cy, insideEllipse))
            {
                continue;
            }

            candidateCount++;
            if (area > bestArea)
            {
                bestArea = area;
                best = new Point2f(cx, cy);
            }
        }
        return new Result(best, otsuThreshold, candidateCount);
    }

    /// <summary>
    /// Findet einen **dunklen** Spot, der **innerhalb** einer hellen Fläche liegt.
    /// Typischer OCAL-Fall: die Primärspiegel-Markierung erscheint als dunkler Punkt
    /// im hellen Reflex des Sekundärspiegels. Pipeline: Otsu trennt hell von dunkel
    /// → innerhalb des hellen Bereichs (zusätzlich auf <paramref name="insideEllipse"/>
    /// beschränkt) wird der dunkelste Pixel gesucht, ein relativer Schwellenwert
    /// extrahiert die Marker-Pixel und der Schwerpunkt der größten Kontur ist die
    /// Markerposition.
    /// </summary>
    public static Result FindDarkInBright(
        Mat grayscale,
        EllipseFit? insideEllipse = null,
        int minArea = 10,
        int maxArea = 2500,
        int discErosionPx = 5)
    {
        if (grayscale.Channels() != 1)
        {
            throw new ArgumentException("Eingabe muss einkanalig sein.", nameof(grayscale));
        }

        // 1) Otsu trennt hell/dunkel.
        using var brightMask = new Mat();
        var otsuThreshold = Cv2.Threshold(
            grayscale, brightMask, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

        // 2) Hinweis-Ellipse (OAZ-Rand) eingrenzen, damit Reflexe außerhalb nicht stören.
        if (insideEllipse is not null
            && insideEllipse.Size.Width > 0
            && insideEllipse.Size.Height > 0)
        {
            using var hintMask = new Mat(grayscale.Size(), MatType.CV_8UC1, Scalar.All(0));
            Cv2.Ellipse(hintMask, insideEllipse.ToRotatedRect(), Scalar.All(255), thickness: -1);
            Cv2.BitwiseAnd(brightMask, hintMask, brightMask);
        }

        // 3) Größte zusammenhängende helle Region = der eigentliche Disc.
        Cv2.FindContours(brightMask, out var brightContours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        if (brightContours.Length == 0)
        {
            return new Result(null, otsuThreshold, 0);
        }

        var discIdx = -1;
        double discArea = 0;
        for (var i = 0; i < brightContours.Length; i++)
        {
            var a = Cv2.ContourArea(brightContours[i]);
            if (a > discArea)
            {
                discArea = a;
                discIdx = i;
            }
        }

        if (discIdx < 0 || discArea < 100)
        {
            return new Result(null, otsuThreshold, 0);
        }

        // 4) Disc-Maske ausfüllen, dann ein wenig erodieren, damit der Marker
        // bei Randberührung nicht mit der dunklen Außenumgebung verschmilzt.
        using var discMask = new Mat(grayscale.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.DrawContours(discMask, brightContours, discIdx, Scalar.All(255), thickness: -1);

        if (discErosionPx > 0)
        {
            using var kernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(discErosionPx * 2 + 1, discErosionPx * 2 + 1));
            Cv2.Erode(discMask, discMask, kernel);
        }

        // 5) Dunkel im erodierten Disc-Bereich = Marker-Kandidaten.
        using var darkMask = new Mat();
        Cv2.Threshold(grayscale, darkMask, otsuThreshold, 255, ThresholdTypes.BinaryInv);
        Cv2.BitwiseAnd(darkMask, discMask, darkMask);

        Cv2.FindContours(darkMask, out var contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        Point2f? best = null;
        double bestArea = 0;
        var candidateCount = 0;
        foreach (var c in contours)
        {
            var area = Cv2.ContourArea(c);
            if (area < minArea || area > maxArea) continue;

            var moments = Cv2.Moments(c);
            if (moments.M00 == 0) continue;
            var cx = (float)(moments.M10 / moments.M00);
            var cy = (float)(moments.M01 / moments.M00);

            candidateCount++;
            if (area > bestArea)
            {
                bestArea = area;
                best = new Point2f(cx, cy);
            }
        }
        return new Result(best, otsuThreshold, candidateCount);
    }

    private static bool IsInside(float x, float y, EllipseFit e)
    {
        var a = e.Size.Width / 2.0;
        var b = e.Size.Height / 2.0;
        if (a <= 0 || b <= 0) return false;

        var dx = x - e.Center.X;
        var dy = y - e.Center.Y;
        var rad = e.AngleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var xLocal = dx * cos + dy * sin;
        var yLocal = -dx * sin + dy * cos;
        return (xLocal * xLocal) / (a * a) + (yLocal * yLocal) / (b * b) <= 1.0;
    }
}

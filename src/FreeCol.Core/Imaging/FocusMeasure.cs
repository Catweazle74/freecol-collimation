using OpenCvSharp;

namespace FreeCol.Core.Imaging;

/// <summary>
/// Schärfemaße für den eigenen ROI-Autofokus. Der Kamera-Autofokus taugt nicht,
/// weil er das gesamte Bild bewertet; wir messen die Schärfe gezielt im kleinen
/// Bereich um die gerade selektierte Markierung.
/// </summary>
public static class FocusMeasure
{
    /// <summary>
    /// Varianz des Laplacian im ROI als Schärfemaß — höher = schärfer. ROI wird
    /// auf die Bildgrenzen beschnitten; zu kleine ROIs liefern 0.
    /// </summary>
    public static double LaplacianVariance(Mat gray, Rect roi)
    {
        var clamped = roi & new Rect(0, 0, gray.Width, gray.Height);
        if (clamped.Width < 3 || clamped.Height < 3)
        {
            return 0;
        }

        using var region = new Mat(gray, clamped);
        using var lap = new Mat();
        Cv2.Laplacian(region, lap, MatType.CV_64F);
        Cv2.MeanStdDev(lap, out _, out var stddev);
        return stddev.Val0 * stddev.Val0;
    }

    /// <summary>
    /// Schärfe im quadratischen Fenster um den Fokus-Punkt der Markierung.
    /// </summary>
    public static double Variance(Mat gray, FocusRoi roi)
    {
        var h = (int)System.Math.Round(roi.HalfSize);
        var cx = (int)System.Math.Round(roi.CenterX);
        var cy = (int)System.Math.Round(roi.CenterY);
        return LaplacianVariance(gray, new Rect(cx - h, cy - h, h * 2, h * 2));
    }
}

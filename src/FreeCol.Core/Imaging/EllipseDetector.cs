using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace FreeCol.Core.Imaging;

/// <summary>
/// Findet Ellipsen-Kandidaten auf einem Graustufen-Bild über die Pipeline
/// Canny → FindContours → FitEllipse. Filter nach Kontur-Fläche und Achsen-
/// verhältnis sortieren Rauschen aus. Ergebnis ist absteigend nach
/// Kontur-Fläche sortiert.
/// </summary>
public sealed class EllipseDetector
{
    /// <summary>Konturen unterhalb dieser Fläche (Pixel²) verwerfen.</summary>
    public int MinContourArea { get; init; } = 100;

    /// <summary>
    /// Minimales Verhältnis kurze/lange Achse (0..1). Sehr längliche Fits
    /// sind meistens Bildrand-Artefakte und werden so ausgesondert.
    /// </summary>
    public double MinAxisRatio { get; init; } = 0.2;

    /// <summary>Canny-Schwellen (low, high). Default ist konservativ.</summary>
    public (double Low, double High) CannyThresholds { get; init; } = (80, 200);

    public IReadOnlyList<EllipseFit> Detect(Mat grayscale)
    {
        ArgumentNullException.ThrowIfNull(grayscale);
        if (grayscale.Channels() != 1)
        {
            throw new ArgumentException(
                "Eingabe muss einkanalig sein. Erst Preprocessor.ToGrayscaleBlurred aufrufen.",
                nameof(grayscale));
        }

        using var edges = new Mat();
        Cv2.Canny(grayscale, edges, CannyThresholds.Low, CannyThresholds.High);

        // RetrievalModes.List liefert alle Konturen (auch verschachtelte) —
        // wichtig, weil eine Newton-Kollimations-Sicht typisch mehrere
        // konzentrische Ringe enthält.
        Cv2.FindContours(edges, out var contours, out _,
            RetrievalModes.List, ContourApproximationModes.ApproxSimple);

        var result = new List<EllipseFit>();
        foreach (var contour in contours)
        {
            // FitEllipse benötigt mindestens 5 Punkte.
            if (contour.Length < 5)
            {
                continue;
            }

            var area = Cv2.ContourArea(contour);
            if (area < MinContourArea)
            {
                continue;
            }

            var ellipse = Cv2.FitEllipse(contour);
            var minAxis = Math.Min(ellipse.Size.Width, ellipse.Size.Height);
            var maxAxis = Math.Max(ellipse.Size.Width, ellipse.Size.Height);
            if (maxAxis <= 0 || minAxis / maxAxis < MinAxisRatio)
            {
                continue;
            }

            result.Add(new EllipseFit(ellipse.Center, ellipse.Size, ellipse.Angle, area));
        }

        result.Sort((a, b) => b.ContourArea.CompareTo(a.ContourArea));
        return result;
    }
}

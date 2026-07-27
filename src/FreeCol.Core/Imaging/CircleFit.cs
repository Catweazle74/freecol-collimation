using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace FreeCol.Core.Imaging;

public sealed record CircleFitResult(Point2f Center, double Radius, double RmsResidual);

/// <summary>
/// Algebraischer Kreisfit nach Kåsa: löst (x² + y²) + Ax + By + C = 0
/// im kleinsten Quadrat über Cv2.Solve(SVD). Liefert Mittelpunkt, Radius und
/// RMS-Residuum (mittlerer Abstand der Punkte zur gefitteten Kreislinie).
/// </summary>
public static class CircleFit
{
    public static CircleFitResult Fit(IReadOnlyList<Point2f> points)
    {
        if (points.Count < 3)
        {
            throw new ArgumentException(
                "Mindestens 3 Punkte werden für einen Kreisfit benötigt.",
                nameof(points));
        }

        var n = points.Count;
        using var matM = new Mat(n, 3, MatType.CV_64F);
        using var matV = new Mat(n, 1, MatType.CV_64F);
        for (var i = 0; i < n; i++)
        {
            double x = points[i].X;
            double y = points[i].Y;
            matM.Set(i, 0, x);
            matM.Set(i, 1, y);
            matM.Set(i, 2, 1.0);
            matV.Set(i, 0, -(x * x + y * y));
        }

        using var result = new Mat();
        if (!Cv2.Solve(matM, matV, result, DecompTypes.SVD))
        {
            throw new InvalidOperationException("Kreisfit: linearer Solve fehlgeschlagen.");
        }

        var a = result.At<double>(0, 0);
        var b = result.At<double>(1, 0);
        var c = result.At<double>(2, 0);

        var cx = -a / 2.0;
        var cy = -b / 2.0;
        var radiusSquared = cx * cx + cy * cy - c;
        if (radiusSquared <= 0)
        {
            throw new InvalidOperationException(
                "Kreisfit: degenerierte Lösung (negativer Radius²). Punkte vermutlich kollinear.");
        }
        var radius = Math.Sqrt(radiusSquared);

        double sumSq = 0;
        foreach (var p in points)
        {
            double dx = p.X - cx;
            double dy = p.Y - cy;
            double d = Math.Sqrt(dx * dx + dy * dy) - radius;
            sumSq += d * d;
        }
        var rms = Math.Sqrt(sumSq / n);

        return new CircleFitResult(new Point2f((float)cx, (float)cy), radius, rms);
    }
}

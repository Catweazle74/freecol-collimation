using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace FreeCol.Core.Imaging;

/// <summary>
/// Versatz Marker → OazRand in Bildkoordinaten (Pixel). Positives X bedeutet, der
/// Marker sitzt rechts vom OazRand-Mittelpunkt, positives Y darunter.
/// </summary>
public sealed record CollimationOffset(double X, double Y)
{
    public double Magnitude => Math.Sqrt(X * X + Y * Y);
}

/// <summary>
/// Ergebnis der semantischen Zuordnung: welche der gefundenen Ellipsen ist der
/// OAZ-Rand, welche der Hauptspiegel-Zentrumsmarker, und wie weit liegt der
/// Marker vom OazRand-Mittelpunkt entfernt.
/// </summary>
public sealed record CollimationAnalysis(
    EllipseFit? OazRand,
    EllipseFit? Marker,
    CollimationOffset? Offset);

/// <summary>
/// Ordnet bereits geclusterte Ellipsen die Rollen "OazRand" und "Marker" zu.
/// Heuristik: die flächengrößte Ellipse ist der OazRand; aus den restlichen
/// kommen nur deutlich kleinere und innerhalb des OazRand liegende als Marker
/// infrage. Bei mehreren Kandidaten gewinnt der, dessen Mittelpunkt dem OazRand-
/// Mittelpunkt am nächsten liegt — das ist der robusteste Hinweis auf den
/// realen Hauptspiegel-Zentrumsmarker.
/// </summary>
public sealed class CollimationAnalyzer
{
    /// <summary>
    /// Maximale erlaubte ContourArea des Marker-Kandidaten relativ zum OazRand
    /// (0..1). Werte oberhalb sortieren konkurrierende große Ellipsen aus.
    /// </summary>
    public double MaxMarkerAreaRatio { get; init; } = 0.25;

    public CollimationAnalysis Analyze(IReadOnlyList<EllipseFit> clustered)
    {
        if (clustered.Count == 0)
        {
            return new CollimationAnalysis(null, null, null);
        }

        var oazRand = clustered[0];

        EllipseFit? marker = null;
        var bestDistance = double.MaxValue;
        for (var i = 1; i < clustered.Count; i++)
        {
            var candidate = clustered[i];
            if (candidate.ContourArea > oazRand.ContourArea * MaxMarkerAreaRatio)
            {
                continue;
            }
            if (!IsInsideEllipse(candidate.Center, oazRand))
            {
                continue;
            }
            var dx = candidate.Center.X - oazRand.Center.X;
            var dy = candidate.Center.Y - oazRand.Center.Y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d < bestDistance)
            {
                bestDistance = d;
                marker = candidate;
            }
        }

        if (marker is null)
        {
            return new CollimationAnalysis(oazRand, null, null);
        }

        var offset = new CollimationOffset(
            marker.Center.X - oazRand.Center.X,
            marker.Center.Y - oazRand.Center.Y);

        return new CollimationAnalysis(oazRand, marker, offset);
    }

    private static bool IsInsideEllipse(Point2f point, EllipseFit ellipse)
    {
        var a = ellipse.Size.Width / 2.0;
        var b = ellipse.Size.Height / 2.0;
        if (a <= 0 || b <= 0)
        {
            return false;
        }

        // Punkt in das Ellipsen-lokale Koordinatensystem drehen.
        var dx = point.X - ellipse.Center.X;
        var dy = point.Y - ellipse.Center.Y;
        var rad = ellipse.AngleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var xLocal = dx * cos + dy * sin;
        var yLocal = -dx * sin + dy * cos;

        return (xLocal * xLocal) / (a * a) + (yLocal * yLocal) / (b * b) <= 1.0;
    }
}

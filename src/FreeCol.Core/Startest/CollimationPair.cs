using System;
using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Startest;

/// <summary>
/// Ergebnis der Paar-Auswertung, siehe <see cref="CollimationPair.Evaluate"/>.
/// Bei nicht auswertbaren Eingaben (<see cref="IsEvaluable"/> == false) tragen
/// die übrigen Felder Nullwerte; <see cref="Reason"/> nennt den Grund.
/// </summary>
public sealed record CollimationPairResult(
    bool IsEvaluable,
    string? Reason,
    Point2f ErrorVector,
    Point2f SystematicVector,
    double ErrorPercent,
    double SystematicPercent,
    Point2f ErrorPixels,
    double RadiusRatio,
    bool UnequalDefocusWarning)
{
    internal static CollimationPairResult NotEvaluable(string reason) =>
        new(false, reason, default, default, 0, 0, default, 0, false);

    internal static CollimationPairResult Evaluable(
        Point2f errorVector, Point2f systematicVector,
        double errorPercent, double systematicPercent,
        Point2f errorPixels, double radiusRatio,
        bool unequalDefocusWarning) =>
        new(true, null, errorVector, systematicVector, errorPercent,
            systematicPercent, errorPixels, radiusRatio, unequalDefocusWarning);
}

/// <summary>
/// Zerlegt ein intra-/extrafokales Donut-Paar in echten Kollimationsfehler und
/// systematischen Anteil (v. a. der absichtliche Fangspiegel-Versatz beim
/// Newton). An 17 echten Aufnahmen verifiziert: der geometrische Versatz
/// zwischen Obstruktion und Scheibchen kehrt beim Fokusdurchgang das Vorzeichen
/// um, während der Scheibchenradius proportional zum Defokus-Betrag wächst.
///
/// Radiusnormierte Versätze u = Offset/OuterRadius je Aufnahme:
/// echter Fehler e = (u_A + u_B)/2, systematischer Anteil s = (u_A - u_B)/2.
/// Für die Summe ist es unerheblich, welche Aufnahme „intra" bzw. „extra"
/// heißt — vertauscht man A und B, dreht nur
/// <see cref="CollimationPairResult.SystematicVector"/> das Vorzeichen,
/// <see cref="CollimationPairResult.ErrorVector"/> bleibt gleich. Die
/// Zerlegung setzt aber voraus, dass beide Aufnahmen auf entgegengesetzten
/// Seiten des Fokus liegen — sonst ist sie sinnlos und wird abgelehnt.
/// </summary>
public static class CollimationPair
{
    /// <summary>Schwelle für die Ungleich-Defokus-Warnung: prozentuale
    /// Abweichung des größeren vom kleineren Scheibchenradius.</summary>
    public const double UnequalDefocusWarnThresholdPercent = 25.0;

    /// <summary>
    /// Wertet ein Aufnahme-Paar aus. <paramref name="firstFocusOffsetSteps"/>
    /// und <paramref name="secondFocusOffsetSteps"/> geben je Aufnahme nur die
    /// SEITE des Fokus über ihr Vorzeichen an (z. B. Fokuser-Schritte relativ
    /// zur Fokus-Mitte); ihr Betrag fließt nicht in die Rechnung ein.
    /// </summary>
    /// <param name="first">Donut-Messung der ersten Aufnahme.</param>
    /// <param name="firstFocusOffsetSteps">Vorzeichen = Fokus-Seite der
    /// ersten Aufnahme.</param>
    /// <param name="second">Donut-Messung der zweiten Aufnahme.</param>
    /// <param name="secondFocusOffsetSteps">Vorzeichen = Fokus-Seite der
    /// zweiten Aufnahme.</param>
    /// <returns>Auswertung, oder ein nicht-auswertbares Ergebnis mit
    /// Begründung, wenn beide Aufnahmen auf derselben Fokus-Seite liegen.</returns>
    public static CollimationPairResult Evaluate(
        DonutResult first, int firstFocusOffsetSteps,
        DonutResult second, int secondFocusOffsetSteps)
    {
        if (!OnOppositeFocusSides(firstFocusOffsetSteps, secondFocusOffsetSteps))
        {
            return CollimationPairResult.NotEvaluable(
                "Beide Aufnahmen liegen auf derselben Seite des Fokus " +
                "(oder eine liegt im Fokus) — die Zerlegung in Fehler und " +
                "Systematik setzt eine intra-/extrafokale Gegenprobe voraus.");
        }

        var normalizedFirst = NormalizeOffset(first);
        var normalizedSecond = NormalizeOffset(second);

        var errorVector = Midpoint(normalizedFirst, normalizedSecond);
        var systematicVector = HalfDifference(normalizedFirst, normalizedSecond);

        var referenceRadius = (first.OuterRadius + second.OuterRadius) / 2.0;
        var errorPixels = Scale(errorVector, referenceRadius);

        var radiusRatio = RadiusRatio(first.OuterRadius, second.OuterRadius);
        var unequalDefocusWarning =
            (radiusRatio - 1.0) * 100.0 > UnequalDefocusWarnThresholdPercent;

        return CollimationPairResult.Evaluable(
            errorVector, systematicVector,
            Magnitude(errorVector) * 100.0, Magnitude(systematicVector) * 100.0,
            errorPixels, radiusRatio, unequalDefocusWarning);
    }

    private static bool OnOppositeFocusSides(int firstSteps, int secondSteps) =>
        Math.Sign(firstSteps) * Math.Sign(secondSteps) < 0;

    private static Point2f NormalizeOffset(DonutResult donut) =>
        new((float)(donut.Offset.X / donut.OuterRadius),
            (float)(donut.Offset.Y / donut.OuterRadius));

    private static Point2f Midpoint(Point2f a, Point2f b) =>
        new((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);

    private static Point2f HalfDifference(Point2f a, Point2f b) =>
        new((a.X - b.X) / 2f, (a.Y - b.Y) / 2f);

    private static Point2f Scale(Point2f vector, double factor) =>
        new((float)(vector.X * factor), (float)(vector.Y * factor));

    private static double Magnitude(Point2f vector) =>
        Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y);

    private static double RadiusRatio(double radiusA, double radiusB) =>
        Math.Max(radiusA, radiusB) / Math.Min(radiusA, radiusB);
}

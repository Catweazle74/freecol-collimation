using System;
using System.Collections.Generic;

namespace FreeCol.Core.Screws;

/// <summary>
/// Berechnet aus dem Versatz-Vektor der beweglichen Markierung Drehmengen pro
/// Schraube, sodass Σ <c>t_i × (E_i.x, E_i.y)</c> = <c>(targetDx, targetDy)</c>.
/// Bei mehr als 2 Schrauben ist das System unter-bestimmt; wir wählen die
/// Minimum-Norm-Lösung (Pseudo-Inverse), die in der Praxis dazu führt, dass die
/// Drehungen möglichst gleichmäßig verteilt werden.
/// </summary>
public static class ScrewSolver
{
    /// <summary>
    /// Liefert pro Schraube die empfohlene Anzahl Umdrehungen (CW positiv, CCW
    /// negativ), um die bewegliche Markierung um <paramref name="targetDx"/>,
    /// <paramref name="targetDy"/> zu verschieben. Wenn das System degeneriert
    /// ist (Wirkungs-Vektoren kollinear) oder keine kalibrierten Schrauben da
    /// sind, sind alle Drehungen 0.
    /// </summary>
    public static double[] ComputeTurns(IReadOnlyList<Screw> screws, double targetDx, double targetDy)
    {
        var n = screws.Count;
        if (n == 0) return Array.Empty<double>();

        // Normal-Gleichungen: M = A·A^T ist eine 2×2-Matrix.
        double m11 = 0, m12 = 0, m22 = 0;
        for (var i = 0; i < n; i++)
        {
            m11 += screws[i].EffectDx * screws[i].EffectDx;
            m12 += screws[i].EffectDx * screws[i].EffectDy;
            m22 += screws[i].EffectDy * screws[i].EffectDy;
        }

        var det = m11 * m22 - m12 * m12;
        if (Math.Abs(det) < 1e-9) return new double[n];

        // M^-1 × b
        var c1 = (m22 * targetDx - m12 * targetDy) / det;
        var c2 = (-m12 * targetDx + m11 * targetDy) / det;

        // t_i = (E_i.x, E_i.y) · (c1, c2)
        var turns = new double[n];
        for (var i = 0; i < n; i++)
        {
            turns[i] = screws[i].EffectDx * c1 + screws[i].EffectDy * c2;
        }
        return turns;
    }
}

using System;
using System.Globalization;

namespace FreeCol.Core.Screws;

/// <summary>
/// Rechenkern der Schrauben-Kalibrierung: rechnet einen gemessenen Versatz auf den
/// Effekt pro CW-Umdrehung um. Die tatsächlich gedrehte Menge kommt vom Nutzer —
/// eine fest angenommene ¼-Umdrehung (früher: Effekt = Δ·4) skaliert alle
/// Drehempfehlungen falsch, sobald real mehr gedreht wurde, weil ¼ die Markierung
/// weniger als die Rausch-Schwelle bewegt.
/// </summary>
public static class ScrewCalibrationMath
{
    /// <summary>Effekt-Vektor pro CW-Umdrehung. <paramref name="turns"/> &gt; 0.</summary>
    public static (double Dx, double Dy) EffectPerTurn(double dx, double dy, double turns, bool clockwise)
    {
        if (turns <= 0)
            throw new ArgumentOutOfRangeException(nameof(turns), turns, "Drehmenge muss positiv sein.");
        var sign = clockwise ? 1.0 : -1.0;
        return (dx / turns * sign, dy / turns * sign);
    }

    /// <summary>
    /// Prüft, ob eine manuell gesetzte Markierung sich seit der Baseline um
    /// mindestens <paramref name="thresholdPx"/> Pixel bewegt hat. Schützt die
    /// Phase-3-Kalibrierung (Linse wird per Klick statt Auto-Erkennung gemessen)
    /// davor, eine vergessene Neu-Platzierung als „bestätigt“ durchgehen zu lassen:
    /// identischer Punkt ⇒ der Nutzer hat nach dem Schrauben-Dreh noch nicht neu
    /// geklickt.
    /// </summary>
    public static bool HasMovedEnough(double dx, double dy, double thresholdPx = 2.0)
        => Math.Sqrt(dx * dx + dy * dy) >= thresholdPx;

    /// <summary>
    /// Parst eine Nutzereingabe in Umdrehungen. Akzeptiert Komma und Punkt sowie
    /// einfache Brüche ("1/4"). Liefert null bei Unsinn oder außerhalb (0, 10].
    /// </summary>
    public static double? ParseTurns(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().Replace(',', '.');

        double value;
        var slash = s.IndexOf('/');
        if (slash > 0)
        {
            if (!double.TryParse(s[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) ||
                !double.TryParse(s[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) ||
                den == 0)
                return null;
            value = num / den;
        }
        else if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return null;
        }

        return value > 0 && value <= 10 ? value : null;
    }
}

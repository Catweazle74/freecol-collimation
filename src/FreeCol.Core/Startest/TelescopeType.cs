namespace FreeCol.Core.Startest;

/// <summary>
/// Teleskop-Bauart für die Sterntest-Bewertung: bestimmt, ob der geometrische
/// Versatz Obstruktion↔Scheibchen in EINEM Bild (<see cref="Imaging.DonutResult.Offset"/>)
/// ein gültiges Kollimationsmaß ist, oder ob dafür erst der Vergleich eines
/// intra-/extrafokalen Paars nötig ist (siehe <see cref="CollimationPair"/>).
/// </summary>
public enum TelescopeType
{
    /// <summary>Newton: der Fangspiegel sitzt konstruktiv versetzt (Offset zum
    /// OAZ hin). Ein Rest-Versatz im Einzelbild ist deshalb normal und KEIN
    /// Kollimationsfehler — erst die Paar-Auswertung trennt echten Fehler und
    /// systematischen Anteil.</summary>
    Newton,

    /// <summary>RC/SC (Ritchey-Chrétien, Schmidt-Cassegrain u. ä.): der
    /// Fangspiegel sitzt konstruktiv zentrisch. Hier ist der Versatz im
    /// Einzelbild weiterhin ein gültiges Kollimationsmaß.</summary>
    RcOrSc,
}

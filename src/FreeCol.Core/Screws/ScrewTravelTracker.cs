namespace FreeCol.Core.Screws;

/// <summary>
/// Verfolgt die kumulative Netto-Drehung je Schraube über die Justage-Iterationen und
/// erkennt ein Herausdrehen (Runout): wird eine Schraube wiederholt in dieselbe
/// Lösen-Richtung (negative Umdrehungen, CCW) bewegt, kann sie den Gewinde-Kontakt
/// verlieren und schließlich aus dem Gewinde laufen. UI-frei und damit isoliert testbar —
/// die Formulierung der Warnmeldung bleibt Sache des Aufrufers.
/// </summary>
public sealed class ScrewTravelTracker
{
    private readonly Dictionary<string, double> _cumulative = new();

    /// <summary>Setzt die gesammelten Drehungen zurück (z. B. bei Phasenwechsel).</summary>
    public void Clear() => _cumulative.Clear();

    /// <summary>Summiert die zuletzt ausgeführten Empfehlungen je Schraube auf.</summary>
    public void Accumulate(IEnumerable<KeyValuePair<string, double>> appliedTurns)
    {
        foreach (var (name, turns) in appliedTurns)
        {
            _cumulative.TryGetValue(name, out var c);
            _cumulative[name] = c + turns;
        }
    }

    /// <summary>Kumulative Netto-Drehung einer Schraube (negativ = netto gelöst/CCW).</summary>
    public double CumulativeFor(string name) => _cumulative.TryGetValue(name, out var c) ? c : 0;

    /// <summary>
    /// Erste Schraube aus <paramref name="orderedNames"/>, die mindestens
    /// <paramref name="warnTurns"/> Umdrehungen herausgedreht (CCW) wurde, samt ihrer
    /// kumulativen Drehung — oder <c>null</c>, wenn keine die Schranke erreicht.
    /// Die Reihenfolge bestimmt der Aufrufer (Phasen-Schraubenfolge).
    /// </summary>
    public (string Name, double Cumulative)? FindRunout(IEnumerable<string> orderedNames, double warnTurns)
    {
        foreach (var name in orderedNames)
        {
            if (_cumulative.TryGetValue(name, out var c) && c <= -warnTurns)
                return (name, c);
        }
        return null;
    }
}

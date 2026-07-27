namespace FreeCol.Core.Focuser;

/// <summary>
/// Reine, UI-freie Positions-Arithmetik für das Fokus-Paar beim Sterntest:
/// aus einer gemerkten Fokus-Mitte und einem Defokus-Betrag (Schritte) ergeben
/// sich Intra-/Extrafokal-Zielposition — jeweils näher am OAZ (intra) bzw.
/// weiter davon entfernt (extra). Damit isoliert testbar, siehe
/// FreeCol.Core.Tests/Focuser.
/// </summary>
public static class FocusPairModel
{
    /// <summary>Intrafokale Zielposition (näher am OAZ) — Fokus-Mitte minus
    /// Defokus-Betrag. Nicht gegen den Fahrbereich geklemmt, siehe
    /// <see cref="IsWithinRange"/> für die Fahrbereichsprüfung.</summary>
    public static int IntraFocusPosition(int center, int defocusSteps) =>
        center - defocusSteps;

    /// <summary>Extrafokale Zielposition (weiter vom OAZ) — Fokus-Mitte plus
    /// Defokus-Betrag.</summary>
    public static int ExtraFocusPosition(int center, int defocusSteps) =>
        center + defocusSteps;

    /// <summary>Liegt <paramref name="position"/> innerhalb des vom Fokuser
    /// gemeldeten Fahrbereichs [0, maxStep]? Ein <paramref name="maxStep"/>
    /// &lt;= 0 bedeutet „Fahrbereich unbekannt" — dann gilt jede nicht-negative
    /// Position als erreichbar.</summary>
    public static bool IsWithinRange(int position, int maxStep) =>
        maxStep > 0 ? position >= 0 && position <= maxStep : position >= 0;
}

using System;
using FreeCol.Core.Markings;

namespace FreeCol.Core.Justage;

/// <summary>
/// Reine, UI-freie Abbildungen der Justage-Phasen — damit isoliert testbar.
/// Phasen: 0 = Orientierung (OAZ), 1 = Fangspiegel zentrieren (Spinne),
/// 2 = Fangspiegel kippen, 3 = Hauptspiegel kippen.
/// Phase 1 ist optional: bei fester CNC-Spinne entfällt sie, dann rückt die
/// Anzeige-Nummerierung der Kipp-Phasen nach vorn.
/// </summary>
public static class JustagePhaseModel
{
    /// <summary>Erste Spiegel-Phase nach der Orientierung — überspringt Phase 1
    /// (Spinnen-Zentrierung) bei fester Spinne. Phase 0 geht ihr immer voraus.</summary>
    public static int FirstMirrorPhase(bool phase1Enabled) => phase1Enabled ? 1 : 2;

    /// <summary>Anzeige-Nummerierung der Phasen (1-basiert). Ist die Spinnen-Phase
    /// ausgeblendet, rücken die Kipp-Phasen in der Nummerierung nach vorn.</summary>
    public static int DisplayNumber(int phase, bool phase1Enabled) => phase switch
    {
        0 => 1,
        1 => 2,
        2 => phase1Enabled ? 3 : 2,
        3 => phase1Enabled ? 4 : 3,
        _ => 0,
    };

    /// <summary>Markierung, die sich in der gegebenen Phase beim Justieren bewegt.
    /// Phase 3: die Linse wandert beim Hauptspiegel-Kippen (nicht der mitwandernde
    /// Marker).</summary>
    public static MarkingKind MovingKind(int phase) => phase switch
    {
        1 => MarkingKind.Sekundaer,
        2 => MarkingKind.HauptspiegelReflex,
        3 => MarkingKind.Linse,
        _ => MarkingKind.Sekundaer,
    };

    /// <summary>Fokus-Tiefenrang einer Markierung: 0 = nah, höher = weiter weg.
    /// OAZ-Rand ist am nächsten, Sekundär dazwischen; Hauptspiegel-Reflex, Marker
    /// und Linse liegen auf der Hauptspiegel-Ebene und teilen sich den fernsten Rang.</summary>
    public static int FocusDepthRank(MarkingKind kind) => kind switch
    {
        MarkingKind.OazRand => 0,
        MarkingKind.Sekundaer => 1,
        _ => 2,
    };

    /// <summary>Mess-Phasen (1-3), deren IST/SOLL-Paar die gegebene Markierung
    /// enthält — identische Zuordnung wie PhaseOffset im ViewModel: Phase 1 =
    /// OazRand+Sekundaer, Phase 2 = HauptspiegelReflex+Sekundaer, Phase 3 =
    /// Linse+Marker. Dient dem Frische-Tracking: wird eine Markierung neu
    /// gesetzt/gemessen, gelten genau diese Phasen als aktuell.</summary>
    public static int[] PhasesUsing(MarkingKind kind) => kind switch
    {
        MarkingKind.OazRand => new[] { 1 },
        MarkingKind.Sekundaer => new[] { 1, 2 },
        MarkingKind.HauptspiegelReflex => new[] { 2 },
        MarkingKind.Marker => new[] { 3 },
        MarkingKind.Linse => new[] { 3 },
        _ => Array.Empty<int>(),
    };
}

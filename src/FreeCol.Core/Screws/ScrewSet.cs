using System.Collections.Generic;
using System.Linq;

namespace FreeCol.Core.Screws;

/// <summary>
/// Schrauben-Konfiguration pro Teleskop. Standard-Layout folgt der gängigen
/// Newton-Terminologie (Skywatcher-/Teleskop-Service-Justieranleitungen): die
/// Spinne trägt 4 Rändelschrauben für die laterale Fangspiegel-Zentrierung,
/// Fang- und Hauptspiegel je 3 Justierschrauben (120° versetzt) für den Tilt.
/// Namen können per Profil-Skizze an das reale Teleskop angepasst werden.
/// </summary>
public sealed record ScrewSet(IReadOnlyList<Screw> Screws)
{
    /// <summary>
    /// Ob die Fangspiegel-Spinne justierbar ist (Phase 1, laterale Zentrierung).
    /// Viele ersetzen die Original-Spinne durch eine CNC-gefräste, fest
    /// zentrierte Variante — dann entfällt Phase 1. Default <c>true</c>; als
    /// Nicht-Konstruktor-Property deklariert, damit ältere JSON-Profile ohne
    /// dieses Feld weiterhin als "justierbar" laden.
    /// </summary>
    public bool SpiderAdjustable { get; init; } = true;

    /// <summary>
    /// Position des Okularauszugs (OAZ) als Winkel im Uhrzeigersinn von oben
    /// (0° = 12 Uhr). Maßgeblich, wer am montierten Teleskop justiert und den
    /// Tubus dabei kippt: dann sitzt der OAZ aus Anwendersicht nicht mehr oben.
    /// Die Phasen-Skizzen werden um diesen Winkel rotiert, damit sie der realen
    /// Blickrichtung entsprechen. Default 0; Nicht-Konstruktor-Init-Property für
    /// Abwärtskompatibilität mit älteren JSON-Profilen.
    /// </summary>
    public double OazAngleDeg { get; init; }

    /// <summary>
    /// Verdrehung der Fangspiegel-Spinne relativ zum OAZ, im Uhrzeigersinn (0° =
    /// Spinne nicht gegen den OAZ verdreht). Der absolute Winkel des Spinnenkreuzes
    /// im Indikator ist <see cref="OazAngleDeg"/> + dieser Versatz. Default 0;
    /// Nicht-Konstruktor-Init-Property für Abwärtskompatibilität.
    /// </summary>
    public double SpiderAngleDeg { get; init; }

    public static ScrewSet Default => new(new[]
    {
        // Phase 1 — laterale Fangspiegel-Zentrierung im Tubus über die
        // 4 Spinnen-Rändelschrauben (außen am Tubus). Die Namen sind nur stabile
        // Slot-Identitäten (Kalibrier-Lookup/Persistenz); angezeigt wird die
        // mitrotierende Uhrzeit-Position ("Spinne 3 Uhr", siehe SpiderDisplayLabels).
        // Reihenfolge = Lehrlage im Uhrzeigersinn (Slot 0 = 0°/oben, +90° je Slot).
        Screw.Untrained("Spinne 1", 1),
        Screw.Untrained("Spinne 2", 1),
        Screw.Untrained("Spinne 3", 1),
        Screw.Untrained("Spinne 4", 1),
        // Phase 2 — Fangspiegel-Tilt: 3 Justierschrauben auf der Fassung.
        Screw.Untrained("Fangspiegel 1", 2),
        Screw.Untrained("Fangspiegel 2", 2),
        Screw.Untrained("Fangspiegel 3", 2),
        // Phase 3 — Hauptspiegel-Tilt: 3 Justierschrauben an der Spiegelzelle
        // (vor dem Drehen die Konterschrauben lösen, danach wieder kontern).
        Screw.Untrained("Hauptspiegel 1", 3),
        Screw.Untrained("Hauptspiegel 2", 3),
        Screw.Untrained("Hauptspiegel 3", 3),
    });

    public IEnumerable<Screw> ForPhase(int phase) => Screws.Where(s => s.Phase == phase);

    public ScrewSet Replace(Screw updated)
    {
        var copy = new List<Screw>(Screws.Count);
        var replaced = false;
        foreach (var s in Screws)
        {
            if (!replaced && s.Name == updated.Name && s.Phase == updated.Phase)
            {
                copy.Add(updated);
                replaced = true;
            }
            else
            {
                copy.Add(s);
            }
        }
        return this with { Screws = copy };
    }
}

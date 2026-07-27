namespace FreeCol.Core.Markings;

/// <summary>
/// Sammlung aller Markierungen für eine Kamera/Optik-Kombination.
/// Jeder Slot ist immer vorhanden (mit <see cref="Marking.IsPlaced"/>=false als
/// Startzustand), damit Auto- und Sichtbarkeits-Settings auch ohne Platzierung
/// persistiert werden.
/// </summary>
public sealed record MarkingSet(
    Marking OazRand,
    Marking HauptspiegelReflex,
    Marking Sekundaer,
    Marking Marker,
    Marking Linse,
    // Aufnahme-Auflösung, aus der die Frame-Pixel-Koordinaten oben stammen.
    // Default 0 = Legacy/unbekannt (ältere JSON-Dateien ohne dieses Feld
    // laden weiterhin klaglos). Wird beim Speichern mit der aktuellen
    // Capture-Größe befüllt (siehe MainWindowViewModel.PersistMarkings) —
    // Grundlage für die Auflösungs-Mismatch-Erkennung (kein automatisches
    // Umskalieren, siehe MainWindowViewModel.MarkingsResolutionMismatch).
    int FrameWidth = 0,
    int FrameHeight = 0)
{
    public static MarkingSet Default => new(
        Marking.Default(MarkingKind.OazRand),
        Marking.Default(MarkingKind.HauptspiegelReflex),
        Marking.Default(MarkingKind.Sekundaer),
        Marking.Default(MarkingKind.Marker),
        Marking.Default(MarkingKind.Linse));

    public Marking this[MarkingKind kind] => kind switch
    {
        MarkingKind.OazRand => OazRand,
        MarkingKind.HauptspiegelReflex => HauptspiegelReflex,
        MarkingKind.Sekundaer => Sekundaer,
        MarkingKind.Marker => Marker,
        MarkingKind.Linse => Linse,
        _ => throw new System.ArgumentOutOfRangeException(nameof(kind)),
    };

    public MarkingSet With(Marking updated) => updated.Kind switch
    {
        MarkingKind.OazRand => this with { OazRand = updated },
        MarkingKind.HauptspiegelReflex => this with { HauptspiegelReflex = updated },
        MarkingKind.Sekundaer => this with { Sekundaer = updated },
        MarkingKind.Marker => this with { Marker = updated },
        MarkingKind.Linse => this with { Linse = updated },
        _ => throw new System.ArgumentOutOfRangeException(nameof(updated)),
    };
}

namespace FreeCol.Camera;

/// <summary>
/// Beschreibt eine erkannte Kamera mit OpenCV-Index, gemeldetem Namen und
/// (falls ermittelbar) USB-Seriennummer.
/// </summary>
public sealed record CameraDevice(int Index, string Name, string? Serial = null)
{
    public string Display => $"[{Index}] {Name}";

    /// <summary>
    /// Schlüssel für die Kalibrier-/Markierungs-/Schrauben-/Einstellungs-Ablage.
    /// Trägt die Kamera eine Seriennummer, wird sie angehängt, damit zwei baugleiche
    /// Kameras (gleicher Name) nicht dieselbe Datei teilen. Ohne Seriennummer bleibt
    /// der Schlüssel unverändert der Name (Bestandsverhalten).
    /// </summary>
    public string StorageKey => string.IsNullOrEmpty(Serial) ? Name : $"{Name} SN{Serial}";

    /// <summary>ToolTip-Text für die Kamera-Auswahl: macht die Seriennummer
    /// (bzw. deren Fehlen) sichtbar, über die Kalibrier-/Markierungsdaten
    /// eindeutig dieser Kamera zugeordnet werden.</summary>
    public string SerialToolTip => string.IsNullOrEmpty(Serial)
        ? "keine Seriennummer — Zuordnung über den Namen"
        : $"Seriennummer: {Serial}";

    public override string ToString() => Display;
}

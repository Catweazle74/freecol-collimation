namespace FreeCol.Core.Settings;

/// <summary>
/// Gemerkte Bedien-Einstellungen pro Kamera. Wird beim Stopp/Schließen geschrieben
/// und beim nächsten Start wieder angewendet.
/// </summary>
public sealed record CameraSettings(
    bool IsAutoExposure,
    double Exposure,
    bool IsAutoFocus,
    double Focus,
    // Gewünschte Aufnahme-Auflösung. 0 = Kamera-Default (640×480). Höhere
    // Auflösung gibt kleinen Strukturen (Marker-Ring) mehr Pixel.
    int CaptureWidth = 0,
    int CaptureHeight = 0);

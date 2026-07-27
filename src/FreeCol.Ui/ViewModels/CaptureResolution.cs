namespace FreeCol.Ui.ViewModels;

/// <summary>
/// Auswählbare Aufnahme-Auflösung. Höhere Auflösung gibt kleinen Strukturen
/// (z.B. dem Marker-Ring) mehr Pixel — entscheidend für die Detektion.
/// </summary>
public sealed record CaptureResolution(string Label, int Width, int Height);

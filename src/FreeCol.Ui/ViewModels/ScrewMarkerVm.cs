using Avalonia.Media;

namespace FreeCol.Ui.ViewModels;

/// <summary>
/// Ein Schrauben-Markierungspunkt im Phasen-Diagramm. Position (Left/Top) ist
/// bereits als linke obere Ecke des Punkts in der Diagramm-Koordinate berechnet.
/// Farbe/Rahmen kodieren den Zustand: aktiv = gerade kalibriert, sonst
/// kalibriert/offen.
/// </summary>
public sealed class ScrewMarkerVm
{
    public double Diameter { get; init; } = 22;
    public double Left { get; init; }
    public double Top { get; init; }
    public string Label { get; init; } = "";
    public bool IsActive { get; init; }
    public bool IsCalibrated { get; init; }

    public IBrush Fill => IsActive
        ? new SolidColorBrush(Color.Parse("#FFD24D"))   // gerade kalibriert: hell
        : IsCalibrated
            ? new SolidColorBrush(Color.Parse("#5FAE7F")) // kalibriert: grün
            : new SolidColorBrush(Color.Parse("#9AA4AE")); // offen: grau

    public IBrush Stroke => IsActive
        ? Brushes.White
        : new SolidColorBrush(Color.Parse("#222222"));

    public double StrokeThickness => IsActive ? 2.5 : 1;
}

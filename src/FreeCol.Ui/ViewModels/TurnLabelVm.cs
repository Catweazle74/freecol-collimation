namespace FreeCol.Ui.ViewModels;

/// <summary>
/// Großer, aus Entfernung lesbarer Drehzahl-Text neben einem Schrauben-Pfeil im
/// Phasen-Diagramm (z. B. „0,6 CW"). Position (Left/Top) ist bereits als linke
/// obere Ecke in Diagramm-Koordinaten berechnet.
/// </summary>
public sealed class TurnLabelVm
{
    public double Left { get; init; }
    public double Top { get; init; }
    public string Text { get; init; } = "";
}

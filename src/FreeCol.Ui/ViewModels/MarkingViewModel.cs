using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FreeCol.Core.Markings;

namespace FreeCol.Ui.ViewModels;

public sealed partial class MarkingViewModel : ObservableObject
{
    public MarkingKind Kind { get; }
    public string Name { get; }
    public IBrush Swatch { get; }

    /// <summary>Laienverständliche Erklärung des Fachbegriffs — als ToolTip in
    /// Sidebar und Overlay-Legende, damit unerfahrene Nutzer die Markierung einordnen.</summary>
    public string Hint { get; }

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isAutoEnabled = true;

    [ObservableProperty]
    private bool _isSelectedForEdit;

    // Display-Space-Geometrie für das Avalonia-Overlay. Wird vom MainVM
    // berechnet und gesetzt, sobald sich Marking, Selection, Calibration,
    // Image-Bounds oder Zoom-Crop ändern.
    [ObservableProperty]
    private double _displayLeft;

    [ObservableProperty]
    private double _displayTop;

    [ObservableProperty]
    private double _displayWidth;

    [ObservableProperty]
    private double _displayHeight;

    [ObservableProperty]
    private double _displayCenterX;

    [ObservableProperty]
    private double _displayCenterY;

    [ObservableProperty]
    private bool _isRenderVisible;

    public MarkingViewModel(MarkingKind kind, string name, IBrush swatch, string hint = "")
    {
        Kind = kind;
        Name = name;
        Swatch = swatch;
        Hint = hint;
    }

    public void ApplyFrom(Marking m)
    {
        IsVisible = m.IsVisible;
        IsAutoEnabled = m.IsAutoEnabled;
    }

    public void SetDisplayFrom(Marking m, DisplayTransform t, bool suppress = false)
    {
        if (suppress || !m.IsPlaced || !m.IsVisible || !t.IsValid)
        {
            IsRenderVisible = false;
            return;
        }

        var (cx, cy) = t.MapToDisplay(m.CenterX, m.CenterY);
        var rxd = t.MapLengthToDisplay(m.RadiusX);
        var ryd = t.MapLengthToDisplay(m.RadiusY);
        DisplayCenterX = cx;
        DisplayCenterY = cy;
        DisplayLeft = cx - rxd;
        DisplayTop = cy - ryd;
        DisplayWidth = rxd * 2;
        DisplayHeight = ryd * 2;
        IsRenderVisible = true;
    }
}

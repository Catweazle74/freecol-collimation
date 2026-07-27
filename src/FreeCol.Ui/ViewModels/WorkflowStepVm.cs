using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FreeCol.Ui.ViewModels;

/// <summary>
/// Ein Schritt der Workflow-Leiste (Kamera → Kalibrieren → Markieren → Justage →
/// Sterntest). Zeigt Erledigt-/Aktiv-Status; klickbare Schritte wechseln den Modus.
/// </summary>
public sealed partial class WorkflowStepVm : ObservableObject
{
    public string Label { get; }
    public string? Hint { get; }
    public IRelayCommand? ActivateCommand { get; }

    /// <summary>Klickbar (hat ein ActivateCommand) — sonst reine Statusanzeige
    /// (z. B. „1 Kamera", „2 Kalibrieren"), die optisch nicht wie ein Knopf wirken soll.</summary>
    public bool IsClickable => ActivateCommand is not null;

    /// <summary>Schritt abgeschlossen (✓ im Chip).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private bool _isDone;

    /// <summary>Schritt ist der aktuell aktive Modus (hervorgehoben).</summary>
    [ObservableProperty]
    private bool _isActive;

    public string Text => IsDone ? $"{Label} ✓" : Label;

    public WorkflowStepVm(string label, string? hint = null, Action? activate = null)
    {
        Label = label;
        Hint = hint;
        if (activate is not null) ActivateCommand = new RelayCommand(activate);
    }
}

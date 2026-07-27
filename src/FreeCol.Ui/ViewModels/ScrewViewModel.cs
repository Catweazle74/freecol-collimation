using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeCol.Core.Screws;

namespace FreeCol.Ui.ViewModels;

public sealed partial class ScrewViewModel : ObservableObject
{
    // Persistierte Identität für Solver/Persistenz/Kalibrier-Lookup.
    public string Name { get; }
    // Anzeigename: bei Phase 1 die aktuelle Sicht-Position ("Spinne 3 Uhr") —
    // rotiert mit OAZ/Spinne mit. Sonst gleich `Name`.
    public string DisplayName { get; }
    public int Phase { get; }
    public double EffectDx { get; }
    public double EffectDy { get; }
    public bool IsCalibrated { get; }
    // Zeitpunkt der letzten Kalibrierung, oder null (unkalibriert bzw. aus
    // älterem Profil ohne dieses Feld geladen).
    public DateTimeOffset? CalibratedAt { get; }

    public string StatusText => IsCalibrated
        ? $"kalibriert{(CalibratedAt is { } at ? $" ({at.LocalDateTime:dd.MM.})" : "")}: "
          + $"Δx={EffectDx:+0.0;-0.0;0.0}  Δy={EffectDy:+0.0;-0.0;0.0} px/Umdr."
        : "nicht kalibriert";

    // Ehrliche Beschriftung statt immer "Kalibrieren": bei bereits kalibrierter
    // Schraube macht "Neu kalibrieren" die Überschreib-Absicht sichtbar.
    public string CalibrateButtonText => IsCalibrated ? "Neu kalibrieren" : "Kalibrieren";

    public string RecommendedTurnsText { get; }
    public bool HasRecommendation { get; }

    public Action<ScrewViewModel>? OnCalibrateRequested { get; init; }

    [RelayCommand]
    private void StartCalibration() => OnCalibrateRequested?.Invoke(this);

    public ScrewViewModel(Screw model, double? recommendedTurns, string? displayName = null)
    {
        Name = model.Name;
        DisplayName = displayName ?? model.Name;
        Phase = model.Phase;
        EffectDx = model.EffectDx;
        EffectDy = model.EffectDy;
        IsCalibrated = model.IsCalibrated;
        CalibratedAt = model.CalibratedAt;

        if (recommendedTurns is double t)
        {
            HasRecommendation = true;
            if (Math.Abs(t) < 0.02)
            {
                RecommendedTurnsText = "✓ ≈ 0 Umdrehungen";
            }
            else
            {
                var sign = t > 0 ? "CW" : "CCW";
                RecommendedTurnsText = $"{Math.Abs(t):F2} Umdr. {sign}";
            }
        }
        else
        {
            HasRecommendation = false;
            RecommendedTurnsText = "—";
        }
    }
}

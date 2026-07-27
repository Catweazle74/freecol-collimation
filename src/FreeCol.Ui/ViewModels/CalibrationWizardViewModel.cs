using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeCol.Core.Calibration;
using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Ui.ViewModels;

public partial class CalibrationWizardViewModel : ViewModelBase
{
    public enum Stage { Idle, AwaitingOrientation, Rotating, Review }

    // Orientierungs-Phase: wann gilt der Marker als "mittig unter der 12-Uhr-Linie"?
    private const double OrientationAlignmentXTolerancePx = 5.0;
    private const double OrientationMinYDistance = 15.0;
    private const double OrientationHoldSeconds = 2.0;

    // Rotations-Phase: weg-basiertes Sampling, lieber dichter als spärlich.
    private const double MinSampleSpacingPx = 6.0;
    private const int MinSamplesForClosure = 20;
    private const double ClosureTolerancePx = 18.0;
    private const int MaxSamples = 200;

    private readonly CalibrationStore _store;
    private readonly Stopwatch _holdTimer = new();
    private readonly List<Point2f> _rotationSamples = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsAwaitingOrientation))]
    [NotifyPropertyChangedFor(nameof(IsRotating))]
    [NotifyPropertyChangedFor(nameof(IsAtReview))]
    [NotifyPropertyChangedFor(nameof(PanelBackground))]
    [NotifyPropertyChangedFor(nameof(PanelBorderBrush))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private Stage _currentStage = Stage.Idle;

    [ObservableProperty]
    private bool _isAligned;

    [ObservableProperty]
    private double _alignmentHoldSeconds;

    [ObservableProperty]
    private string _instructionText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private CalibrationResult? _preview;

    // Überschreibschutz (Zwei-Klick, wie „Phase abgeschlossen"): existiert für
    // den CameraKey bereits eine Kalibrierung, schaltet der erste Klick auf
    // "Kalibrieren" nur scharf statt sofort zu überschreiben. Kein Timer nötig —
    // entschärft wird gezielt bei Cancel und Kamerawechsel (siehe
    // OnCameraKeyChanged). Der Banner-Weg (StartForced) überspringt das bewusst.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    private bool _isOverwriteArmed;

    public string StartButtonText => IsOverwriteArmed ? "Wirklich neu kalibrieren?" : "Kalibrieren";

    public CalibrationWizardViewModel(CalibrationStore store)
    {
        _store = store;
    }

    /// <summary>Welche Kamera wird gerade kalibriert. Muss vor Start gesetzt sein.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string? _cameraKey;

    // Kamerawechsel entschärft den Überschreibschutz — die Warnung galt für die
    // vorherige Kamera und wäre sonst irreführend.
    partial void OnCameraKeyChanged(string? value) => IsOverwriteArmed = false;

    // Aufnahme-Auflösung des zuletzt analysierten Frames — vom VM bei jedem
    // Frame durchgereicht (siehe MainWindowViewModel.DrawOverlay), da der
    // Wizard selbst keinen Zugriff auf das Mat hat. Fließt beim Abschluss der
    // Rotation in den neuen CalibrationResult ein (siehe CompleteRotation) —
    // Grundlage der Auflösungs-Mismatch-Erkennung.
    [ObservableProperty]
    private int _frameWidth;

    [ObservableProperty]
    private int _frameHeight;

    /// <summary>Wird aufgerufen, nachdem ein Save erfolgreich auf Platte lag.</summary>
    public Action<CalibrationResult>? OnSaved { get; set; }

    public bool IsActive => CurrentStage != Stage.Idle;
    public bool IsAwaitingOrientation => CurrentStage == Stage.AwaitingOrientation;
    public bool IsRotating => CurrentStage == Stage.Rotating;
    public bool IsAtReview => CurrentStage == Stage.Review;

    public IReadOnlyList<Point2f> Samples => _rotationSamples;

    public IBrush PanelBackground => CurrentStage switch
    {
        Stage.AwaitingOrientation => new SolidColorBrush(Color.Parse("#1B1B2E")), // dunkles Indigo
        Stage.Rotating            => new SolidColorBrush(Color.Parse("#0E3A1B")), // sattes Dunkelgrün
        Stage.Review              => new SolidColorBrush(Color.Parse("#1B2E3A")), // dunkles Blau
        _                          => Brushes.Transparent,
    };

    public IBrush PanelBorderBrush => CurrentStage switch
    {
        Stage.AwaitingOrientation => new SolidColorBrush(Color.Parse("#FFD700")), // gelb
        Stage.Rotating            => new SolidColorBrush(Color.Parse("#28E060")), // hellgrün
        Stage.Review              => new SolidColorBrush(Color.Parse("#54B0FF")), // hellblau
        _                          => Brushes.Transparent,
    };

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        // Überschreibschutz: existiert bereits eine Kalibrierung für diese
        // Kamera, schaltet der erste Klick nur scharf — erst der zweite Klick
        // (oder StartForced aus dem Entscheidungs-Banner) startet wirklich.
        if (!IsOverwriteArmed && CameraKey is { Length: > 0 } key && _store.Load(key) is { } existing)
        {
            IsOverwriteArmed = true;
            InstructionText = $"Ersetzt die Kalibrierung vom {existing.Timestamp.LocalDateTime:dd.MM.yyyy} "
                              + "— erneut 'Kalibrieren' klicken.";
            return;
        }

        StartInternal();
    }

    /// <summary>
    /// Startet die Kalibrierung ohne den Überschreibschutz aus <see cref="Start"/>.
    /// Für den expliziten „Neu kalibrieren"-Weg aus dem Kalibrier-Entscheidungs-
    /// Banner: dort ist die Absicht bereits explizit bestätigt, ein zweiter Klick
    /// wäre nur redundante Reibung.
    /// </summary>
    public void StartForced()
    {
        IsOverwriteArmed = false;
        StartInternal();
    }

    private void StartInternal()
    {
        IsOverwriteArmed = false;
        _rotationSamples.Clear();
        Preview = null;
        IsAligned = false;
        _holdTimer.Reset();
        AlignmentHoldSeconds = 0;
        CurrentStage = Stage.AwaitingOrientation;
        InstructionText = "Drehe die Kamera, bis der Marker mittig unter der 12-Uhr-Linie liegt.";
    }

    private bool CanStart() => CurrentStage == Stage.Idle && !string.IsNullOrEmpty(CameraKey);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _rotationSamples.Clear();
        Preview = null;
        IsAligned = false;
        _holdTimer.Reset();
        IsOverwriteArmed = false;
        CurrentStage = Stage.Idle;
        InstructionText = "Kalibrierung abgebrochen.";
    }

    private bool CanCancel() => CurrentStage != Stage.Idle;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (Preview is null || string.IsNullOrEmpty(CameraKey)) return;
        try
        {
            _store.Save(CameraKey, Preview);
            InstructionText = $"Kalibrierung gespeichert: {_store.GetPathFor(CameraKey)}";
        }
        catch (Exception ex)
        {
            InstructionText = $"Speichern fehlgeschlagen: {ex.Message}";
            return;
        }
        OnSaved?.Invoke(Preview);
        _rotationSamples.Clear();
        CurrentStage = Stage.Idle;
    }

    private bool CanSave() => CurrentStage == Stage.Review && Preview is not null
                              && !string.IsNullOrEmpty(CameraKey);

    /// <summary>
    /// Wird jeden Frame mit dem aktuellen Detektions-Ergebnis aufgerufen, **auf dem
    /// UI-Thread**. Treibt den Zustandsautomaten an. <paramref name="markerDiag"/>
    /// trägt Threshold und Kandidatenzahl für Diagnose-Anzeigen.
    /// </summary>
    public void Tick(EllipseFit? oazRand, EllipseFit? marker, BrightSpotDetector.Result markerDiag)
    {
        switch (CurrentStage)
        {
            case Stage.AwaitingOrientation:
                ProcessOrientation(oazRand, marker, markerDiag);
                break;
            case Stage.Rotating:
                ProcessRotation(marker);
                break;
        }
    }

    private void ProcessOrientation(EllipseFit? oazRand, EllipseFit? marker, BrightSpotDetector.Result markerDiag)
    {
        if (oazRand is null)
        {
            ResetAlignment();
            InstructionText = $"OAZ-Rand nicht erkannt. Belichtung erhöhen oder die Kamera mittiger auf den Okularauszug richten, dann erneut versuchen. (Technisch: Otsu-Schwelle {markerDiag.Threshold:F0}, {markerDiag.CandidateCount} helle Blobs)";
            return;
        }
        if (marker is null)
        {
            ResetAlignment();
            InstructionText = $"Marker nicht erkannt. Heller stellen oder schärfer fokussieren, dann erneut versuchen. (Technisch: Otsu-Schwelle {markerDiag.Threshold:F0}, {markerDiag.CandidateCount} Blobs innerhalb des OAZ-Rands)";
            return;
        }

        // 12-Uhr-Linie geht vom OAZ-Rand-Zentrum nach oben (kleineres Y). Der Marker
        // soll auf dieser Linie zentral oberhalb des OAZ-Rand-Mittelpunkts liegen.
        var xOffset = marker.Center.X - oazRand.Center.X;
        var yOffset = marker.Center.Y - oazRand.Center.Y;
        var aligned = Math.Abs(xOffset) <= OrientationAlignmentXTolerancePx
                      && yOffset < -OrientationMinYDistance;

        if (!aligned)
        {
            ResetAlignment();
            InstructionText = $"Marker auf 12 Uhr drehen — Offset x={xOffset:+0;-0;0} y={yOffset:+0;-0;0} px (Ziel: |x|≤{OrientationAlignmentXTolerancePx:F0}, y<−{OrientationMinYDistance:F0}).";
            return;
        }

        if (!_holdTimer.IsRunning) _holdTimer.Start();
        AlignmentHoldSeconds = _holdTimer.Elapsed.TotalSeconds;
        IsAligned = true;
        InstructionText = $"Position halten … {AlignmentHoldSeconds:F1} / {OrientationHoldSeconds:F1} s";

        if (_holdTimer.Elapsed.TotalSeconds >= OrientationHoldSeconds)
        {
            TransitionToRotating(marker);
        }
    }

    private void ResetAlignment()
    {
        _holdTimer.Reset();
        AlignmentHoldSeconds = 0;
        IsAligned = false;
    }

    private void TransitionToRotating(EllipseFit initialMarker)
    {
        _rotationSamples.Clear();
        _rotationSamples.Add(initialMarker.Center);
        CurrentStage = Stage.Rotating;
        InstructionText = "Drehe die Kamera langsam und gleichmäßig — Stützpunkte werden automatisch erfasst (1).";
    }

    private void ProcessRotation(EllipseFit? marker)
    {
        if (marker is null || _rotationSamples.Count == 0) return;

        var current = marker.Center;
        var last = _rotationSamples[^1];
        var dx = current.X - last.X;
        var dy = current.Y - last.Y;
        var distFromLast = Math.Sqrt(dx * dx + dy * dy);

        if (distFromLast < MinSampleSpacingPx)
        {
            return;
        }

        _rotationSamples.Add(current);

        // Closure-Check: nach genügend Punkten zurück nahe Sample 0.
        if (_rotationSamples.Count >= MinSamplesForClosure)
        {
            var first = _rotationSamples[0];
            var dx0 = current.X - first.X;
            var dy0 = current.Y - first.Y;
            var distFromStart = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
            if (distFromStart < ClosureTolerancePx)
            {
                CompleteRotation();
                return;
            }
        }

        InstructionText = $"Weiterdrehen … {_rotationSamples.Count} Stützpunkte erfasst.";

        if (_rotationSamples.Count >= MaxSamples)
        {
            CompleteRotation();
        }
    }

    private void CompleteRotation()
    {
        if (_rotationSamples.Count < 3)
        {
            InstructionText = "Nicht genug Stützpunkte. Bitte neu starten.";
            CurrentStage = Stage.Idle;
            return;
        }

        try
        {
            var fit = CircleFit.Fit(_rotationSamples);
            Preview = new CalibrationResult(
                fit.Center, fit.Radius, fit.RmsResidual,
                _rotationSamples.Count,
                OrientationConfirmed: true,
                Timestamp: DateTimeOffset.UtcNow,
                FrameWidth: FrameWidth,
                FrameHeight: FrameHeight);
            CurrentStage = Stage.Review;
            InstructionText = $"Fit: Zentrum=({fit.Center.X:F1}, {fit.Center.Y:F1}) px, "
                              + $"Radius={fit.Radius:F1} px, RMS={fit.RmsResidual:F2} px "
                              + $"({_rotationSamples.Count} Stützpunkte). "
                              + "Passt der grüne Kreis? Dann ‚Speichern' — sonst "
                              + "‚Abbrechen' und neu starten.";
        }
        catch (Exception ex)
        {
            InstructionText = $"Kreisfit fehlgeschlagen: {ex.Message}. Bitte neu starten.";
            CurrentStage = Stage.Idle;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeCol.Camera;
using FreeCol.Core.Calibration;
using FreeCol.Core.Focuser;
using FreeCol.Core.Imaging;
using FreeCol.Core.Justage;
using FreeCol.Core.Markings;
using FreeCol.Core.Screws;
using FreeCol.Core.Settings;
using FreeCol.Core.Startest;
using OpenCvSharp;

namespace FreeCol.Ui.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    // Zielhelligkeit (0..255) für die Software-Auto-Belichtung.
    private const double TargetBrightness = 128.0;
    // Begrenzt pro Anpassungsschritt auf ±MaxStepRatio (Faktor) — verhindert
    // grobe Sprünge und Oszillation, wenn Belichtungs-Antwort verzögert ist.
    private const double MaxStepRatio = 1.5;
    // Alle N Frames eine Anpassung. Bei ~30 FPS → ~6 Hz Regelung.
    private const int AutoAdjustInterval = 5;

    // Produktversion aus den Assembly-Metadaten (in der CI aus dem Git-Tag gesetzt,
    // lokal Default 0.1.0-dev). Der +<commit>-Suffix wird für die Anzeige abgeschnitten.
    public static string AppVersion { get; } = ReadAppVersion();
    public string AppTitle => $"FreeCol {AppVersion}";

    private static string ReadAppVersion()
    {
        var info = typeof(MainWindowViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "?";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }

    public ObservableCollection<CameraDevice> Devices { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private CameraDevice? _selectedDevice;

    // Wählbare Aufnahme-Auflösungen. Höher = mehr Pixel auf kleinen Strukturen
    // (Marker-Ring), bessere Detektion; pro Kamera persistiert.
    public System.Collections.Generic.IReadOnlyList<CaptureResolution> Resolutions { get; } = new[]
    {
        new CaptureResolution("640×480 (Standard)", 640, 480),
        new CaptureResolution("1280×720", 1280, 720),
        new CaptureResolution("1920×1080", 1920, 1080),
        new CaptureResolution("2560×1472 (max)", 2560, 1472),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkingsResolutionMismatch))]
    [NotifyPropertyChangedFor(nameof(MarkingsResolutionMismatchHint))]
    [NotifyPropertyChangedFor(nameof(CalibrationResolutionMismatch))]
    [NotifyPropertyChangedFor(nameof(CalibrationSummary))]
    private CaptureResolution _selectedResolution;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(SnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCalibrationCommand))]
    [NotifyPropertyChangedFor(nameof(ShowCalibrationStatusBar))]
    [NotifyPropertyChangedFor(nameof(ShowNoCameraGate))]
    [NotifyPropertyChangedFor(nameof(ShowImageControlRow))]
    [NotifyPropertyChangedFor(nameof(MarkingsResolutionMismatch))]
    [NotifyPropertyChangedFor(nameof(MarkingsResolutionMismatchHint))]
    [NotifyPropertyChangedFor(nameof(CalibrationResolutionMismatch))]
    [NotifyPropertyChangedFor(nameof(CalibrationSummary))]
    private bool _isRunning;

    // Ohne laufende Kamera gibt es keinen CameraKey → Markierungen/Schrauben werden
    // NICHT gespeichert (stiller No-Op in PersistMarkings/PersistScrews). Der Banner
    // macht das sichtbar, statt den Nutzer ins Leere arbeiten zu lassen. Im Sterntest
    // nicht nötig (arbeitet datei-basiert, eigener StarScrewKey).
    public bool ShowNoCameraGate => !IsRunning && !IsStarTestMode;

    // Kopfbereich Zeile 2 „Bild & Ansicht": nur sichtbar, wenn die Kamera läuft
    // und wir uns nicht im (datei-basierten) Sterntest-Modus befinden — dort hat
    // die Zeile eigene Zoom-/Overlay-Bedienung direkt im Bild-Panel.
    public bool ShowImageControlRow => IsRunning && !IsStarTestMode;

    [ObservableProperty]
    private string _statusText = "Bereit.";

    // Sichtbarer Busy-Zustand für sekundenlange Operationen (Autofokus, Auto-
    // Markierung, Erkennen): zeigt die ProgressBar und sperrt die auslösenden
    // Buttons. Zählerbasiert, damit verschachtelte Abläufe (Scharfstellen +
    // Erkennen ruft den Autofokus) den Zustand nicht vorzeitig beenden.
    [ObservableProperty]
    private bool _isBusy;

    private int _busyOps;

    private void EnterBusy()
    {
        if (Interlocked.Increment(ref _busyOps) == 1) IsBusy = true;
    }

    private void ExitBusy()
    {
        if (Interlocked.Decrement(ref _busyOps) == 0) IsBusy = false;
    }

    [ObservableProperty]
    private Bitmap? _currentFrame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlayLegend))]
    private bool _isOverlayEnabled = true;

    // Legende nur zeigen, wenn sie auch Einträge hätte — sonst stünde der nackte
    // Titel „Legende" allein über dem Bild (z. B. Sterntest ohne erkannten Donut
    // oder Markier-Modus ohne gezeichnete Markierungen).
    public bool ShowOverlayLegend
        => IsOverlayEnabled
           && (IsStarTestMode ? ShowStarTestOverlay : MarkingVms.Any(v => v.IsRenderVisible));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualExposureEnabled))]
    private bool _isExposureControlAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualExposureEnabled))]
    private bool _isAutoExposure = true;

    [ObservableProperty]
    private double _exposureValue = 100;

    [ObservableProperty]
    private double _exposureMin = 1;

    [ObservableProperty]
    private double _exposureMax = 10000;

    public bool ManualExposureEnabled => IsExposureControlAvailable && !IsAutoExposure;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualFocusEnabled))]
    private bool _isFocusControlAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualFocusEnabled))]
    private bool _isAutoFocus = true;

    [ObservableProperty]
    private double _focusValue;

    [ObservableProperty]
    private double _focusMin;

    [ObservableProperty]
    private double _focusMax = 255;

    public bool ManualFocusEnabled => IsFocusControlAvailable && !IsAutoFocus;

    [ObservableProperty]
    private double _zoomPercent = 100;

    public double ZoomMin => 100;
    public double ZoomMax => 300;

    private ICameraSource? _source;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    // Tatsächlich gelieferte Capture-Größe der laufenden Kamera statt der
    // angeforderten SelectedResolution — UVC-Kameras liefern unter DirectShow
    // ggf. eine andere Größe als angefordert (siehe ActualResolutionHint,
    // z.B. OCAL: angefordert 2560×1472, geliefert 2592×1944). Fallback auf
    // SelectedResolution, wenn keine Kamera läuft oder die Quelle keine
    // Ist-Größe meldet (Alpaca/ASI melden ActualWidth/-Height nicht). Diese
    // Größe ist die Grundlage für Stempel (PersistMarkings) und Mismatch-
    // Prüfungen (MarkingsResolutionMismatch, CalibrationResolutionMismatch) —
    // beides muss dieselbe, tatsächlich gelieferte Größe verwenden, sonst
    // laufen Koordinaten und Frame auseinander, ohne dass die Warnung anschlägt.
    private (int Width, int Height) CaptureFrameSize
        => IsRunning && _source is OpenCvVideoCaptureSource { ActualWidth: > 0, ActualHeight: > 0 } uvc
            ? (uvc.ActualWidth, uvc.ActualHeight)
            : (SelectedResolution.Width, SelectedResolution.Height);

    private int CaptureFrameWidth => CaptureFrameSize.Width;
    private int CaptureFrameHeight => CaptureFrameSize.Height;

    private readonly EllipseDetector _detector = new() { MinContourArea = 100, MinAxisRatio = 0.3 };
    private readonly EllipseClusterer _clusterer = new();
    // Während der Kalibrierung halten wir den letzten plausiblen OAZ-Rand fest, damit
    // ein einzelner Bewegungs-Frame mit zerfallener Kontur den Anker nicht verschiebt.
    private EllipseFit? _lastCalibrationOazRand;
    private const double MinCalibrationOazRandArea = 5000.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCalibration))]
    [NotifyPropertyChangedFor(nameof(CalibrationSummary))]
    [NotifyPropertyChangedFor(nameof(HasOffsetReading))]
    [NotifyPropertyChangedFor(nameof(OffsetText))]
    [NotifyPropertyChangedFor(nameof(ArrowEndPoint))]
    [NotifyPropertyChangedFor(nameof(SekundaerToOazRandText))]
    [NotifyPropertyChangedFor(nameof(HasSekundaerToOazRand))]
    [NotifyPropertyChangedFor(nameof(HsrToSekundaerText))]
    [NotifyPropertyChangedFor(nameof(HasHsrToSekundaer))]
    [NotifyPropertyChangedFor(nameof(SekundaerEccentricityText))]
    [NotifyPropertyChangedFor(nameof(HasSekundaerEccentricity))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseStatusText))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseUnderTolerance))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseScrewVms))]
    [NotifyPropertyChangedFor(nameof(CalibrationResolutionMismatch))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCalibrationCommand))]
    private CalibrationResult? _currentCalibration;

    public bool HasCalibration => CurrentCalibration is not null;

    // true, sobald eine Kamera läuft UND die geladene Kalibrierung aus einer
    // anderen Aufnahme-Auflösung stammt als der aktuellen Capture-Größe. Kein
    // automatisches Umskalieren (Seitenverhältnisse/Crops unterscheiden sich
    // je Auflösung) — stattdessen werden optische-Zentrum-basierte Anzeigen
    // unterdrückt (siehe HasOffsetReading, RecomputeOverlayDisplay) und ein
    // Hinweis in CalibrationSummary ergänzt. FrameWidth/-Height = 0 bedeutet
    // eine Legacy-Kalibrierung ohne bekannte Auflösung — dafür kein Mismatch.
    // Vergleich gegen CaptureFrameWidth/-Height (tatsächlich gelieferte
    // Größe), nicht gegen SelectedResolution (angeforderte Größe).
    public bool CalibrationResolutionMismatch
        => IsRunning && CurrentCalibration is { FrameWidth: > 0, FrameHeight: > 0 } c
           && (c.FrameWidth != CaptureFrameWidth || c.FrameHeight != CaptureFrameHeight);

    public string CalibrationSummary => CurrentCalibration is { } c
        ? $"Kalibriert: {c.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm} · RMS {c.RmsResidual:F2} px · {c.SampleCount} Stützpunkte"
          + (CalibrationResolutionMismatch ? $" (andere Auflösung: {c.FrameWidth}×{c.FrameHeight})" : "")
        : "Keine Kalibrierung geladen.";

    public bool ShowCalibrationStatusBar => IsRunning && !CalibrationWizard.IsActive;

    // Entscheidungs-Banner „vorhandene Kalibrierung verwenden vs. neu bestimmen":
    // Start() lädt eine persistierte Kalibrierung weiterhin still (das bleibt der
    // richtige Default) — dieser Banner macht die Entscheidung darüber nur
    // sichtbar, statt sie stillschweigend zu treffen. Verschwindet bei Kamera-
    // Stop und sobald der Wizard anderweitig aktiv wird (siehe CalibrationWizard-
    // PropertyChanged-Handler im Konstruktor).
    [ObservableProperty]
    private bool _showCalibrationDecision;

    // Zusatzzeile im Banner: ob/wann die OCAL-Schrauben zuletzt kalibriert wurden.
    public bool ShowCalibrationDecisionScrewLine => CurrentScrews.Screws.Any(s => s.IsCalibrated);

    public string CalibrationDecisionScrewText
    {
        get
        {
            var calibrated = CurrentScrews.Screws.Where(s => s.IsCalibrated).ToList();
            if (calibrated.Count == 0) return "";
            var timestamps = calibrated
                .Where(s => s.CalibratedAt is not null)
                .Select(s => s.CalibratedAt!.Value)
                .ToList();
            var when = timestamps.Count > 0
                ? timestamps.Max().LocalDateTime.ToString("dd.MM.yyyy")
                : "unbekannt";
            return $"Schrauben-Kalibrierung: {calibrated.Count} Schrauben, zuletzt {when}.";
        }
    }

    // Zusatzzeile im Banner: macht sichtbar, unter welchem Schlüssel die geladenen
    // Daten abgelegt sind (Name, oder Name+Seriennummer bei mehreren baugleichen
    // Kameras) — siehe CameraDevice.StorageKey.
    public string CalibrationDecisionStorageKeyText
        => $"Zugeordnet über: {CalibrationWizard.CameraKey}";

    [RelayCommand]
    private void UseExistingCalibration()
    {
        ShowCalibrationDecision = false;
        StatusText = "Vorhandene Kalibrierung wird verwendet.";
    }

    [RelayCommand]
    private void RecalibrateNow()
    {
        ShowCalibrationDecision = false;
        CalibrationWizard.StartForced();
    }
    private readonly CollimationAnalyzer _analyzer = new();
    private readonly CalibrationStore _calibrationStore = new();
    private readonly CameraSettingsStore _settingsStore = new();
    private readonly MarkingStore _markingStore = new();
    private readonly ScrewStore _screwStore = new();
    // Gebündelte Detektoren der Kalibrier-/Justage-Pipeline (Hint-Kette OAZ-Rand →
    // HSR → Sekundär/Fangspiegel). Eine Abhängigkeit statt sieben Einzelfeldern.
    private readonly DetectorSet _detectors = new();
    // Zentrale Erzeugung der Live-Kameraquellen (UVC/Alpaca/ASI) statt verstreuter new.
    private readonly CameraSourceFactory _cameraSources = new();

    // Sterntest: geladenes (gebinntes/normalisiertes) Graustufen-Frame + letzte
    // Donut-Erkennung. Datei-basiert (FITS), kein Kamera-Loop.
    private Mat? _starGray;
    private DonutResult? _donut;
    // Aktuelles Crop-Fenster im _starGray (für Zoom/Auto-Zoom auf den Donut).
    private OpenCvSharp.Rect _starCropRect;
    private double _starCropOffsetX;
    private double _starCropOffsetY;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOffsetReading))]
    [NotifyPropertyChangedFor(nameof(OffsetText))]
    [NotifyPropertyChangedFor(nameof(ArrowEndPoint))]
    [NotifyPropertyChangedFor(nameof(SekundaerToOazRandText))]
    [NotifyPropertyChangedFor(nameof(HasSekundaerToOazRand))]
    [NotifyPropertyChangedFor(nameof(HsrToSekundaerText))]
    [NotifyPropertyChangedFor(nameof(HasHsrToSekundaer))]
    [NotifyPropertyChangedFor(nameof(SekundaerEccentricityText))]
    [NotifyPropertyChangedFor(nameof(HasSekundaerEccentricity))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseStatusText))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseUnderTolerance))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseScrewVms))]
    [NotifyPropertyChangedFor(nameof(ShowStaleRecommendationHint))]
    [NotifyPropertyChangedFor(nameof(MarkingsResolutionMismatch))]
    [NotifyPropertyChangedFor(nameof(MarkingsResolutionMismatchHint))]
    private MarkingSet _currentMarkings = MarkingSet.Default;

    // true, sobald eine Kamera läuft UND die geladenen Markierungen aus einer
    // anderen Aufnahme-Auflösung stammen als der aktuellen Capture-Größe. Kein
    // automatisches Umskalieren (Seitenverhältnisse/Crops unterscheiden sich je
    // Auflösung — falsche Magie) — stattdessen werden die Ringe NICHT gezeichnet
    // und die Drehempfehlungen unterdrückt (siehe RecomputeOverlayDisplay,
    // PhaseOffset), OHNE die Daten zu löschen. FrameWidth/-Height = 0 bedeutet:
    // noch nie gespeichert (frisches MarkingSet ohne Bestand) — dafür kein
    // Mismatch. Vergleich gegen CaptureFrameWidth/-Height (tatsächlich
    // gelieferte Größe), nicht gegen SelectedResolution (angeforderte Größe).
    public bool MarkingsResolutionMismatch
        => IsRunning && CurrentMarkings is { FrameWidth: > 0, FrameHeight: > 0 }
           && (CurrentMarkings.FrameWidth != CaptureFrameWidth
               || CurrentMarkings.FrameHeight != CaptureFrameHeight);

    public string MarkingsResolutionMismatchHint
        => $"Die gespeicherten Markierungen stammen aus {CurrentMarkings.FrameWidth}×{CurrentMarkings.FrameHeight}, "
         + $"die Kamera liefert {CaptureFrameWidth}×{CaptureFrameHeight}. Entweder die Auflösung "
         + $"zurückstellen (Stop → {CurrentMarkings.FrameWidth}×{CurrentMarkings.FrameHeight} wählen → Start) oder "
         + "mit ‚Automarkierung' neu markieren.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePhaseScrewVms))]
    [NotifyPropertyChangedFor(nameof(IsPhase1Enabled))]
    [NotifyPropertyChangedFor(nameof(OazAngleDeg))]
    [NotifyPropertyChangedFor(nameof(OazClockText))]
    [NotifyPropertyChangedFor(nameof(OazTubePoints))]
    [NotifyPropertyChangedFor(nameof(OazLineEnd))]
    [NotifyPropertyChangedFor(nameof(SpiderAngleDeg))]
    [NotifyPropertyChangedFor(nameof(SpiderOffsetText))]
    [NotifyPropertyChangedFor(nameof(SpiderArm1A))]
    [NotifyPropertyChangedFor(nameof(SpiderArm1B))]
    [NotifyPropertyChangedFor(nameof(SpiderArm2A))]
    [NotifyPropertyChangedFor(nameof(SpiderArm2B))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseScrewMarkers))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseArrowsGeometry))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseTurnLabels))]
    [NotifyPropertyChangedFor(nameof(Phase2Title))]
    [NotifyPropertyChangedFor(nameof(Phase3Title))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseTitle))]
    [NotifyPropertyChangedFor(nameof(PhaseGuideText))]
    [NotifyPropertyChangedFor(nameof(GuideText))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseHasScrews))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseFullyCalibrated))]
    [NotifyPropertyChangedFor(nameof(ShowCalibrationGate))]
    [NotifyPropertyChangedFor(nameof(CalibrationGateText))]
    [NotifyPropertyChangedFor(nameof(ShowCalibrationDecisionScrewLine))]
    [NotifyPropertyChangedFor(nameof(CalibrationDecisionScrewText))]
    [NotifyPropertyChangedFor(nameof(PhaseOrderHint))]
    [NotifyPropertyChangedFor(nameof(HasPhaseOrderHint))]
    private ScrewSet _currentScrews = ScrewSet.Default;

    // OAZ-Position als Winkel im Uhrzeigersinn von oben (0° = 12 Uhr). Bestimmt
    // die Rotation der Phasen-Skizzen, damit sie der realen Blickrichtung am
    // (ggf. gekippten) Teleskop entsprechen. Lebt im ScrewSet, persistiert.
    public double OazAngleDeg
    {
        get => CurrentScrews.OazAngleDeg;
        set
        {
            var clamped = ((value % 360) + 360) % 360;
            if (Math.Abs(CurrentScrews.OazAngleDeg - clamped) < 0.01) return;
            CurrentScrews = CurrentScrews with { OazAngleDeg = clamped };
            PersistScrews();
        }
    }

    public string OazClockText
    {
        get
        {
            var hour = (int)Math.Round(CurrentScrews.OazAngleDeg / 30.0) % 12;
            if (hour == 0) hour = 12;
            return $"OAZ: {CurrentScrews.OazAngleDeg:0}° (≈ {hour} Uhr)";
        }
    }

    // OAZ-Richtungsindikator (120×120-Canvas, Kreismitte 60/60, Tubus-Radius 40).
    // Alles deterministisch per Trigonometrie platziert — keine RotateTransform,
    // damit weder Rotationsachse noch Drehrichtung mehrdeutig sind. Das OAZ-Rohr
    // ist ein Rechteck, das die Tubuswand überlagert: größtenteils außen
    // (OazTubeOuter), ein kurzes Stück innen (OazTubeInner).
    private const double OazCenter = 60;
    private const double OazCircleR = 40;
    private const double OazTubeInner = 8;
    private const double OazTubeOuter = 20;
    private const double OazTubeHalfW = 7;

    private (double ux, double uy, double px, double py) OazAxes()
    {
        var t = CurrentScrews.OazAngleDeg * Math.PI / 180.0;
        var s = Math.Sin(t);
        var c = Math.Cos(t);
        // u = radial nach außen (Uhrzeigersinn von oben), p = senkrecht dazu.
        return (s, -c, c, s);
    }

    public System.Collections.Generic.IList<Avalonia.Point> OazTubePoints
    {
        get
        {
            var (ux, uy, px, py) = OazAxes();
            var ri = OazCircleR - OazTubeInner;
            var ro = OazCircleR + OazTubeOuter;
            Avalonia.Point Corner(double r, double sign) => new(
                OazCenter + r * ux + sign * OazTubeHalfW * px,
                OazCenter + r * uy + sign * OazTubeHalfW * py);
            return new System.Collections.Generic.List<Avalonia.Point>
            {
                Corner(ri, +1), Corner(ro, +1), Corner(ro, -1), Corner(ri, -1),
            };
        }
    }

    public Avalonia.Point OazLineEnd
    {
        get
        {
            var (ux, uy, _, _) = OazAxes();
            var ri = OazCircleR - OazTubeInner;
            return new(OazCenter + ri * ux, OazCenter + ri * uy);
        }
    }

    // Spinnen-Versatz relativ zum OAZ (Fangspiegel-Zentrierung). 0° = Spinne
    // nicht gegen den OAZ verdreht. Lebt im ScrewSet, persistiert pro Kamera.
    public double SpiderAngleDeg
    {
        get => CurrentScrews.SpiderAngleDeg;
        set
        {
            // 4-Speichen-Kreuz ist 90°-symmetrisch → 0..90 deckt alle Lagen ab.
            var clamped = Math.Clamp(value, 0, 90);
            if (Math.Abs(CurrentScrews.SpiderAngleDeg - clamped) < 0.01) return;
            CurrentScrews = CurrentScrews with { SpiderAngleDeg = clamped };
            PersistScrews();
        }
    }

    public string SpiderOffsetText => $"Versatz zur OAZ: {CurrentScrews.SpiderAngleDeg:0}°";

    // Spinnenkreuz im Indikator: zwei Durchmesser (4 Speichen) auf dem Tubus-Kreis.
    // Absolutwinkel = OAZ-Position + Spinnen-Versatz (der Versatz ist relativ zum
    // OAZ). Endpunkte deterministisch per Trigonometrie, wie das OAZ-Rohr.
    private double SpiderAbsoluteDeg => CurrentScrews.OazAngleDeg + CurrentScrews.SpiderAngleDeg;

    private Avalonia.Point CrossPoint(double deg, double sign)
    {
        var t = deg * Math.PI / 180.0;
        return new(
            OazCenter + sign * OazCircleR * Math.Sin(t),
            OazCenter - sign * OazCircleR * Math.Cos(t));
    }

    public Avalonia.Point SpiderArm1A => CrossPoint(SpiderAbsoluteDeg, +1);
    public Avalonia.Point SpiderArm1B => CrossPoint(SpiderAbsoluteDeg, -1);
    public Avalonia.Point SpiderArm2A => CrossPoint(SpiderAbsoluteDeg + 90, +1);
    public Avalonia.Point SpiderArm2B => CrossPoint(SpiderAbsoluteDeg + 90, -1);

    // --- Schrauben-Marker im Phasen-Diagramm -------------------------------
    // Alle Schrauben der aktiven Phase werden als Punkte gezeigt; die gerade
    // kalibrierte ist hervorgehoben. Positionen deterministisch aus der
    // OAZ-/Spinnen-Lage (Konvention: Kipp-Schraube 1 liegt 180° zum OAZ, 2/3
    // folgen im Uhrzeigersinn; Spinnenschrauben an den 4 Kreuz-Enden).
    // Kipp-Phasen rendern auf einem 200er-Overlay über der PNG, die Spinne auf
    // dem 120er-Indikator — Schraubenradien an die jeweilige Skizze angepasst.
    private const double TiltOverlaySize = 200;
    private const double SecondaryScrewR200 = 58.0 * TiltOverlaySize / 360.0;
    private const double PrimaryScrewR200 = 124.0 * TiltOverlaySize / 360.0;

    private static string MarkerLabel(Screw s)
    {
        var last = s.Name.Split(' ')[^1];
        return last.Length <= 2 ? last : last[..1].ToUpperInvariant();
    }

    // Phase-1-Schrauben sind im ScrewSet im Uhrzeigersinn angelegt (Slot 0 = der
    // Spinnen-Speiche bei SpiderAbsoluteDeg + 0°, dann +90°, +180°, +270°). Die
    // persistierte Identität ("Spinne 1..4", Slot-Reihenfolge) bleibt fix für
    // Kalibrier-Lookup; angezeigt wird dagegen die aktuelle Sicht-Position als
    // Uhrzeit ("Spinne 3 Uhr"), damit Marc die Schraube am realen Teleskop
    // wiedererkennt, auch wenn OAZ/Spinne gegen die Lehrlage gedreht sind.
    private (string display, string label) SpiderDisplayLabels(int slotIndex)
    {
        var deg = ((SpiderAbsoluteDeg + slotIndex * 90) % 360 + 360) % 360;
        var hour = (int)Math.Round(deg / 30.0) % 12;
        if (hour == 0) hour = 12;
        return ($"Spinne {hour} Uhr", hour.ToString());
    }

    private readonly record struct ScrewLayout(Screw Screw, double X, double Y, double Dot, int SlotIndex);

    // Deterministische Lage aller Schrauben der aktiven Phase im Diagramm-Raum
    // (Spinne: 120er-Indikator; Kipp-Phasen: 200er-Overlay über der PNG).
    private System.Collections.Generic.List<ScrewLayout> PhaseScrewLayout()
    {
        var phase = ActiveJustagePhase;
        var screws = CurrentScrews.ForPhase(phase).ToList();
        var result = new System.Collections.Generic.List<ScrewLayout>(screws.Count);
        if (screws.Count == 0) return result;

        double cx, cy, radius, baseAngle, step, dot;
        if (phase == 1) // Spinne — Marker an den Kreuz-Enden (120er-Indikator)
        {
            cx = cy = 60; radius = 40; dot = 18;
            baseAngle = SpiderAbsoluteDeg; step = 90;
        }
        else // Kipp-Phasen — Overlay über der rotierten PNG (200er)
        {
            cx = cy = 100; dot = 22;
            radius = phase == 2 ? SecondaryScrewR200 : PrimaryScrewR200;
            baseAngle = CurrentScrews.OazAngleDeg + 180; step = 120;
        }
        for (var i = 0; i < screws.Count; i++)
        {
            var a = (baseAngle + i * step) * Math.PI / 180.0;
            result.Add(new ScrewLayout(screws[i], cx + radius * Math.Sin(a), cy - radius * Math.Cos(a), dot, i));
        }
        return result;
    }

    // Anzeigename einer Schraube: Phase 1 = mitrotierende Uhrzeit, sonst Name.
    private string ScrewDisplayName(Screw s, int slotIndex)
        => s.Phase == 1 ? SpiderDisplayLabels(slotIndex).display : s.Name;

    // Schrauben der aktiven Phase, die noch nicht kalibriert sind (Anzeigenamen).
    private System.Collections.Generic.List<string> UncalibratedActivePhaseScrews()
    {
        var list = new System.Collections.Generic.List<string>();
        var slot = 0;
        foreach (var s in CurrentScrews.ForPhase(ActiveJustagePhase))
        {
            if (!s.IsCalibrated) list.Add(ScrewDisplayName(s, slot));
            slot++;
        }
        return list;
    }

    // Gate: Drehempfehlungen erst, wenn ALLE Schrauben der Phase kalibriert sind.
    // Verhindert falsche Mengen aus Teil- oder veralteten Kalibrierdaten.
    public bool ActivePhaseHasScrews => CurrentScrews.ForPhase(ActiveJustagePhase).Any();
    public bool ActivePhaseFullyCalibrated
        => ActivePhaseHasScrews && CurrentScrews.ForPhase(ActiveJustagePhase).All(s => s.IsCalibrated);
    public bool ShowCalibrationGate
        => IsJustageMode && ActivePhaseHasScrews && !ActivePhaseFullyCalibrated && IsScrewCalibrationIdle;

    public string CalibrationGateText
    {
        get
        {
            var open = UncalibratedActivePhaseScrews();
            if (open.Count == 0) return "";
            return "Erst kalibrieren – ohne Kalibrierung keine Drehempfehlung. "
                 + $"Offen: {string.Join(", ", open)}.";
        }
    }

    // Empfohlene Umdrehungen je Schraube der aktiven Phase (Vorzeichen: + = CW).
    // Nur wenn alle Schrauben der Phase kalibriert sind (siehe Gate).
    private System.Collections.Generic.Dictionary<string, double> RecommendedTurns()
    {
        var result = new System.Collections.Generic.Dictionary<string, double>();
        if (!ActivePhaseFullyCalibrated || ActivePhaseOffset is not { } off) return result;
        var calibrated = CurrentScrews.ForPhase(ActiveJustagePhase).Where(s => s.IsCalibrated).ToList();
        if (calibrated.Count == 0) return result;
        var turns = ScrewSolver.ComputeTurns(calibrated, -off.dx, -off.dy);
        for (var i = 0; i < calibrated.Count; i++) result[calibrated[i].Name] = turns[i];

        // Kipp-Phasen (3 feste Schrauben): die Drehsumme muss 0 sein. Alle
        // Schrauben sind angezogen — man kann nicht alle anziehen, sondern muss
        // mindestens eine lösen, damit die anderen Spiel bekommen. Ein gemeinsamer
        // Offset auf alle drei ist reiner Piston (Axialverschiebung, kein Tilt) und
        // optisch belanglos → Mittelwert abziehen, dann gilt Σ = 0 und es gibt
        // garantiert mindestens eine Lösung (CCW) und mindestens ein Anziehen (CW).
        if ((ActiveJustagePhase == 2 || ActiveJustagePhase == 3) && result.Count > 0)
        {
            var mean = result.Values.Average();
            foreach (var key in result.Keys.ToList()) result[key] -= mean;
        }
        return result;
    }

    // Vorschau-Pfeil während der Schrauben-Kalibrierung: zeigt für die gerade
    // kalibrierte Schraube die gewählte Drehrichtung (¼ Umdrehung CW/CCW) im
    // Diagramm an, damit Richtung und Marker zusammenpassen. Kehrt sich um, wenn
    // die Drehrichtung gewechselt wird.
    public Avalonia.Media.Geometry? CalibrationArrowGeometry
    {
        get
        {
            if (!IsScrewCalibrationActive || _calibratingScrewName is null) return null;
            if (_calibratingScrewPhase != ActiveJustagePhase) return null;
            foreach (var l in PhaseScrewLayout())
            {
                if (l.Screw.Name != _calibratingScrewName) continue;
                var sb = new System.Text.StringBuilder();
                AppendTurnArrow(sb, l.X, l.Y, l.Dot / 2 + 7, IsScrewCwSelected ? 0.25 : -0.25);
                return sb.Length == 0 ? null : Avalonia.Media.Geometry.Parse(sb.ToString());
            }
            return null;
        }
    }

    // Große, aus Entfernung lesbare Drehzahl-Texte neben den Pfeilen — beim
    // Justieren steht man am Teleskop und kann die Sidebar-Zahl nicht ablesen.
    // Nicht während der Kalibrierung (da führt der Kalibrier-Pfeil).
    public System.Collections.Generic.IReadOnlyList<TurnLabelVm> ActivePhaseTurnLabels
    {
        get
        {
            var list = new System.Collections.Generic.List<TurnLabelVm>();
            if (IsScrewCalibrationActive) return list;
            var turns = RecommendedTurns();
            if (turns.Count == 0) return list;
            double canvas = ActiveJustagePhase == 1 ? 120 : 200;
            double cx = canvas / 2;
            double cy = cx;
            foreach (var l in PhaseScrewLayout())
            {
                if (!turns.TryGetValue(l.Screw.Name, out var t)) continue;
                // Nur die Zahl — die Drehrichtung zeigt der Pfeil.
                var text = Math.Abs(t) < 0.02 ? "✓" : $"{Math.Abs(t):0.0}";
                // Weit nach außen versetzt (weg vom Diagrammzentrum), damit der Text
                // klar außerhalb des Drehpfeils sitzt und ihn nicht verdeckt.
                var dx = l.X - cx;
                var dy = l.Y - cy;
                var len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1) len = 1;
                // Hauptspiegel-Marker (Phase 3) liegen am Rand → Text nach innen;
                // sonst nach außen.
                var sign = ActiveJustagePhase == 3 ? -1.0 : 1.0;
                var ox = l.X + sign * dx / len * (l.Dot / 2 + 32);
                var oy = l.Y + sign * dy / len * (l.Dot / 2 + 32);
                // Sicherheitshalber im Canvas halten.
                var left = Math.Clamp(ox - 10, 2, canvas - 24);
                var top = Math.Clamp(oy - 10, 2, canvas - 20);
                list.Add(new TurnLabelVm { Left = left, Top = top, Text = text });
            }
            return list;
        }
    }

    public System.Collections.Generic.IReadOnlyList<ScrewMarkerVm> ActivePhaseScrewMarkers
    {
        get
        {
            var phase = ActiveJustagePhase;
            var list = new System.Collections.Generic.List<ScrewMarkerVm>();
            foreach (var l in PhaseScrewLayout())
            {
                var label = phase == 1 ? SpiderDisplayLabels(l.SlotIndex).label : MarkerLabel(l.Screw);
                list.Add(new ScrewMarkerVm
                {
                    Diameter = l.Dot,
                    Left = l.X - l.Dot / 2,
                    Top = l.Y - l.Dot / 2,
                    Label = label,
                    IsActive = IsScrewCalibrationActive
                        && l.Screw.Name == _calibratingScrewName
                        && phase == _calibratingScrewPhase,
                    IsCalibrated = l.Screw.IsCalibrated,
                });
            }
            return list;
        }
    }

    // Gebogener Drehpfeil je Schraube: Richtung = Drehsinn, Bogenlänge ∝ nötige
    // Umdrehung (Bogenmaß), gedeckelt bei 320°. Alle Pfeile als eine Geometrie.
    public Avalonia.Media.Geometry? ActivePhaseArrowsGeometry
    {
        get
        {
            // Während der Kalibrierung nur den Kalibrier-Pfeil zeigen, nicht die
            // berechneten Drehempfehlungen.
            if (IsScrewCalibrationActive) return null;
            var turns = RecommendedTurns();
            if (turns.Count == 0) return null;
            var sb = new System.Text.StringBuilder();
            foreach (var l in PhaseScrewLayout())
            {
                if (turns.TryGetValue(l.Screw.Name, out var t) && Math.Abs(t) >= 0.02)
                    AppendTurnArrow(sb, l.X, l.Y, l.Dot / 2 + 7, t);
            }
            return sb.Length == 0 ? null : Avalonia.Media.Geometry.Parse(sb.ToString());
        }
    }

    private static void AppendTurnArrow(System.Text.StringBuilder sb, double x, double y, double r, double turns)
    {
        var sweepDeg = Math.Min(Math.Abs(turns) * 360.0, 320.0);
        var cw = turns > 0;
        var sweepFlag = cw ? 1 : 0;          // SVG-Arc: 1 = im Uhrzeigersinn (y nach unten)
        var largeArc = sweepDeg > 180 ? 1 : 0;
        var th1 = (cw ? sweepDeg : -sweepDeg) * Math.PI / 180.0;
        double p0x = x, p0y = y - r;          // Start oben am Marker (θ=0)
        double p1x = x + r * Math.Sin(th1), p1y = y - r * Math.Cos(th1);
        sb.Append("M ").Append(F(p0x)).Append(',').Append(F(p0y))
          .Append(" A ").Append(F(r)).Append(',').Append(F(r)).Append(" 0 ")
          .Append(largeArc).Append(' ').Append(sweepFlag).Append(' ')
          .Append(F(p1x)).Append(',').Append(F(p1y)).Append(' ');
        // Pfeilspitze tangential am Endpunkt
        double tx = Math.Cos(th1), ty = Math.Sin(th1);
        if (!cw) { tx = -tx; ty = -ty; }
        double bx = -tx, by = -ty;
        const double len = 6, ah = 25 * Math.PI / 180.0;
        double c = Math.Cos(ah), s = Math.Sin(ah);
        double b1x = bx * c - by * s, b1y = bx * s + by * c;
        double b2x = bx * c + by * s, b2y = -bx * s + by * c;
        sb.Append("M ").Append(F(p1x + len * b1x)).Append(',').Append(F(p1y + len * b1y))
          .Append(" L ").Append(F(p1x)).Append(',').Append(F(p1y))
          .Append(" L ").Append(F(p1x + len * b2x)).Append(',').Append(F(p1y + len * b2y)).Append(' ');
    }

    private static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    // Phase 1 (Fangspiegel-Zentrierung über die Spinne) ist optional: bei einer
    // fest zentrierten CNC-Spinne gibt es nichts zu justieren. Flag lebt im
    // ScrewSet und wird pro Kamera persistiert.
    public bool IsPhase1Enabled
    {
        get => CurrentScrews.SpiderAdjustable;
        set
        {
            if (CurrentScrews.SpiderAdjustable == value) return;
            CurrentScrews = CurrentScrews with { SpiderAdjustable = value };
            PersistScrews();
            // Deaktiviert, während Phase 1 aktiv ist → direkt auf Phase 2.
            if (!value && ActiveJustagePhase == 1) ActiveJustagePhase = 2;
        }
    }

    // Erste Spiegel-Phase nach der Orientierung (überspringt Phase 1 bei fester
    // Spinne). Phase 0 (Orientierung) geht dem immer voraus.
    private int FirstMirrorPhase => JustagePhaseModel.FirstMirrorPhase(IsPhase1Enabled);

    public System.Collections.Generic.IReadOnlyList<ScrewViewModel> ActivePhaseScrewVms
    {
        get
        {
            var turns = RecommendedTurns();
            var phase = ActiveJustagePhase;
            var list = new System.Collections.Generic.List<ScrewViewModel>();
            var slot = 0;
            foreach (var s in CurrentScrews.ForPhase(phase))
            {
                double? recommended = turns.TryGetValue(s.Name, out var t) ? t : null;
                var display = phase == 1 ? SpiderDisplayLabels(slot).display : s.Name;
                list.Add(new ScrewViewModel(s, recommended, display) { OnCalibrateRequested = StartScrewCalibration });
                slot++;
            }
            return list;
        }
    }

    private const double ArrowCanvasSize = 120;
    private const double ArrowCenter = ArrowCanvasSize / 2;
    private const double ArrowMaxDisplacement = 50;

    // Justage-Modus: ersetzt im Sidebar das Markierungs-Panel durch das
    // Justage-Panel. ActiveJustagePhase 1..3 = Sekundär-Zentrieren /
    // Sekundär-Tilt / Hauptspiegel-Tilt.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMarkingsMode))]
    [NotifyPropertyChangedFor(nameof(JustageToggleText))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseTitle))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseStatusText))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseUnderTolerance))]
    [NotifyPropertyChangedFor(nameof(ShowCalibrationGate))]
    [NotifyPropertyChangedFor(nameof(CalibrationGateText))]
    [NotifyPropertyChangedFor(nameof(ShowJustageCompleteBanner))]
    [NotifyPropertyChangedFor(nameof(ShowStaleRecommendationHint))]
    [NotifyPropertyChangedFor(nameof(GuideText))]
    [NotifyPropertyChangedFor(nameof(HasGuideText))]
    private bool _isJustageMode;

    // Sterntest-Modus: dritter Modus (Datei-basiert). Ersetzt im Sidebar das
    // Markierungs-Panel durch das Sterntest-Panel; analysiert ein geladenes
    // defokussiertes Stern-Bild (FITS) auf Kollimation.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMarkingsMode))]
    [NotifyPropertyChangedFor(nameof(StarTestToggleText))]
    [NotifyPropertyChangedFor(nameof(ShowJustageCompleteBanner))]
    [NotifyPropertyChangedFor(nameof(GuideText))]
    [NotifyPropertyChangedFor(nameof(HasGuideText))]
    [NotifyPropertyChangedFor(nameof(ShowImageControlRow))]
    [NotifyPropertyChangedFor(nameof(ShowOverlayLegend))]
    private bool _isStarTestMode;

    // Justage-Abschluss-Handoff: nach Abschluss von Phase 3 zeigt die Sidebar
    // einen grünen Banner mit Arretier-Hinweis + Weiter-Button zum Sterntest.
    // Wird beim erneuten Betreten des Justage-Modus zurückgesetzt — der Nutzer
    // justiert dann bewusst neu.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJustageCompleteBanner))]
    private bool _isJustageComplete;

    public bool ShowJustageCompleteBanner => IsJustageComplete && !IsJustageMode && !IsStarTestMode;

    [RelayCommand]
    private void GoToStarTest() => ActivateStarTestMode();

    // Sterntest-Gesamtabschluss-Latch für die Workflow-Leiste: wird NUR anhand
    // echter Messungen gesetzt (siehe ApplyStarGray) — kein Donut/Bild oder
    // Modus-Verlassen setzt ihn zurück, sonst würde ein einmal erreichtes Ziel
    // beim nächsten Blick auf ein schlechteres Bild wieder verschwinden.
    [ObservableProperty]
    private bool _starCollimationAchieved;

    partial void OnStarCollimationAchievedChanged(bool value) => RefreshWorkflowSteps();

    // Regressions-Meldung: das einmal erreichte Ziel (StarCollimationAchieved-
    // Latch) wurde durch eine neue Messung wieder verlassen. Laufzeitflag —
    // setzt NICHT den Latch selbst zurück (der bleibt bewusst „einmal erreicht"
    // dokumentiert), informiert aber sichtbar über die Regression. Wird beim
    // erneuten Erreichen des Ziels oder beim Verlassen des Sterntest-Modus
    // wieder gelöscht.
    [ObservableProperty]
    private bool _starCollimationLost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPhase0Selected))]
    [NotifyPropertyChangedFor(nameof(IsPhase1Selected))]
    [NotifyPropertyChangedFor(nameof(IsPhase2Selected))]
    [NotifyPropertyChangedFor(nameof(IsPhase3Selected))]
    [NotifyPropertyChangedFor(nameof(IsOrientationPhase))]
    [NotifyPropertyChangedFor(nameof(IsSpiderCenteringPhase))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseScrewMarkers))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseArrowsGeometry))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseTitle))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseStatusText))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseUnderTolerance))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseScrewVms))]
    [NotifyPropertyChangedFor(nameof(PhaseGuideText))]
    [NotifyPropertyChangedFor(nameof(GuideText))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseHasScrews))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseFullyCalibrated))]
    [NotifyPropertyChangedFor(nameof(ShowCalibrationGate))]
    [NotifyPropertyChangedFor(nameof(CalibrationGateText))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseTurnLabels))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseArrowsGeometry))]
    [NotifyPropertyChangedFor(nameof(PhaseOrderHint))]
    [NotifyPropertyChangedFor(nameof(HasPhaseOrderHint))]
    [NotifyPropertyChangedFor(nameof(ShowStaleRecommendationHint))]
    private int _activeJustagePhase;

    public bool IsMarkingsMode => !IsJustageMode && !IsStarTestMode;
    public string JustageToggleText => IsJustageMode ? "Justage beenden" : "Justage starten";
    public string StarTestToggleText => IsStarTestMode ? "Sterntest beenden" : "Sterntest starten";

    // Phase 0 = grundlegende Orientierung (OAZ-Position), gilt für alle Skizzen.
    public bool IsOrientationPhase => ActiveJustagePhase == 0;
    // Phase 1 = Fangspiegel zentrieren: eigenes Spinnen-Rotations-Werkzeug.
    public bool IsSpiderCenteringPhase => ActiveJustagePhase == 1;

    public bool IsPhase0Selected
    {
        get => ActiveJustagePhase == 0;
        set { if (value) ActiveJustagePhase = 0; }
    }
    public bool IsPhase1Selected
    {
        get => ActiveJustagePhase == 1;
        set { if (value) ActiveJustagePhase = 1; }
    }
    public bool IsPhase2Selected
    {
        get => ActiveJustagePhase == 2;
        set { if (value) ActiveJustagePhase = 2; }
    }
    public bool IsPhase3Selected
    {
        get => ActiveJustagePhase == 3;
        set { if (value) ActiveJustagePhase = 3; }
    }

    // Per-Phase Toleranz in Frame-Pixeln. Phase 1 grob (Sekundär-Verschiebung
    // mechanisch grob), Phase 3 fein (optisch).
    private static double TolerancePhase1Px => 10;
    private static double TolerancePhase2Px => 5;
    private static double TolerancePhase3Px => 2;

    // Anzeige-Nummerierung der Phasen. Ist die Spinnen-Phase ausgeblendet
    // (feste Spinne), rückt die Nummerierung der Kipp-Phasen nach vorn.
    private int PhaseDisplayNumber(int phase) => JustagePhaseModel.DisplayNumber(phase, IsPhase1Enabled);

    // Häkchen zeigt „Versatz unter Toleranz" — der Nutzer sieht an der Phasen-
    // Auswahl, welche Kipp-/Zentrier-Phasen bereits im Ziel sind.
    public string Phase0Title => "1. Orientierung (OAZ)";
    public string Phase1Title => $"2. Fangspiegel zentrieren{DoneSuffix(PhaseDone(1))}";
    public string Phase2Title => $"{PhaseDisplayNumber(2)}. Fangspiegel kippen{DoneSuffix(PhaseDone(2))}";
    public string Phase3Title => $"{PhaseDisplayNumber(3)}. Hauptspiegel kippen{DoneSuffix(PhaseDone(3))}";

    private void NotifyPhaseTitles()
    {
        OnPropertyChanged(nameof(Phase1Title));
        OnPropertyChanged(nameof(Phase2Title));
        OnPropertyChanged(nameof(Phase3Title));
        OnPropertyChanged(nameof(PhaseOrderHint));
        OnPropertyChanged(nameof(HasPhaseOrderHint));
    }

    // Prüfreihenfolge der Vorgänger-Phasen je aktiver Phase (Phase 0 zählt nie
    // als Vorgänger, Phase 1 nur bei justierbarer Spinne). Bei Phase 3 wird
    // zuerst die unmittelbare Vorgänger-Phase 2 genannt, erst danach Phase 1.
    private static readonly int[] EmptyPredecessors = Array.Empty<int>();
    private int[] PredecessorPhasesFor(int phase) => phase switch
    {
        2 => new[] { 1 },
        3 => new[] { 2, 1 },
        _ => EmptyPredecessors,
    };

    private string PredecessorTitle(int phase) => phase switch
    {
        1 => Phase1Title,
        2 => Phase2Title,
        _ => string.Empty,
    };

    // Sanftes Gate an den Phasen-Radios: kein Hard-Block — Fortsetzen/Springen
    // bleibt erlaubt, aber ein Hinweis macht auf eine noch offene Vorgänger-
    // Phase aufmerksam. Nur die erste nicht erfüllte Vorgänger-Phase wird genannt.
    public string? PhaseOrderHint
    {
        get
        {
            foreach (var phase in PredecessorPhasesFor(ActiveJustagePhase))
            {
                if (phase == 1 && !IsPhase1Enabled) continue;
                if (PhaseDone(phase)) continue;
                return $"⚠ {PredecessorTitle(phase)} ist noch nicht im Ziel — am besten zuerst dort weitermachen.";
            }
            return null;
        }
    }

    public bool HasPhaseOrderHint => PhaseOrderHint is not null;

    public string ActivePhaseTitle => ActiveJustagePhase switch
    {
        0 => "1. Orientierung – OAZ-Position",
        1 => "2. Fangspiegel zentrieren",
        2 => $"{PhaseDisplayNumber(2)}. Fangspiegel kippen",
        3 => $"{PhaseDisplayNumber(3)}. Hauptspiegel kippen",
        _ => "—",
    };

    // Wizard-Erklärung pro Phase: nummerierte Schritt-für-Schritt-Anleitung.
    // Bewusst ausführlich/prominent — sie ist die Hauptführung durch die Justage.
    public string PhaseGuideText
    {
        get
        {
            var n = PhaseDisplayNumber(ActiveJustagePhase);
            return ActiveJustagePhase switch
            {
                0 => $"Schritt {n} – Orientierung\n"
                   + "1. Stell mit dem Regler ein, wo der Okularauszug (OAZ) aus deiner "
                   + "Blickrichtung am Teleskop sitzt.\n"
                   + "2. Das blaue OAZ-Rohr dreht sich mit; alle folgenden Skizzen "
                   + "richten sich danach aus.\n"
                   + "3. Dann ‚Phase abgeschlossen'.",
                1 => $"Schritt {n} – Fangspiegel zentrieren (4 Spinnen-Rändelschrauben, Blick von vorn in den Tubus)\n"
                   + "1. Dreh das Spinnenkreuz per Versatz-Regler in die reale Lage "
                   + "deiner Spinne.\n"
                   + "2. Kalibriere jede Schraube (Knopf ‚Kalibrieren'): etwas drehen "
                   + "(¼ Umdrehung als Start), gedrehte Menge + Richtung angeben, "
                   + "‚Bestätigen'. Erst dann erscheinen Drehempfehlungen.\n"
                   + "3. Folge den Empfehlungen, bis der Fangspiegel mittig unter dem "
                   + "OAZ steht.",
                2 => $"Schritt {n} – Fangspiegel kippen (3 Justierschrauben)\n"
                   + "1. Kalibriere zuerst alle 3 Schrauben – ohne Kalibrierung keine "
                   + "Drehempfehlung.\n"
                   + "2. Schrauben sind anfangs fest: löse zuerst EINE Schraube "
                   + "(gegen den Uhrzeigersinn), bevor du andere anziehst.\n"
                   + "3. Folge den orangen Pfeilen (Richtung = Drehsinn, Länge = "
                   + "Umdrehung). ‚Markierung aktualisieren' prüft den Fortschritt.\n"
                   + "Ziel: zentrierter Hauptspiegel-Reflex.",
                3 => $"Schritt {n} – Hauptspiegel kippen (3 Justierschrauben, Blick von hinten auf den Tubusboden)\n"
                   + "1. Strg+Klick auf den Marker-Ring setzt das Ziel (SOLL) – "
                   + "einmalig, da die Ansicht sich beim Fangspiegel-Kippen verschoben "
                   + "hat. Bleibt dann fix.\n"
                   + "2. Klick auf die Linsenmitte setzt das Linsen-Kreuz (IST) – "
                   + "nach jeder Schraubendrehung neu (Linse sitzt nahe der "
                   + "Ausrichtung hinter dem Marker, daher von Hand).\n"
                   + "3. Konterschrauben lösen, alle 3 Justierschrauben kalibrieren "
                   + "(erst EINE gegen den Uhrzeigersinn lösen): Kalibrieren = drehen → "
                   + "Linse an ihrer neuen Position neu anklicken → 'Bestätigen'.\n"
                   + "4. Folge den Pfeilen, bis die Linse im Marker-Punkt sitzt; "
                   + "danach wieder kontern.",
                _ => "",
            };
        }
    }

    // Markier-Modus-Anleitung: eigener kurzer Text, da hier keine Phasen existieren.
    private const string MarkingsGuideText =
        "Schritt 3 – Markieren\n"
        + "1. ‚Automarkierung' erkennt OAZ-Rand, Spiegel und Marker automatisch.\n"
        + "2. Stimmt eine Markierung nicht, im Bild anklicken oder ziehen "
        + "(Pfeiltasten = fein).\n"
        + "3. Passt alles, weiter mit ‚4 Justage'.";

    // Sterntest-Modus-Anleitung: eigener kurzer Text, analog zur Justage-Führung.
    // Newton braucht zusätzlich den Paar-Schritt (intra-/extrafokal) — ohne den
    // ist der angezeigte Versatz kein Kollimationsmaß (Fangspiegel-Offset).
    private string StarTestGuideText => TelescopeType == TelescopeType.Newton
        ? "Schritt 5 – Sterntest (Feinjustage am Himmel)\n"
          + "1. Hellen Stern mittig anfahren und mittel defokussieren, bis ein Donut "
          + "sichtbar ist.\n"
          + "2. Unten die Bildquelle wählen (Datei, Ordner-Überwachung oder "
          + "Live-Kamera) und ein Bild laden.\n"
          + "3. Fokus-Mitte merken, dann je eine Aufnahme intra- und extrafokal "
          + "aufnehmen (Paar-Messung) — erst deren Vergleich zeigt den echten "
          + "Kollimationsfehler.\n"
          + "4. Schrauben kalibrieren, dann den Drehempfehlungen folgen, bis alle "
          + "✓ zeigen."
        : "Schritt 5 – Sterntest (Feinjustage am Himmel)\n"
          + "1. Hellen Stern mittig anfahren und mittel defokussieren, bis ein Donut "
          + "sichtbar ist.\n"
          + "2. Unten die Bildquelle wählen (Datei, Ordner-Überwachung oder "
          + "Live-Kamera) und ein Bild laden.\n"
          + "3. Schrauben kalibrieren, dann den Drehempfehlungen folgen, bis alle "
          + "✓ zeigen.";

    // Modusübergreifende „So geht's"-Anleitung: löst die frühere Justage-only-
    // Bindung ab, damit jeder Modus seine eigene Kurzanleitung zeigt.
    public string GuideText => IsJustageMode ? PhaseGuideText
        : IsStarTestMode ? StarTestGuideText
        : MarkingsGuideText;

    public bool HasGuideText => !string.IsNullOrEmpty(GuideText);

    // Position der „So geht's"-Box: frei verschiebbares Overlay über dem
    // Livebild statt fest in der Sidebar (die dort permanent gescrollt werden
    // musste). -1 = noch nicht positioniert; MainWindow.axaml.cs setzt beim
    // ersten Layout-Pass die Default-Position (unten links) und klemmt die Box
    // bei Verschieben/Fenster-Resize ans sichtbare Bild.
    [ObservableProperty]
    private double _guideBoxX = -1;

    [ObservableProperty]
    private double _guideBoxY = -1;

    // --- Phasen-Skizze (Schrauben-Anordnung) -------------------------------
    // Nur die beiden Kipp-Phasen zeigen eine PNG-Skizze (Orientierung und
    // Fangspiegel-Zentrierung haben generierte Indikatoren). Als eingebettete
    // Avalonia-Ressource (avares://) in der App enthalten — nicht aus dem
    // User-Config-Verzeichnis geladen, damit die Skizzen auf jedem Rechner
    // verfügbar sind. Kanonische Ausrichtung der PNGs: OAZ auf 12 Uhr — die
    // App rotiert sie um die OAZ-Position.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivePhaseSketch))]
    private Bitmap? _activePhaseSketch;

    public bool HasActivePhaseSketch => ActivePhaseSketch is not null;

    public string ActivePhaseSketchHint => ActiveJustagePhase switch
    {
        2 => "Skizze (Blick von vorn in die Tubusöffnung): die 3 Fangspiegel-"
            + "Justierschrauben auf der Fassung (120° versetzt). Sie kippen den "
            + "Fangspiegel auf den Hauptspiegel.",
        3 => "Skizze (Blick von hinten entlang des Tubus, spiegelverkehrt zur "
            + "Vorderansicht): die 3 Hauptspiegel-Justierschrauben an der "
            + "Spiegelzelle (120° versetzt). Vor dem Drehen die Konterschrauben "
            + "lösen, danach wieder kontern.",
        _ => "",
    };

    private void RefreshPhaseSketch()
    {
        var old = ActivePhaseSketch;
        ActivePhaseSketch = IsJustageMode ? LoadPhaseSketch(ActiveJustagePhase) : null;
        if (!ReferenceEquals(old, ActivePhaseSketch))
        {
            old?.Dispose();
        }
        OnPropertyChanged(nameof(ActivePhaseSketchHint));
    }

    // Sprechender Dateiname pro Phase (nur die Kipp-Phasen haben eine PNG-Skizze).
    private static string? SketchFileFor(int phase) => phase switch
    {
        2 => "sketch-fangspiegel-kippen.png",
        3 => "sketch-hauptspiegel-kippen.png",
        _ => null,
    };

    private static Bitmap? LoadPhaseSketch(int phase)
    {
        if (SketchFileFor(phase) is not { } file) return null;
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://FreeCol/Assets/{file}"));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private (double dx, double dy, double mag, double tol)? ActivePhaseOffset => PhaseOffset(ActiveJustagePhase);

    // IST↔SOLL-Versatz einer Phase — für die aktive Anzeige UND die
    // Erledigt-Häkchen an den Phasen-Radiobuttons (alle Phasen gleichzeitig).
    // Bei Auflösungs-Mismatch gelten die Markierungen als nicht verwertbar
    // (falsch skalierte Koordinaten) — kein Versatz, aber die Daten bleiben
    // erhalten (siehe MarkingsResolutionMismatch).
    private (double dx, double dy, double mag, double tol)? PhaseOffset(int phase)
    {
        if (MarkingsResolutionMismatch) return null;
        switch (phase)
        {
            case 1:
                if (!CurrentMarkings.OazRand.IsPlaced || !CurrentMarkings.Sekundaer.IsPlaced) return null;
                var d1x = CurrentMarkings.Sekundaer.CenterX - CurrentMarkings.OazRand.CenterX;
                var d1y = CurrentMarkings.Sekundaer.CenterY - CurrentMarkings.OazRand.CenterY;
                return (d1x, d1y, Math.Sqrt(d1x * d1x + d1y * d1y), TolerancePhase1Px);
            case 2:
                if (!CurrentMarkings.HauptspiegelReflex.IsPlaced || !CurrentMarkings.Sekundaer.IsPlaced) return null;
                var d2x = CurrentMarkings.HauptspiegelReflex.CenterX - CurrentMarkings.Sekundaer.CenterX;
                var d2y = CurrentMarkings.HauptspiegelReflex.CenterY - CurrentMarkings.Sekundaer.CenterY;
                return (d2x, d2y, Math.Sqrt(d2x * d2x + d2y * d2y), TolerancePhase2Px);
            case 3:
                // Hauptspiegel kippen: Linse (IST) in den Marker-Punkt (SOLL)
                // ziehen. Der Marker klebt zentral auf dem HS und ist bei
                // korrekt gekipptem Fangspiegel bereits zentriert — er ist die
                // Referenz, nicht das optische Zentrum aus der Kalibrierung.
                if (!CurrentMarkings.Linse.IsPlaced || !CurrentMarkings.Marker.IsPlaced) return null;
                var d3x = CurrentMarkings.Linse.CenterX - CurrentMarkings.Marker.CenterX;
                var d3y = CurrentMarkings.Linse.CenterY - CurrentMarkings.Marker.CenterY;
                return (d3x, d3y, Math.Sqrt(d3x * d3x + d3y * d3y), TolerancePhase3Px);
            default:
                return null;
        }
    }

    // Phase erledigt = Versatz unter Toleranz (Phase 0/Orientierung hat kein Maß).
    private bool PhaseDone(int phase) => PhaseOffset(phase) is { } o && o.mag <= o.tol;
    private static string DoneSuffix(bool done) => done ? " ✓" : "";

    // Für jede Phase die für ihren Versatz nötigen Markierungs-Arten — Basis für
    // die konkrete Fehlmeldung, welche Markierung(en) noch fehlen.
    private static MarkingKind[] RequiredMarkingsFor(int phase) => phase switch
    {
        1 => new[] { MarkingKind.OazRand, MarkingKind.Sekundaer },
        2 => new[] { MarkingKind.HauptspiegelReflex, MarkingKind.Sekundaer },
        3 => new[] { MarkingKind.Linse, MarkingKind.Marker },
        _ => Array.Empty<MarkingKind>(),
    };

    // Namen der noch fehlenden Markierungen einer Phase, in derselben
    // laienverständlichen Form wie die Sidebar (MarkingViewModel.Name).
    private string MissingMarkingNamesFor(int phase) => string.Join(", ",
        RequiredMarkingsFor(phase)
            .Where(k => !CurrentMarkings[k].IsPlaced)
            .Select(k => MarkingVms.First(v => v.Kind == k).Name));

    public string ActivePhaseStatusText
    {
        get
        {
            if (ActivePhaseOffset is not { } off)
            {
                var missing = MissingMarkingNamesFor(ActiveJustagePhase);
                if (missing.Length == 0)
                    return "Markierungen unvollständig — bitte erst die nötigen Markierungen setzen.";
                // Phase 3 nutzt eine andere Setz-Geste als die übrigen Markierungen
                // (Automarkierung/Nudge) — daher der Zusatzhinweis nur dort.
                var placementHint = ActiveJustagePhase == 3
                    ? " (Klick = Linse, Strg+Klick = Marker)"
                    : "";
                return $"Markierungen unvollständig — es fehlt: {missing}.{placementHint}";
            }

            // Versatz = Abstand IST↔SOLL in Pixeln. Klartext-Wertung, damit ein
            // unerfahrener Nutzer sofort sieht, ob der Wert gut ist und was zu tun bleibt.
            var detail = $"Δx={off.dx:+0.0;-0.0;0.0}, Δy={off.dy:+0.0;-0.0;0.0} px";
            if (off.mag <= off.tol)
                return $"Versatz {off.mag:F1} px — GUT ✓ im Ziel (≤ {off.tol:F0} px). {detail}";

            var remaining = off.mag - off.tol;
            var verdict = off.mag <= off.tol * 2 ? "WARNUNG" : "KRITISCH";
            return $"Versatz {off.mag:F1} px — {verdict}: noch {remaining:F0} px bis zum Ziel (≤ {off.tol:F0} px). {detail}";
        }
    }

    public bool ActivePhaseUnderTolerance
        => ActivePhaseOffset is { } off && off.mag <= off.tol;

    // Schnellverfahren „Phase abgeschlossen" (Zweistufen-Muster wie Kalibrierung
    // löschen, siehe DeleteCalibration): liegt der Versatz NICHT unter Toleranz,
    // schaltet der erste Klick nur scharf und warnt; erst der zweite Klick
    // schließt die Phase trotzdem ab. Kein Timer nötig — entschärft wird gezielt
    // bei Phasenwechsel, Verlassen der Justage oder neuer Messung.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompletePhaseButtonText))]
    private bool _isPhaseCompleteArmed;

    public string CompletePhaseButtonText => IsPhaseCompleteArmed
        ? "Nicht im Ziel — erneut klicken schließt trotzdem ab"
        : "Phase abgeschlossen";

    // Kumulative Netto-Drehung je Schraube (über die Justage-Iterationen), um ein
    // Herausdrehen zu erkennen: wird eine Schraube immer wieder in dieselbe (Lösen-)
    // Richtung empfohlen, kann sie den Kontakt verlieren und läuft schließlich aus
    // dem Gewinde. Wir summieren bei jedem „Markierung aktualisieren" die zuvor
    // angezeigte (also vom Anwender ausgeführte) Empfehlung auf.
    private const double ScrewRunoutWarnTurns = 3.0;
    private readonly ScrewTravelTracker _screwTravel = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScrewTravelWarning))]
    private string _screwTravelWarning = "";

    public bool HasScrewTravelWarning => !string.IsNullOrEmpty(ScrewTravelWarning);

    private void ResetCumulativeTurns()
    {
        _screwTravel.Clear();
        ScrewTravelWarning = "";
    }

    // Vor der Neu-Erfassung die zuletzt angezeigte Empfehlung als „ausgeführt"
    // aufsummieren und auf Herausdrehen prüfen.
    private void AccumulateAppliedTurnsAndCheck()
    {
        var applied = RecommendedTurns();
        var phaseScrews = CurrentScrews.ForPhase(ActiveJustagePhase).ToList();

        _screwTravel.Accumulate(phaseScrews
            .Where(s => applied.ContainsKey(s.Name))
            .Select(s => new System.Collections.Generic.KeyValuePair<string, double>(s.Name, applied[s.Name])));

        string warn = "";
        if (_screwTravel.FindRunout(phaseScrews.Select(s => s.Name), ScrewRunoutWarnTurns) is { } runout)
        {
            var slot = phaseScrews.FindIndex(s => s.Name == runout.Name);
            warn = $"Schraube '{ScrewDisplayName(phaseScrews[slot], slot)}' wurde bereits {-runout.Cumulative:0.0} Umdrehungen "
                 + "herausgedreht (CCW) — sie könnte den Kontakt verlieren. Setze sie wieder "
                 + "hinein und löse stattdessen eine andere Schraube.";
        }
        ScrewTravelWarning = warn;
    }

    // Frische-Tracking für die OCAL-Messungen je Phase: rein laufzeitbezogen
    // (nicht persistiert) — nach Programmstart/Kamerawechsel gilt jede aus dem
    // Store geladene Markierung als potenziell veraltet, bis sie neu gemessen
    // oder von Hand gesetzt wurde. Macht sichtbar, wenn die angezeigte Dreh-
    // empfehlung noch auf einer alten (gespeicherten) Messung statt dem
    // aktuellen Bild beruht (siehe ShowStaleRecommendationHint).
    private readonly HashSet<int> _freshlyMeasuredPhases = new();

    private void MarkPhasesFreshlyMeasured(params int[] phases)
    {
        foreach (var phase in phases) _freshlyMeasuredPhases.Add(phase);
        NotifyStaleRecommendationHint();
    }

    private void ResetFreshlyMeasuredPhases()
    {
        if (_freshlyMeasuredPhases.Count == 0) return;
        _freshlyMeasuredPhases.Clear();
        NotifyStaleRecommendationHint();
    }

    private void NotifyStaleRecommendationHint()
    {
        OnPropertyChanged(nameof(ShowStaleRecommendationHint));
        OnPropertyChanged(nameof(StaleRecommendationHint));
    }

    // Sichtbar, sobald für die aktive Spiegel-Phase (1–3) bereits Markierungen
    // vorliegen (PhaseOffset berechenbar), diese aber seit dem letzten Reset
    // (Kamera-Start/-Stop, Belichtungs-/Fokus-Änderung) noch nicht frisch neu
    // gemessen wurden — die gezeigte Drehempfehlung stammt dann noch aus einer
    // gespeicherten (evtl. veralteten) Messung.
    public bool ShowStaleRecommendationHint
        => IsJustageMode && ActiveJustagePhase is >= 1 and <= 3
           && ActivePhaseOffset is not null
           && !_freshlyMeasuredPhases.Contains(ActiveJustagePhase);

    public string StaleRecommendationHint
        => "⚠ Anzeige basiert auf gespeicherten Markierungen — „Markierung aktualisieren“ misst auf dem aktuellen Bild neu.";

    [RelayCommand]
    private void ToggleJustageMode()
    {
        IsJustageMode = !IsJustageMode;
        // Die zuletzt aktive Phase bleibt erhalten — ein Abstecher in Markierungen/
        // Sterntest wirft den Nutzer nicht auf Phase 0 zurück. Erst-Einstieg startet
        // bei 0 (Feld-Default); nach ABGESCHLOSSENER Justage setzt CompletePhase()
        // selbst auf 0 zurück, sodass die nächste Justage wieder vorn beginnt.
        if (IsJustageMode) IsStarTestMode = false;
    }

    [RelayCommand]
    private void ToggleStarTestMode()
    {
        IsStarTestMode = !IsStarTestMode;
        if (IsStarTestMode) IsJustageMode = false;
    }

    // Schrauben-Kalibrierungs-Wizard
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScrewCalibrationIdle))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseScrewMarkers))]
    [NotifyPropertyChangedFor(nameof(ShowCalibrationGate))]
    [NotifyPropertyChangedFor(nameof(CalibrationArrowGeometry))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseArrowsGeometry))]
    [NotifyPropertyChangedFor(nameof(ActivePhaseTurnLabels))]
    private bool _isScrewCalibrationActive;

    public bool IsScrewCalibrationIdle => !IsScrewCalibrationActive;

    [ObservableProperty]
    private string _screwCalibrationInstruction = "";

    // Drehrichtung beim Kalibrieren — manche Schrauben sind fest und lassen sich
    // nur in eine Richtung drehen. Gespeichert wird intern immer der Effekt pro
    // CW-Umdrehung; bei CCW wird der gemessene Vektor vorher negiert.
    // Default CCW: bei festgezogenen Schrauben (Normalzustand) ist als erste
    // Bewegung nur Lösen (gegen den Uhrzeigersinn) möglich.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScrewCcwSelected))]
    [NotifyPropertyChangedFor(nameof(CalibrationArrowGeometry))]
    private bool _isScrewCwSelected;

    public bool IsScrewCcwSelected
    {
        get => !IsScrewCwSelected;
        set { if (value) IsScrewCwSelected = false; else IsScrewCwSelected = true; }
    }

    // Tatsächlich gedrehte Menge in Umdrehungen (geteilt von Justage- und Sterntest-
    // Kalibrierung, wie die Drehrichtung). Der Effekt pro Umdrehung entsteht durch
    // Division — bewegt ¼ Umdrehung die Markierung weniger als die Rausch-Schwelle,
    // dreht man weiter und trägt die Gesamtmenge hier ein; eine fest angenommene
    // ¼-Umdrehung machte die Empfehlungen sonst um den Faktor der Mehr-Drehung zu klein.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalibrationTurnsInvalid))]
    private string _calibrationTurnsText = "0,25";

    public bool CalibrationTurnsInvalid => ScrewCalibrationMath.ParseTurns(CalibrationTurnsText) is null;

    [RelayCommand]
    private void SetCalibrationTurns(string turns) => CalibrationTurnsText = turns;

    private string? _calibratingScrewName;
    private string? _calibratingScrewDisplay;
    private int _calibratingScrewPhase;
    private double _calibrationBaselineX;
    private double _calibrationBaselineY;

    private MarkingKind MovingKindForPhase(int phase) => JustagePhaseModel.MovingKind(phase);

    private void StartScrewCalibration(ScrewViewModel vm)
    {
        var movingKind = MovingKindForPhase(vm.Phase);
        var m = CurrentMarkings[movingKind];
        if (!m.IsPlaced)
        {
            StatusText = $"Kalibrierung: '{vm.DisplayName}' braucht eine platzierte {movingKind}-Markierung als Baseline.";
            return;
        }
        _calibratingScrewName = vm.Name;
        _calibratingScrewDisplay = vm.DisplayName;
        _calibratingScrewPhase = vm.Phase;
        _calibrationBaselineX = m.CenterX;
        _calibrationBaselineY = m.CenterY;
        // Phase 3 misst nicht automatisch (Linse/Marker fast konzentrisch,
        // siehe RefreshActiveMarkingCoreAsync) — der Nutzer muss die Linse nach
        // dem Dreh selbst neu anklicken, sonst bliebe die Baseline unverändert.
        ScrewCalibrationInstruction = vm.Phase == 3
            ? $"Drehe Schraube '{vm.DisplayName}' — ¼ Umdrehung als Startwert. Klicke danach die "
              + "Linse an ihrer neuen Position im Bild an (IST neu setzen), trage die gedrehte "
              + "Menge + Richtung ein, dann 'Bestätigen'."
            : $"Drehe Schraube '{vm.DisplayName}' — ¼ Umdrehung als Startwert. Trage unten ein, "
              + "wie viel du wirklich gedreht hast, wähle die Richtung, dann 'Bestätigen'.";
        IsScrewCalibrationActive = true;
    }

    [RelayCommand]
    private async Task ConfirmScrewCalibrationAsync(CancellationToken ct)
    {
        var source = _source;
        if (source is null || _calibratingScrewName is null)
        {
            IsScrewCalibrationActive = false;
            return;
        }

        var movingKind = MovingKindForPhase(_calibratingScrewPhase);
        Marking? detected;
        if (_calibratingScrewPhase == 3)
        {
            // Phase 3 erkennt die Linse nicht automatisch (fast konzentrisch mit
            // dem Marker, siehe RefreshActiveMarkingCoreAsync) — der Nutzer hat
            // sie nach dem Dreh per Klick neu gesetzt. Bestätigen misst diese
            // manuell gesetzte Position gegen die Baseline, statt neu zu suchen.
            detected = ConfirmPhase3ManualMeasurement();
            if (detected is null) return;
        }
        else
        {
            StatusText = $"Kalibrierung '{_calibratingScrewDisplay ?? _calibratingScrewName}': Markierung neu erfassen …";
            try
            {
                detected = await DetectOnceAsync(source, movingKind, CurrentMarkings[movingKind], ct);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Kalibrierung abgebrochen.";
                IsScrewCalibrationActive = false;
                return;
            }

            if (detected is null)
            {
                StatusText = "Kalibrierung: Markierung nach der Schraubendrehung nicht wiedererkannt — evtl. zu weit gedreht oder unscharf. Schärfe/Belichtung prüfen und erneut bestätigen.";
                return;
            }
        }

        if (ScrewCalibrationMath.ParseTurns(CalibrationTurnsText) is not { } turns)
        {
            StatusText = $"Kalibrierung: Drehmenge '{CalibrationTurnsText}' ungültig — z. B. 0,25 / 0,5 / 1 eingeben.";
            return;
        }

        var dx = detected.CenterX - _calibrationBaselineX;
        var dy = detected.CenterY - _calibrationBaselineY;
        var (effectDx, effectDy) = ScrewCalibrationMath.EffectPerTurn(dx, dy, turns, IsScrewCwSelected);
        var existing = CurrentScrews.Screws.First(s =>
            s.Name == _calibratingScrewName && s.Phase == _calibratingScrewPhase);
        var updated = existing with
        {
            EffectDx = effectDx,
            EffectDy = effectDy,
            IsCalibrated = true,
            CalibratedAt = DateTimeOffset.Now,
        };
        CurrentScrews = CurrentScrews.Replace(updated);
        PersistScrews();
        UpdateMarking(movingKind, detected, persist: true);

        AppendJustageTrace($"nach Kalibrierung '{_calibratingScrewDisplay ?? updated.Name}' " +
            $"({(IsScrewCwSelected ? "CW" : "CCW")}, gemessen {turns:0.##} Umdr dx={dx:F2} dy={dy:F2})");
        StatusText = $"Schraube '{_calibratingScrewDisplay ?? updated.Name}' kalibriert: Δ pro Umdrehung ≈ ({updated.EffectDx:F1}, {updated.EffectDy:F1}) px.";
        IsScrewCalibrationActive = false;
        _calibratingScrewName = null;
        _calibratingScrewDisplay = null;
    }

    // Phase 3: liest die aktuell gesetzte Linsen-Position statt sie neu zu
    // erkennen. Liefert null (und lässt die Kalibrierung offen), solange sich
    // die Position seit dem Kalibrier-Start nicht sichtbar verändert hat — das
    // ist der Fall, wenn der Nutzer vergessen hat, die Linse nach dem
    // Schrauben-Dreh neu anzuklicken.
    private Marking? ConfirmPhase3ManualMeasurement()
    {
        var current = CurrentMarkings[MarkingKind.Linse];
        var dx = current.CenterX - _calibrationBaselineX;
        var dy = current.CenterY - _calibrationBaselineY;
        if (!ScrewCalibrationMath.HasMovedEnough(dx, dy))
        {
            StatusText = "Kalibrierung: Erst die Linse an ihrer neuen Position anklicken "
                + "(Klick ins Bild), dann 'Bestätigen'.";
            return null;
        }
        return current;
    }

    [RelayCommand]
    private void CancelScrewCalibration()
    {
        IsScrewCalibrationActive = false;
        _calibratingScrewName = null;
        _calibratingScrewDisplay = null;
        StatusText = "Schrauben-Kalibrierung abgebrochen.";
    }

    private void PersistScrews()
    {
        var key = CalibrationWizard.CameraKey;
        if (string.IsNullOrEmpty(key)) return;
        try { _screwStore.Save(key, CurrentScrews); } catch { /* nicht kritisch */ }
    }

    // Diagnose-Trace für den Justage-Konvergenztest: hält bei jeder Aktualisierung
    // Versatz, empfohlene Umdrehungen und gelernte Effekt-Vektoren fest, damit sich
    // Über-/Unterschwingen über mehrere Iterationen objektiv auswerten lässt.
    // Datei: <Config>/justage-trace.log.
    private void AppendJustageTrace(string eventLabel)
    {
        try
        {
            var dir = CalibrationStore.GetDefaultDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "justage-trace.log");
            var sb = new System.Text.StringBuilder();
            sb.Append(System.DateTimeOffset.Now.ToString("HH:mm:ss"));
            sb.Append("  Phase ").Append(ActiveJustagePhase);
            sb.Append("  ").Append(eventLabel);
            if (ActivePhaseOffset is { } off)
                sb.Append($"  | Versatz dx={off.dx:F2} dy={off.dy:F2} |Δ|={off.mag:F2} (tol {off.tol:F0})");
            else
                sb.Append("  | Versatz: n/a");

            var turns = RecommendedTurns();
            sb.Append("  | Umdr:");
            if (turns.Count == 0) sb.Append(" (gegated/keine)");
            var slot = 0;
            foreach (var s in CurrentScrews.ForPhase(ActiveJustagePhase))
            {
                if (turns.TryGetValue(s.Name, out var t))
                    sb.Append($" {ScrewDisplayName(s, slot)}={t:+0.00;-0.00}");
                slot++;
            }

            sb.Append("  | Effekt/Umdr:");
            slot = 0;
            foreach (var s in CurrentScrews.ForPhase(ActiveJustagePhase))
            {
                if (s.IsCalibrated)
                    sb.Append($" {ScrewDisplayName(s, slot)}=({s.EffectDx:F1},{s.EffectDy:F1})");
                slot++;
            }
            sb.Append('\n');
            File.AppendAllText(path, sb.ToString());
        }
        catch { /* Logging darf die Justage nie stören */ }
    }

    [RelayCommand]
    private async Task RefreshActiveMarkingAsync(CancellationToken ct)
    {
        EnterBusy();
        try { await RefreshActiveMarkingCoreAsync(ct); }
        finally { ExitBusy(); }
    }

    private async Task RefreshActiveMarkingCoreAsync(CancellationToken ct)
    {
        var source = _source;
        if (source is null)
        {
            StatusText = "Aktualisierung: keine Kamera aktiv.";
            return;
        }

        // Die aktuell angezeigte Empfehlung wurde vom Anwender ausgeführt, bevor er
        // aktualisiert → aufsummieren und auf Herausdrehen prüfen, BEVOR die neue
        // Messung die Empfehlung verändert. NUR wenn die zuvor angezeigte
        // Empfehlung selbst aus einer frischen Messung stammte — sonst würde eine
        // geladene Alt-Empfehlung (aus dem Store) fälschlich als „ausgeführt" verbucht.
        if (_freshlyMeasuredPhases.Contains(ActiveJustagePhase))
        {
            AccumulateAppliedTurnsAndCheck();
        }

        // Phase 3 detektiert nicht: Marker (SOLL) und Linse (IST) liegen im
        // Überlappungsfall fast konzentrisch und sind nicht zuverlässig auto-
        // trennbar — der Anwender setzt beide von Hand (Klick = Linse,
        // Strg+Klick = Marker). „Aktualisieren" verbucht hier nur die ausgeführte
        // Drehung (Herausdreh-Prüfung) und schreibt den Trace.
        if (ActiveJustagePhase == 3)
        {
            AppendJustageTrace("nach Aktualisierung (manuell)");
            StatusText = "Phase 3: Linse per Klick, Marker per Strg+Klick setzen.";
            return;
        }

        var (kind, name) = ActiveJustagePhase switch
        {
            1 => (MarkingKind.Sekundaer, "Sekundär"),
            2 => (MarkingKind.HauptspiegelReflex, "Hauptspiegel-Reflex"),
            _ => (MarkingKind.Sekundaer, "Sekundär"),
        };

        StatusText = $"Aktualisierung: {name} …";
        try
        {
            var current = CurrentMarkings[kind];
            var detected = await DetectOnceAsync(source, kind, current, ct);
            if (detected is null)
            {
                StatusText = $"Aktualisierung: {name} nicht erkannt — Belichtung/Schärfe anpassen oder die Markierung manuell setzen.";
                return;
            }
            UpdateMarking(kind, detected, persist: true);
            MarkPhasesFreshlyMeasured(ActiveJustagePhase);
            AppendJustageTrace("nach Aktualisierung");
            StatusText = $"Aktualisierung: {name} aktualisiert.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Aktualisierung abgebrochen.";
        }
        catch (Exception ex)
        {
            StatusText = $"Aktualisierung fehlgeschlagen: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CompletePhase()
    {
        if (ActiveJustagePhase == 0)
        {
            // Orientierung gesetzt → weiter zur ersten Spiegel-Phase (kein Maß, kein Schnellverfahren nötig).
            ActiveJustagePhase = FirstMirrorPhase;
            StatusText = $"Justage: weiter mit {ActivePhaseTitle}.";
            return;
        }

        // Phasen 1–3: unter Toleranz schließt sofort ab. Sonst erst scharf
        // schalten (Warnung) und beim zweiten Klick trotzdem abschließen.
        if (!ActivePhaseUnderTolerance && !IsPhaseCompleteArmed)
        {
            IsPhaseCompleteArmed = true;
            StatusText = "Phase nicht im Ziel — zum trotzdem abschließen erneut klicken.";
            return;
        }
        IsPhaseCompleteArmed = false;

        if (ActiveJustagePhase < 3)
        {
            ActiveJustagePhase++;
            StatusText = $"Justage: weiter mit {ActivePhaseTitle}.";
        }
        else
        {
            IsJustageMode = false;
            ActiveJustagePhase = 0;
            IsJustageComplete = true;
            StatusText = "Justage abgeschlossen — weiter mit dem Sterntest.";
        }
    }

    partial void OnIsJustageModeChanged(bool value)
    {
        ResetCumulativeTurns();
        RecomputeOverlayDisplay();
        RefreshPhaseSketch();
        NotifyFrameGestureHint();
        RefreshWorkflowSteps();
        IsPhaseCompleteArmed = false;
        // Erneuter Eintritt in die Justage = bewusstes Neu-Justieren — der
        // Abschluss-Banner (Handoff zum Sterntest) verliert dann seine Gültigkeit.
        if (value) IsJustageComplete = false;
    }

    partial void OnIsStarTestModeChanged(bool value)
    {
        RecomputeOverlayDisplay();
        if (!value)
        {
            // Sterntest verlassen: Ordner-Überwachung stoppen, geladenes Bild
            // verwerfen, laufende Kalibrierung abbrechen (Kalibrierdaten bleiben
            // persistiert).
            StopStarWatch();
            StopAlpaca();
            StopAsi();
            StopFocuser();
            _starGray?.Dispose();
            _starGray = null;
            _donut = null;
            _starCropRect = default;
            _calibratingStarScrew = null;
            IsStarScrewCalibrating = false;
            ShowStarScrewDecision = false;
            _starFrameSourceText = "";
            StarCollimationLost = false;
            _pairSlotA = null;
            _pairSlotB = null;
            PairMeasurementResultText = "";
        }
        else
        {
            // Beim Betreten: geladene Schrauben-Kalibrierung still übernommen
            // (bisheriger Default) — der Banner macht die Entscheidung darüber
            // sichtbar, sofern bereits vollständig kalibriert vorliegt.
            ShowStarScrewDecision = StarScrewsFullyCalibrated;
        }
        NotifyStarTestReadouts();
        NotifyFrameGestureHint();
        OnPropertyChanged(nameof(ShowNoCameraGate));
        RefreshWorkflowSteps();
    }

    partial void OnActiveJustagePhaseChanged(int value)
    {
        // Pro Phase eigene Schrauben → kumulative Zählung neu beginnen.
        ResetCumulativeTurns();
        RecomputeOverlayDisplay();
        RefreshPhaseSketch();
        NotifyFrameGestureHint();
        IsPhaseCompleteArmed = false;
    }

    // Bei Auflösungs-Mismatch ist das optische Zentrum aus der Kalibrierung
    // nicht auf den aktuellen Frame anwendbar — Anzeige unterdrücken statt
    // falsch skaliert zu zeigen (siehe CalibrationResolutionMismatch).
    public bool HasOffsetReading
        => CurrentCalibration is not null && CurrentMarkings.Marker.IsPlaced
           && !CalibrationResolutionMismatch;

    private double OffsetDx => CurrentCalibration is { } c && CurrentMarkings.Marker.IsPlaced
        ? CurrentMarkings.Marker.CenterX - c.OpticalCenter.X
        : 0;
    private double OffsetDy => CurrentCalibration is { } c && CurrentMarkings.Marker.IsPlaced
        ? CurrentMarkings.Marker.CenterY - c.OpticalCenter.Y
        : 0;

    public string OffsetText
    {
        get
        {
            if (!HasOffsetReading) return "—";
            var mag = Math.Sqrt(OffsetDx * OffsetDx + OffsetDy * OffsetDy);
            return $"Δ {mag:F1} px  (Δx={OffsetDx:+0.0;-0.0;0.0}, Δy={OffsetDy:+0.0;-0.0;0.0})";
        }
    }

    public Avalonia.Point ArrowEndPoint
    {
        get
        {
            if (!HasOffsetReading) return new Avalonia.Point(ArrowCenter, ArrowCenter);
            var max = Math.Max(Math.Abs(OffsetDx), Math.Abs(OffsetDy));
            if (max < 0.1) return new Avalonia.Point(ArrowCenter, ArrowCenter);
            var scale = ArrowMaxDisplacement / max;
            return new Avalonia.Point(ArrowCenter + OffsetDx * scale, ArrowCenter + OffsetDy * scale);
        }
    }

    // Schritt-1-Justage: Sekundärmitte → Tubusmitte (radial unter den Fokuser).
    public bool HasSekundaerToOazRand
        => CurrentMarkings.OazRand.IsPlaced && CurrentMarkings.Sekundaer.IsPlaced;

    public string SekundaerToOazRandText
    {
        get
        {
            if (!HasSekundaerToOazRand) return "—";
            var dx = CurrentMarkings.Sekundaer.CenterX - CurrentMarkings.OazRand.CenterX;
            var dy = CurrentMarkings.Sekundaer.CenterY - CurrentMarkings.OazRand.CenterY;
            var mag = Math.Sqrt(dx * dx + dy * dy);
            return $"Δ {mag:F1} px  (Δx={dx:+0.0;-0.0;0.0}, Δy={dy:+0.0;-0.0;0.0})";
        }
    }

    // Schritt-2-Justage: Hauptspiegelreflex → Sekundärmitte (Sekundär-Tilt).
    public bool HasHsrToSekundaer
        => CurrentMarkings.HauptspiegelReflex.IsPlaced && CurrentMarkings.Sekundaer.IsPlaced;

    public string HsrToSekundaerText
    {
        get
        {
            if (!HasHsrToSekundaer) return "—";
            var dx = CurrentMarkings.HauptspiegelReflex.CenterX - CurrentMarkings.Sekundaer.CenterX;
            var dy = CurrentMarkings.HauptspiegelReflex.CenterY - CurrentMarkings.Sekundaer.CenterY;
            var mag = Math.Sqrt(dx * dx + dy * dy);
            return $"Δ {mag:F1} px  (Δx={dx:+0.0;-0.0;0.0}, Δy={dy:+0.0;-0.0;0.0})";
        }
    }

    // Sekundär-Exzentrizität als Tilt-Indikator (0 = perfekt rund).
    public bool HasSekundaerEccentricity => CurrentMarkings.Sekundaer.IsPlaced;

    public string SekundaerEccentricityText
    {
        get
        {
            if (!HasSekundaerEccentricity) return "—";
            return $"e = {CurrentMarkings.Sekundaer.Eccentricity:F3}";
        }
    }

    // Frame-Geometrie für Pointer→Frame-Koordinaten-Mapping. Werden im Capture-Loop
    // gesetzt; Pointer-Handler lesen sie vom UI-Thread. Atomare 32-bit-Reads reichen.
    private int _frameWidth;
    private int _frameHeight;
    private int _croppedWidth;
    private int _croppedHeight;

    public int FrameWidth => _frameWidth;
    public int FrameHeight => _frameHeight;
    public int CroppedWidth => _croppedWidth;
    public int CroppedHeight => _croppedHeight;

    private DisplayTransform _displayTransform;
    private double _lastControlWidth;
    private double _lastControlHeight;

    private const double ReticleArmPx = 5;
    private const double ActiveCrossArmPx = 8;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReticleTopLeftX))]
    private double _opticalCenterDisplayX;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReticleTopLeftY))]
    private double _opticalCenterDisplayY;

    [ObservableProperty]
    private bool _showReticle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveCrossTopLeftX))]
    private double _activeCrossDisplayX;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveCrossTopLeftY))]
    private double _activeCrossDisplayY;

    [ObservableProperty]
    private bool _showActiveCross;

    // Linsen-Mittelpunkt: dauerhaft sichtbar (unabhängig von der Selektion), weil
    // die Linse keinen physischen Markierungsring hat — ihre Lage soll immer
    // erkennbar sein, vor allem für die Hauptspiegel-Kipp-Phase (Linse → Marker).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinseCrossTopLeftX))]
    private double _linseCrossDisplayX;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinseCrossTopLeftY))]
    private double _linseCrossDisplayY;

    [ObservableProperty]
    private bool _showLinseCross;

    // Avalonia positioniert ein Path-Element per Bounding-Box-Top-Left, nicht per
    // Data-Origin. Daten verwenden daher (0..2·arm)-Koords mit Cross-Mitte bei
    // (arm, arm); die Canvas.Left/Top-Bindings ziehen das Center auf die Soll-
    // Position vor.
    public double ReticleTopLeftX => OpticalCenterDisplayX - ReticleArmPx;
    public double ReticleTopLeftY => OpticalCenterDisplayY - ReticleArmPx;
    public double ActiveCrossTopLeftX => ActiveCrossDisplayX - ActiveCrossArmPx;
    public double ActiveCrossTopLeftY => ActiveCrossDisplayY - ActiveCrossArmPx;
    public double LinseCrossTopLeftX => LinseCrossDisplayX - ActiveCrossArmPx;
    public double LinseCrossTopLeftY => LinseCrossDisplayY - ActiveCrossArmPx;

    [ObservableProperty]
    private IBrush _activeCrossBrush = Brushes.White;

    // Fokus-ROI-Overlay (Box am Fokus-Anker) der selektierten Markierung — zeigt,
    // wo der eigene Autofokus die Schärfe misst.
    [ObservableProperty] private bool _showFocusRoi;
    [ObservableProperty] private IBrush _focusRoiBrush = Brushes.Yellow;
    [ObservableProperty] private double _focusRoiLeft;
    [ObservableProperty] private double _focusRoiTop;
    [ObservableProperty] private double _focusRoiSize;

    // Justage-Overlay: gestrichelter SOLL-Kreis + dicker Pfeil von IST nach SOLL
    // im Live-Bild. Nur sichtbar im Justage-Modus, wenn die zur Phase passenden
    // Markierungen platziert sind.
    [ObservableProperty]
    private double _justageSollLeft;

    [ObservableProperty]
    private double _justageSollTop;

    [ObservableProperty]
    private double _justageSollWidth;

    [ObservableProperty]
    private double _justageSollHeight;

    [ObservableProperty]
    private Avalonia.Point _justageIstPoint;

    [ObservableProperty]
    private Avalonia.Point _justageSollPoint;

    [ObservableProperty]
    private IBrush _justageOverlayBrush = Brushes.White;

    [ObservableProperty]
    private bool _showJustageOverlay;

    // IST→SOLL-Pfeil getrennt vom gestrichelten SOLL-Kreis: in der Kipp-Phase
    // bleibt nur der SOLL-Kreis (die echte Linse ist das IST und im Bild sichtbar).
    [ObservableProperty]
    private bool _showJustageArrow;

    public CalibrationWizardViewModel CalibrationWizard { get; }

    public MarkingViewModel OazRandVm { get; }
    public MarkingViewModel HauptspiegelReflexVm { get; }
    public MarkingViewModel SekundaerVm { get; }
    public MarkingViewModel MarkerVm { get; }
    public MarkingViewModel LinseVm { get; }
    public System.Collections.Generic.IReadOnlyList<MarkingViewModel> MarkingVms { get; }

    private static readonly Scalar OazRandColor = new(0, 255, 0);        // BGR grün
    private static readonly Scalar MarkerColor = new(0, 0, 255);       // BGR rot
    // Markierungs- und Reticle-Farben leben jetzt im Avalonia-Overlay (siehe
    // MarkingViewModel.Swatch und MainWindow.axaml-Bindings).
    private static readonly Scalar CalibrationSampleColor = new(255, 200, 0);     // BGR cyan-blau
    private static readonly Scalar CalibrationFitColor = new(0, 255, 255);        // BGR gelb
    private static readonly Scalar CalibrationOrientOkColor = new(0, 255, 0);     // BGR grün
    private static readonly Scalar CalibrationOrientFailColor = new(0, 0, 255);   // BGR rot

    // Workflow-Leiste: macht die fachliche Soll-Reihenfolge sichtbar (bisher nur
    // implizit über die Panel-Anordnung) und zeigt je Schritt Erledigt-/Aktiv-Status.
    public ObservableCollection<WorkflowStepVm> WorkflowSteps { get; }

    private void ActivateMarkingsMode() { IsJustageMode = false; IsStarTestMode = false; }
    private void ActivateJustageMode() { IsStarTestMode = false; IsJustageMode = true; }
    private void ActivateStarTestMode() { IsJustageMode = false; IsStarTestMode = true; }

    /// <summary>Aktueller Modus als persistierbarer Schlüssel (window-state.json).</summary>
    public string CurrentModeKey => IsJustageMode ? "justage" : IsStarTestMode ? "startest" : "markings";

    /// <summary>Stellt Modus + Justage-Phase aus dem persistierten Zustand wieder her,
    /// damit ein Neustart mitten in der Justage dort weitermacht.</summary>
    public void RestoreMode(string? mode, int justagePhase)
    {
        if (justagePhase is >= 0 and <= 3) ActiveJustagePhase = justagePhase;
        switch (mode)
        {
            case "justage": ActivateJustageMode(); break;
            case "startest": ActivateStarTestMode(); break;
            default: break; // Markierungs-Modus ist der Startzustand.
        }
    }

    private bool AllMarkingsPlaced =>
        CurrentMarkings.OazRand.IsPlaced && CurrentMarkings.Sekundaer.IsPlaced
        && CurrentMarkings.HauptspiegelReflex.IsPlaced && CurrentMarkings.Marker.IsPlaced
        && CurrentMarkings.Linse.IsPlaced;

    private void RefreshWorkflowSteps()
    {
        // Während der Konstruktor-Initialisierung können abhängige Setter feuern,
        // bevor die Leiste existiert.
        if (WorkflowSteps is not { Count: 5 }) return;
        WorkflowSteps[0].IsDone = IsRunning;
        WorkflowSteps[1].IsDone = HasCalibration;
        WorkflowSteps[2].IsDone = AllMarkingsPlaced;
        WorkflowSteps[3].IsDone = (!IsPhase1Enabled || PhaseDone(1)) && PhaseDone(2) && PhaseDone(3);
        WorkflowSteps[4].IsDone = StarCollimationAchieved;

        // Standort-Semantik: genau EIN Chip aktiv — „wo der Nutzer gerade ist".
        // Ohne laufende Kamera gibt es nur einen sinnvollen nächsten Schritt
        // (Chip 1), unabhängig vom zuletzt gewählten Modus. Läuft die Kamera,
        // zeigt der Modus den aktiven Chip; ist die Kreis-Kalibrierung (Chip 2)
        // gerade aktiv, hat sie Vorrang vor den Modus-Chips 3-5.
        WorkflowSteps[0].IsActive = !IsRunning;
        WorkflowSteps[1].IsActive = CalibrationWizard.IsActive;
        WorkflowSteps[2].IsActive = IsRunning && IsMarkingsMode && !CalibrationWizard.IsActive;
        WorkflowSteps[3].IsActive = IsRunning && IsJustageMode && !CalibrationWizard.IsActive;
        WorkflowSteps[4].IsActive = IsRunning && IsStarTestMode && !CalibrationWizard.IsActive;
    }

    // Chip „2 Kalibrieren" klickbar: ohne laufende Kamera gibt es noch keinen
    // CameraKey — dann nur ein erklärender Hinweis statt eines No-Op-Klicks.
    // Läuft die Kamera, verhält sich der Klick wie der Kalibrieren-Button in der
    // Statusleiste (inkl. Überschreibschutz, siehe CalibrationWizardViewModel.Start).
    private void ActivateCalibrationChip()
    {
        if (!IsRunning)
        {
            StatusText = "Erst Kamera starten — dann kalibrieren.";
            return;
        }
        if (CalibrationWizard.StartCommand.CanExecute(null))
        {
            CalibrationWizard.StartCommand.Execute(null);
        }
    }

    public MainWindowViewModel()
    {
        WorkflowSteps = new ObservableCollection<WorkflowStepVm>
        {
            new("1 Kamera", "Kamera wählen und starten — ohne laufende Kamera wird nichts gespeichert. (Statusanzeige — nicht klickbar.)"),
            new("2 Kalibrieren", "Optional: Kreis-Kalibrierung für die Offset-Anzeige. Die geführte Justage braucht sie nicht. Klick startet die Kreis-Kalibrierung.", () => ActivateCalibrationChip()),
            new("3 Markieren", "Markierungen automatisch erkennen oder manuell setzen.", () => ActivateMarkingsMode()),
            new("4 Justage", "Geführte Justage in Phasen — Fangspiegel und Hauptspiegel ausrichten.", () => ActivateJustageMode()),
            new("5 Sterntest", "Feinjustage am defokussierten Stern (FITS oder Live-Kamera).", () => ActivateStarTestMode()),
        };

        Devices = new ObservableCollection<CameraDevice>();
        // Default-Auflösung VOR RefreshDevices setzen — sonst überschreibt diese
        // Zuweisung den persistierten Wert, den OnSelectedDeviceChanged während
        // RefreshDevices bereits aus dem CameraSettingsStore restauriert.
        _selectedResolution = Resolutions[0];
        RefreshDevices();

        CalibrationWizard = new CalibrationWizardViewModel(_calibrationStore)
        {
            OnSaved = result => CurrentCalibration = result,
        };
        CalibrationWizard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CalibrationWizardViewModel.IsActive))
            {
                OnPropertyChanged(nameof(ShowCalibrationStatusBar));
                DeleteCalibrationCommand.NotifyCanExecuteChanged();
                // Wird der Wizard anderweitig aktiv (z. B. direkt über den
                // Statusbar-Button), verliert der Entscheidungs-Banner seine
                // Gültigkeit — die Entscheidung ist dann bereits getroffen.
                if (CalibrationWizard.IsActive) ShowCalibrationDecision = false;
                // Chip 2 folgt dem Wizard-Zustand (aktiv/erledigt) und verdrängt
                // während der Kalibrierung die Modus-Chips 3-5.
                RefreshWorkflowSteps();
            }
        };

        OazRandVm = new MarkingViewModel(MarkingKind.OazRand, "OAZ-Rand", Brushes.White,
            "OAZ = Okularauszug: das Rohr am Teleskop, in das Okular oder Kamera gesteckt wird. "
            + "Sein erkannter Rand ist der Referenzkreis für die Zentrierung.");
        HauptspiegelReflexVm = new MarkingViewModel(MarkingKind.HauptspiegelReflex, "Hauptspiegel-Reflex", Brushes.LimeGreen,
            "Spiegelung des Hauptspiegels (der große Spiegel am Tubusboden). Seine Lage "
            + "zeigt, ob der Fangspiegel korrekt gekippt ist.");
        SekundaerVm = new MarkingViewModel(MarkingKind.Sekundaer, "Sekundärspiegel", Brushes.DodgerBlue,
            "Fangspiegel: der kleine, schräge Spiegel vorn im Tubus, der das Licht seitlich "
            + "zum Okularauszug umlenkt.");
        MarkerVm = new MarkingViewModel(MarkingKind.Marker, "Marker", Brushes.Red,
            "Zentrumsmarke auf dem Hauptspiegel (Ring mit Punkt). Sie ist das Ziel, in das "
            + "die Linse beim Hauptspiegel-Kippen gebracht wird.");
        LinseVm = new MarkingViewModel(MarkingKind.Linse, "Linse", Brushes.Magenta,
            "Dunkle Eigenlinse der OCAL-Kamera im Marker-Ring. Sie wandert beim Hauptspiegel-"
            + "Kippen und soll in den Marker-Punkt gebracht werden.");
        MarkingVms = new[] { OazRandVm, HauptspiegelReflexVm, SekundaerVm, MarkerVm, LinseVm };
        foreach (var vm in MarkingVms)
        {
            vm.PropertyChanged += OnMarkingVmPropertyChanged;
        }

        RefreshWorkflowSteps();
    }

    // Auflösungs-Mismatch-Status hängt von IsRunning ab (siehe
    // MarkingsResolutionMismatch/CalibrationResolutionMismatch) — beim Start
    // (CurrentMarkings/CurrentCalibration bereits geladen) und beim Stop neu
    // auswerten, damit Overlay-Suppression und Drehempfehlungen sofort greifen.
    partial void OnIsRunningChanged(bool value)
    {
        RefreshWorkflowSteps();
        RecomputeOverlayDisplay();
        NotifyJustageReadouts();
    }

    // Zweistufen-Bestätigung fürs Löschen der Kalibrierung: erster Klick „scharf
    // schalten" (Button zeigt „Wirklich löschen?"), zweiter Klick löscht. Nach 4 s
    // ohne zweiten Klick fällt der Button automatisch zurück — schützt vor
    // versehentlichem Klick ohne Dialog-Unterbrechung.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteCalibrationButtonText))]
    private bool _isCalibrationDeleteArmed;

    public string DeleteCalibrationButtonText => IsCalibrationDeleteArmed ? "Wirklich löschen?" : "Löschen";

    private DispatcherTimer? _calibrationDeleteDisarmTimer;

    [RelayCommand(CanExecute = nameof(CanDeleteCalibration))]
    private void DeleteCalibration()
    {
        if (!IsCalibrationDeleteArmed)
        {
            IsCalibrationDeleteArmed = true;
            StatusText = "Kalibrierung löschen? Zum Bestätigen erneut klicken.";
            if (_calibrationDeleteDisarmTimer is null)
            {
                _calibrationDeleteDisarmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                _calibrationDeleteDisarmTimer.Tick += (_, _) =>
                {
                    _calibrationDeleteDisarmTimer!.Stop();
                    IsCalibrationDeleteArmed = false;
                };
            }
            _calibrationDeleteDisarmTimer.Stop();
            _calibrationDeleteDisarmTimer.Start();
            return;
        }
        _calibrationDeleteDisarmTimer?.Stop();
        IsCalibrationDeleteArmed = false;

        var key = CalibrationWizard.CameraKey;
        if (string.IsNullOrEmpty(key)) return;

        var path = _calibrationStore.GetPathFor(key);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            CurrentCalibration = null;
            StatusText = "Kalibrierung gelöscht.";
        }
        catch (Exception ex)
        {
            StatusText = $"Fehler beim Löschen der Kalibrierung: {ex.Message}";
        }
    }

    private bool CanDeleteCalibration()
        => HasCalibration && IsRunning && !CalibrationWizard.IsActive;

    // Geräteliste neu einlesen, z.B. nach dem Anschließen einer Kamera. Die
    // aktuelle Auswahl bleibt erhalten, wenn das Gerät weiterhin vorhanden ist.
    [RelayCommand]
    private void RefreshDevices()
    {
        var previousIndex = SelectedDevice?.Index;
        var found = CameraEnumerator.List();

        Devices.Clear();
        foreach (var device in found)
        {
            Devices.Add(device);
        }

        SelectedDevice = Devices.FirstOrDefault(d => d.Index == previousIndex)
            ?? Devices.FirstOrDefault();

        StatusText = Devices.Count == 0
            ? "Keine Kamera gefunden — Kamera anschließen und ⟳ drücken."
            : $"{Devices.Count} Kamera(s) erkannt.";
    }

    /// <summary>
    /// Nennt die tatsächlich gelieferte Aufnahme-Auflösung, falls sie von der
    /// angeforderten abweicht. Nötig, weil UVC-Kameras die nächstgelegene unterstützte
    /// Größe wählen — unter DirectShow liefert z.B. die OCAL auf die Anforderung
    /// 2560×1472 tatsächlich 2592×1944. Ohne diesen Hinweis behauptet die Auswahlliste
    /// weiterhin die angeforderte Größe. Stimmen die Werte überein (oder meldet die
    /// Quelle keine), bleibt der Text unverändert.
    /// </summary>
    private static string ActualResolutionHint(ICameraSource source, CaptureResolution requested)
    {
        if (source is not OpenCvVideoCaptureSource uvc
            || uvc.ActualWidth <= 0
            || uvc.ActualHeight <= 0
            || (uvc.ActualWidth == requested.Width && uvc.ActualHeight == requested.Height))
        {
            return string.Empty;
        }

        return $" — Kamera liefert {uvc.ActualWidth}×{uvc.ActualHeight}"
               + $" (angefordert {requested.Width}×{requested.Height})";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (IsRunning || SelectedDevice is null)
        {
            return;
        }

        var device = SelectedDevice;
        try
        {
            // Auflösung: die aktuelle (ggf. manuell gewählte) ComboBox-Auswahl gewinnt.
            // Vorbelegt wird sie bei der Geräteauswahl (OnSelectedDeviceChanged).
            var res = SelectedResolution;
            var source = _cameraSources.CreateUvc(device.Index, res.Width, res.Height);
            source.Start();
            _source = source;
            LastStartedCameraName = device.Name;
            ApplyExposureCapabilityFromSource();
            ApplyFocusCapabilityFromSource();
            var storageKey = device.StorageKey;
            CalibrationWizard.CameraKey = storageKey;
            CurrentCalibration = _calibrationStore.Load(storageKey);
            CurrentMarkings = _markingStore.Load(storageKey);
            // Frisch aus dem Store geladen (nicht neu gemessen) → Frische-Set leeren.
            ResetFreshlyMeasuredPhases();
            CurrentScrews = _screwStore.Load(storageKey);
            SyncMarkingVmsFromState();
            ApplyPersistedCameraSettings(storageKey);
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => CaptureLoop(_cts.Token));
            IsRunning = true;
            var kalibriert = CurrentCalibration is not null ? " (kalibriert)" : string.Empty;
            StatusText = $"Läuft: {device.Display}{ActualResolutionHint(source, res)}{kalibriert}";
            // Kalibrierung wird weiterhin still geladen (der richtige Default) —
            // der Banner macht die Entscheidung "verwenden vs. neu bestimmen"
            // nur sichtbar, statt sie stillschweigend zu treffen.
            ShowCalibrationDecision = CurrentCalibration is not null;
            // Der Schlüssel-Text hängt am frisch gesetzten CameraKey — ohne Notify
            // bliebe die „Zugeordnet über:"-Zeile auf dem Stand vor dem Start (leer).
            OnPropertyChanged(nameof(CalibrationDecisionStorageKeyText));
        }
        catch (Exception ex)
        {
            _source?.Dispose();
            _source = null;
            IsExposureControlAvailable = false;
            IsFocusControlAvailable = false;
            CurrentCalibration = null;
            CalibrationWizard.CameraKey = null;
            ShowCalibrationDecision = false;
            StatusText = $"Fehler beim Start ({device.Display}): {ex.Message}";
        }
    }

    private bool CanStart() => !IsRunning && SelectedDevice is not null;

    // Geräteauswahl belegt die Auflösungs-ComboBox mit dem persistierten Wert vor.
    // Eine danach manuell gewählte Auflösung bleibt erhalten (Start() überschreibt nicht).
    partial void OnSelectedDeviceChanged(CameraDevice? value)
    {
        if (IsRunning || value is null) return;
        var persisted = _settingsStore.Load(value.StorageKey);
        if (persisted is { CaptureWidth: > 0, CaptureHeight: > 0 })
        {
            SelectedResolution = Resolutions.FirstOrDefault(
                r => r.Width == persisted.CaptureWidth && r.Height == persisted.CaptureHeight)
                ?? SelectedResolution;
        }
    }

    // Zuletzt gestartete Kamera (für Auto-Start beim nächsten Programmstart).
    public string? LastStartedCameraName { get; private set; }

    // Beim Programmstart: gemerkte Kamera auswählen und automatisch starten,
    // sofern sie noch vorhanden ist.
    public void AutoStartCamera(string? cameraName)
    {
        if (string.IsNullOrEmpty(cameraName) || IsRunning) return;
        var device = Devices.FirstOrDefault(d => d.Name == cameraName);
        if (device is null) return;
        SelectedDevice = device;
        if (StartCommand.CanExecute(null)) StartCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        // Aktuelle Einstellungen sichern, bevor die Source verschwindet.
        PersistCameraSettings();

        _autofocusCts?.Cancel();
        _cts?.Cancel();
        if (_worker is not null)
        {
            try { await _worker; } catch { /* swallow */ }
        }
        _worker = null;
        _cts?.Dispose();
        _cts = null;
        _source?.Dispose();
        _source = null;
        lock (_liveSnapshotLock)
        {
            _liveSnapshot?.Dispose();
            _liveSnapshot = null;
        }
        _sessionFocusByKind.Clear();
        ResetFreshlyMeasuredPhases();
        IsExposureControlAvailable = false;
        IsFocusControlAvailable = false;
        IsRunning = false;
        CurrentCalibration = null;
        CalibrationWizard.CameraKey = null;
        ShowCalibrationDecision = false;
        StatusText = "Gestoppt.";
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanSnapshot))]
    private async Task SnapshotAsync()
    {
        var source = _source;
        if (source is null)
        {
            return;
        }

        try
        {
            var directory = GetSnapshotDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"snapshot-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");

            await Task.Run(() =>
            {
                using var frame = source.GrabFrame();
                if (frame is null)
                {
                    throw new InvalidOperationException("Kein Frame verfügbar.");
                }
                Cv2.ImWrite(path, frame);
            });

            StatusText = $"Snapshot: {path}";
        }
        catch (Exception ex)
        {
            StatusText = $"Snapshot fehlgeschlagen: {ex.Message}";
        }
    }

    private bool CanSnapshot() => IsRunning;

    private static string GetSnapshotDirectory()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var basePath = string.IsNullOrEmpty(pictures)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : pictures;
        return Path.Combine(basePath, "FreeCol");
    }

    public async Task ShutdownAsync()
    {
        // Sterntest-Ressourcen freigeben — auch ohne laufende OCAL-Kamera. Die
        // Alpaca-Verbindung sauber (protokollkonform, referenzgezählt) trennen und
        // dabei ABWARTEN, damit der Disconnect den Server vor Prozess-Ende erreicht.
        StopStarLoop();
        StopStarWatch();
        var cam = _alpaca;
        _alpaca = null;
        IsAlpacaConnected = false;
        if (cam is not null) await Task.Run(() => { try { cam.Dispose(); } catch { /* ignore */ } });
        var asi = _asi;
        _asi = null;
        IsAsiConnected = false;
        if (asi is not null) await Task.Run(() => { try { asi.Dispose(); } catch { /* ignore */ } });
        var foc = _focuser;
        _focuser = null;
        IsFocuserConnected = false;
        if (foc is not null) await Task.Run(() => { try { foc.Dispose(); } catch { /* ignore */ } });
        if (IsRunning) await StopAsync();
    }

    private void ApplyPersistedCameraSettings(string cameraKey)
    {
        var settings = _settingsStore.Load(cameraKey);
        if (settings is null)
        {
            return;
        }

        IsAutoExposure = settings.IsAutoExposure;
        if (IsExposureControlAvailable)
        {
            ExposureValue = Math.Clamp(settings.Exposure, ExposureMin, ExposureMax);
        }

        IsAutoFocus = settings.IsAutoFocus;
        if (IsFocusControlAvailable)
        {
            FocusValue = Math.Clamp(settings.Focus, FocusMin, FocusMax);
        }

        // Werte zusätzlich direkt an die Source schreiben — wenn das VM-Property
        // sich nicht geändert hat (Wert == Default), würde die OnXyzChanged-Partial
        // sonst nichts pushen.
        if (_source is IExposureControl ec && !IsAutoExposure)
        {
            ec.Exposure = ExposureValue;
        }
        if (_source is IFocusControl fc)
        {
            fc.AutoFocus = IsAutoFocus;
            if (!IsAutoFocus) fc.Focus = FocusValue;
        }
    }

    private void PersistCameraSettings()
    {
        var key = CalibrationWizard.CameraKey;
        if (string.IsNullOrEmpty(key)) return;

        var settings = new CameraSettings(
            IsAutoExposure: IsAutoExposure,
            Exposure: ExposureValue,
            IsAutoFocus: IsAutoFocus,
            Focus: FocusValue,
            CaptureWidth: SelectedResolution.Width,
            CaptureHeight: SelectedResolution.Height);
        try
        {
            _settingsStore.Save(key, settings);
        }
        catch
        {
            // Persistierungsfehler sind nicht kritisch; nicht crashen beim Beenden.
        }
    }

    private bool _suppressMarkingVmSync;

    private void SyncMarkingVmsFromState()
    {
        _suppressMarkingVmSync = true;
        try
        {
            OazRandVm.ApplyFrom(CurrentMarkings.OazRand);
            HauptspiegelReflexVm.ApplyFrom(CurrentMarkings.HauptspiegelReflex);
            SekundaerVm.ApplyFrom(CurrentMarkings.Sekundaer);
            MarkerVm.ApplyFrom(CurrentMarkings.Marker);
            LinseVm.ApplyFrom(CurrentMarkings.Linse);
        }
        finally
        {
            _suppressMarkingVmSync = false;
        }
    }

    private void OnMarkingVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not MarkingViewModel vm) return;

        if (e.PropertyName == nameof(MarkingViewModel.IsRenderVisible))
        {
            // Legende folgt den tatsächlich gezeichneten Markierungen.
            OnPropertyChanged(nameof(ShowOverlayLegend));
        }

        if (e.PropertyName == nameof(MarkingViewModel.IsSelectedForEdit))
        {
            if (vm.IsSelectedForEdit)
            {
                foreach (var other in MarkingVms)
                {
                    if (!ReferenceEquals(other, vm)) other.IsSelectedForEdit = false;
                }
                RestoreFocusFor(vm.Kind);
            }
            RecomputeOverlayDisplay();
            NotifyFrameGestureHint();
            return;
        }

        if (_suppressMarkingVmSync) return;

        var current = CurrentMarkings[vm.Kind];
        var updated = current with
        {
            IsVisible = vm.IsVisible,
            IsAutoEnabled = vm.IsAutoEnabled,
        };
        CurrentMarkings = CurrentMarkings.With(updated);
        PersistMarkings();
    }

    private void RestoreFocusFor(MarkingKind kind)
    {
        if (!IsFocusControlAvailable) return;
        var m = CurrentMarkings[kind];
        if (!m.IsPlaced) return;

        IsAutoFocus = false;
        // Beim bloßen Selektieren nur den zuletzt gelernten Wert wiederherstellen
        // (kein Sweep). Der ROI-Autofokus läuft, wenn der Fokus-Anker per Klick/
        // Drag gesetzt wird (OnFramePointerCommit) — also genau am gewählten Punkt.
        if (m.AutoFocusTarget is double target)
        {
            FocusValue = Math.Clamp(target, FocusMin, FocusMax);
        }
    }

    // Eigener Autofokus: der Kamera-Autofokus bewertet das ganze Bild und weiß
    // nicht, was uns interessiert. Wir messen die Schärfe (Laplacian-Varianz) NUR
    // im ROI um die Markierung und fahren den Fokus an den Schärfe-Peak. Ein
    // laufender Lauf wird bei neuer Selektion abgebrochen.
    private CancellationTokenSource? _autofocusCts;

    // Scharfgestellte Fokuswerte dieser Session, pro Markierung. Der Fokuswert ist
    // monoton mit der Bildebenen-Tiefe (nähere Ebene ⇒ höherer Wert), und die
    // Markierungen liegen in fester Tiefenordnung (OAZ-Rohr nah → Hauptspiegel
    // fern). Ein bereits scharfgestellter Nachbar grenzt damit den Sweep-Bereich
    // des nächsten Features ein — schneller und gegen Fehlebenen-Lock robuster.
    private readonly Dictionary<MarkingKind, double> _sessionFocusByKind = new();

    // Tiefenrang im Strahlengang (0 = am nächsten zur Kamera) — siehe JustagePhaseModel.
    private static int FocusDepthRank(MarkingKind kind) => JustagePhaseModel.FocusDepthRank(kind);

    /// <summary>Anteil des Fokusbereichs, um den die Nachbar-Schranken gelockert
    /// werden — fängt Mess-Rauschen der Nachbarwerte ab, ohne den Range-Gewinn zu
    /// verlieren.</summary>
    private const double FocusBracketMarginFraction = 0.15;

    /// <summary>
    /// Engt den vollen Sweep-Bereich anhand bereits scharfgestellter Nachbarn ein:
    /// nähere (kleinerer Rang) liefern eine Obergrenze, fernere eine Untergrenze.
    /// Liefert (lo, hi). Fällt bei widersprüchlichen/zu engen Schranken auf den
    /// vollen Bereich zurück — eine fehl-gelernte Nachbar-Ebene darf den echten
    /// Fokus nie aus dem Sweep ausschließen.
    /// </summary>
    private (double Lo, double Hi) FocusBracket(MarkingKind kind)
    {
        var ri = FocusDepthRank(kind);
        double lo = FocusMin, hi = FocusMax;
        foreach (var (k, f) in _sessionFocusByKind)
        {
            if (k == kind) continue;
            var rk = FocusDepthRank(k);
            if (rk < ri) hi = Math.Min(hi, f);       // näher ⇒ unser Wert ≤ dessen
            else if (rk > ri) lo = Math.Max(lo, f);  // ferner ⇒ unser Wert ≥ dessen
        }
        var margin = (FocusMax - FocusMin) * FocusBracketMarginFraction;
        lo = Math.Max(FocusMin, lo - margin);
        hi = Math.Min(FocusMax, hi + margin);
        // Mindest-Spanne: bei zu engem/invertiertem Fenster den vollen Bereich
        // nehmen (Nachbarwert vermutlich unzuverlässig).
        if (hi - lo < (FocusMax - FocusMin) * 0.2) return (FocusMin, FocusMax);
        return (lo, hi);
    }

    private async Task RunRoiAutofocusAsync(MarkingKind kind, double? hint)
    {
        var source = _source;
        if (source is not IFocusControl fc || !IsFocusControlAvailable) return;
        var marking = CurrentMarkings[kind];
        if (!marking.IsPlaced) return;

        _autofocusCts?.Cancel();
        var cts = new CancellationTokenSource();
        _autofocusCts = cts;
        var ct = cts.Token;

        try
        {
            IsAutoFocus = false;
            var roi = RoiForMarking(marking);
            var name = MarkingVms.FirstOrDefault(v => v.Kind == kind)?.Name ?? "Markierung";
            StatusText = $"Autofokus auf {name} …";

            var best = await FocusSweepAsync(source, fc, roi, hint, kind, ct);
            if (best is double f && !ct.IsCancellationRequested)
            {
                FocusValue = Math.Clamp(f, FocusMin, FocusMax);
                _sessionFocusByKind[kind] = FocusValue;
                UpdateMarking(kind, CurrentMarkings[kind] with { AutoFocusTarget = FocusValue }, persist: true);
                StatusText = $"Autofokus {name}: scharf bei {FocusValue:0}.";
            }
        }
        catch (OperationCanceledException)
        {
            // Durch neue Selektion abgelöst — kein Fehler.
        }
        catch (Exception ex)
        {
            StatusText = $"Autofokus fehlgeschlagen: {ex.Message}";
        }
    }

    // Box-ROI am Fokus-Anker: bei Mausklick exakt am Klick, sonst auf der
    // Markierungs-Kante (Kreise) bzw. in der Mitte (Marker-Punkt).
    private static FocusRoi RoiForMarking(Marking m)
    {
        var radius = Math.Max(m.RadiusX, m.RadiusY);
        double px, py;
        if (m.FocusPointX is double fx && m.FocusPointY is double fy)
        {
            px = fx; py = fy;
        }
        else if (m.Kind == MarkingKind.Marker || m.Kind == MarkingKind.Linse)
        {
            px = m.CenterX; py = m.CenterY;
        }
        else
        {
            px = m.CenterX; py = m.CenterY - radius;
        }
        return new FocusRoi(px, py, Math.Clamp(radius * 0.25, 24, 80));
    }

    // Mit Hint (gelernter Wert) nur Feinsuche um den Hint — schnell beim
    // Selektieren. Ohne Hint erst grob über den ganzen Fokusbereich.
    private async Task<double?> FocusSweepAsync(
        ICameraSource source, IFocusControl fc, FocusRoi roi, double? hint, MarkingKind kind, CancellationToken ct)
    {
        if (hint is double h)
        {
            // Bekannter Startwert → ein Feindurchlauf um den Hint genügt.
            var span = (FocusMax - FocusMin) / 5.0;
            return await SweepRangeAsync(source, fc, roi,
                Math.Max(FocusMin, h - span), Math.Min(FocusMax, h + span), 9, ct) ?? h;
        }

        // Grobsuche nur über das von den Nachbarn erlaubte Tiefenfenster statt
        // über den ganzen Fokushub — schneller und kein Lock auf eine Fehlebene.
        var (lo, hi) = FocusBracket(kind);
        var coarse = await SweepRangeAsync(source, fc, roi, lo, hi, 7, ct);
        if (coarse is not double c) return null;
        var fine = (FocusMax - FocusMin) / 7.0;
        return await SweepRangeAsync(source, fc, roi,
            Math.Max(FocusMin, c - fine), Math.Min(FocusMax, c + fine), 5, ct) ?? c;
    }

    // Fokus-Einschwingen: kurz anstoßen, dann eine feste kleine Frame-Anzahl
    // verwerfen. Tunable (langsame Kamera → mehr Drain, schnellere → weniger).
    private const int FocusMotorKickMs = 120;
    private const int FocusDrainFrames = 2;

    private async Task<double?> SweepRangeAsync(
        ICameraSource source, IFocusControl fc, FocusRoi roi,
        double lo, double hi, int steps, CancellationToken ct)
    {
        double bestF = lo, bestScore = -1;
        for (var i = 0; i < steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = steps <= 1 ? lo : lo + (hi - lo) * i / (steps - 1);
            // FocusValue mitführen (statt nur fc.Focus): der OnFocusValueChanged-
            // Handler fährt den Motor, und der Slider zeigt den Sweep sichtbar an.
            FocusValue = Math.Clamp(f, FocusMin, FocusMax);
            await Task.Delay(FocusMotorKickMs, ct);
            var score = await MeasureSharpnessAsync(source, roi, ct);
            if (score > bestScore)
            {
                bestScore = score;
                bestF = f;
            }
        }
        return bestScore < 0 ? null : bestF;
    }

    // Eine feste, kleine Anzahl frischer Frames nach dem Fokuswechsel verwerfen
    // (Motor + Pipeline einschwingen), dann EIN Frame messen. Die langsame
    // OCAL (~1–2 fps) macht jeden zusätzlichen Frame teuer; das adaptive Warten
    // vorher las bei Live-Rauschen immer die Obergrenze und dauerte ~10 s/Schritt.
    private async Task<double> MeasureSharpnessAsync(
        ICameraSource source, FocusRoi roi, CancellationToken ct)
    {
        for (var d = 0; d < FocusDrainFrames; d++)
        {
            (await GrabFreshFrameAsync(source, ct))?.Dispose();
        }
        using var frame = await GrabFreshFrameAsync(source, ct);
        if (frame is null) return 0;
        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        return FocusMeasure.Variance(gray, roi);
    }

    private void PersistMarkings()
    {
        var key = CalibrationWizard.CameraKey;
        if (string.IsNullOrEmpty(key)) return;
        // Mit der tatsächlich gelieferten Capture-Größe stempeln (nicht mit der
        // angeforderten SelectedResolution — siehe CaptureFrameWidth/-Height)
        // — Grundlage der Auflösungs-Mismatch-Erkennung (siehe
        // MarkingsResolutionMismatch). Ein Neu-Setzen (Automarkierung/manuelles
        // Platzieren) löscht damit eine bestehende Mismatch-Warnung automatisch,
        // weil die Markierungen dann wieder zur laufenden Auflösung passen.
        var stamped = CurrentMarkings with
        {
            FrameWidth = CaptureFrameWidth,
            FrameHeight = CaptureFrameHeight,
        };
        if (stamped != CurrentMarkings)
        {
            CurrentMarkings = stamped;
        }
        try
        {
            _markingStore.Save(key, CurrentMarkings);
        }
        catch
        {
            // Persistierungsfehler sind nicht kritisch; nicht crashen beim Beenden.
        }
    }

    private const int AutoRunFrameDrainCount = 3;

    // Live-Snapshot, vom Capture-Loop bei jedem Frame frisch geklont. Auto-
    // Detection nutzt diesen Snapshot statt selbst zu grabben — sonst läuft
    // Detection auf einem älteren Buffer-Frame als die Live-Anzeige zeigt.
    private readonly object _liveSnapshotLock = new();
    private Mat? _liveSnapshot;
    private long _liveSnapshotSequence;

    [RelayCommand]
    private async Task RunAutoMarkingAsync(CancellationToken ct)
    {
        EnterBusy();
        try { await RunAutoMarkingCoreAsync(ct); }
        finally { ExitBusy(); }
    }

    private async Task RunAutoMarkingCoreAsync(CancellationToken ct)
    {
        var source = _source;
        if (source is null)
        {
            StatusText = "Automarkierung: keine Kamera aktiv.";
            return;
        }

        try
        {
            // Fokus-Schranken aus einem früheren Lauf verwerfen — der neue Lauf
            // lernt die Tiefenwerte frisch (Teleskop/Fokus kann sich geändert haben).
            _sessionFocusByKind.Clear();

            // Alte Markierungen löschen — IsAutoEnabled und IsVisible bleiben,
            // AutoFocusTarget wird zurückgesetzt, damit der nachfolgende Lauf den
            // jetzt eingestellten Fokus pro Ziel neu lernt (alte gespeicherte Werte
            // aus früheren Sessions verfälschen sonst den Justage-Restore-Pfad).
            foreach (var vm in MarkingVms)
            {
                var existing = CurrentMarkings[vm.Kind];
                CurrentMarkings = CurrentMarkings.With(Marking.Default(vm.Kind) with
                {
                    IsAutoEnabled = existing.IsAutoEnabled,
                    IsVisible = existing.IsVisible,
                });
            }

            // Geometrie ist OAZ-Rand → Sekundär → Hauptspiegel-Reflex → Marker, aber
            // der Hauptspiegel-Reflex hat eine viel sauberere Kante als die
            // Sekundärsilhouette. Wir suchen erst ihn und nutzen ihn dann als
            // inneren Hint für die Sekundär-Detection.
            // Durchlauf 1: stabile Erkennung beim eingestellten Fokus (kein
            // Fokusfahren — sonst zerschießen Fokuswechsel die Folge-Detections).
            await RunAutoStepAsync(source, MarkingKind.OazRand, ct);
            await RunAutoStepAsync(source, MarkingKind.HauptspiegelReflex, ct);
            await RunAutoStepAsync(source, MarkingKind.Sekundaer, ct);
            await RunAutoStepAsync(source, MarkingKind.Marker, ct);
            // Linse (Phase-3-IST) sitzt im Marker-Ring; braucht den HSR als Hint.
            await RunAutoStepAsync(source, MarkingKind.Linse, ct);

            // Durchlauf 2: pro Ziel scharfstellen + nachziehen. Bei hoher Auflösung
            // liegen OAZ-Rand (nah) und Marker (fern) auf verschiedenen Fokus-
            // ebenen — der Erst-Durchlauf war zwangsläufig auf einer Ebene scharf.
            // Voller Sweep (Hint=null), weil gespeicherte AutoFocusTarget-Werte
            // unzuverlässig sind und die Feinsuche an einem falschen Hint hängen
            // bleibt.
            if (IsFocusControlAvailable)
            {
                await RefocusAndRefineAsync(source, MarkingKind.OazRand, ct);
                await RefocusAndRefineAsync(source, MarkingKind.HauptspiegelReflex, ct);
                await RefocusAndRefineAsync(source, MarkingKind.Sekundaer, ct);
                await RefocusAndRefineAsync(source, MarkingKind.Marker, ct);
                await RefocusAndRefineAsync(source, MarkingKind.Linse, ct);
            }

            StatusText = AutoMarkingResultText();
            PersistMarkings();
            // Alle drei Mess-Phasen beruhen jetzt auf frisch erkannten Markierungen.
            MarkPhasesFreshlyMeasured(1, 2, 3);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Automarkierung abgebrochen.";
        }
        catch (Exception ex)
        {
            StatusText = $"Automarkierung fehlgeschlagen: {ex.Message}";
        }
    }

    // Abschluss-Meldung mit Ergebnis (wie viele der 5 Markierungen erkannt
    // wurden) UND dem nächsten Schritt, statt einer reinen "fertig"-Meldung.
    private string AutoMarkingResultText()
    {
        var placed = MarkingVms.Count(vm => CurrentMarkings[vm.Kind].IsPlaced);
        return placed == MarkingVms.Count
            ? "Automarkierung fertig — alle 5 erkannt. Weiter mit ‚4 Justage'."
            : $"Automarkierung fertig — {placed}/5 erkannt. Fehlende manuell setzen, dann ‚4 Justage'.";
    }

    private async Task RunAutoStepAsync(ICameraSource source, MarkingKind kind, CancellationToken ct)
    {
        var vm = MarkingVms.First(v => v.Kind == kind);
        var current = CurrentMarkings[kind];

        if (!current.IsAutoEnabled)
        {
            StatusText = $"Automarkierung: {vm.Name} übersprungen.";
            return;
        }

        // Erkennung beim aktuell (manuell) eingestellten Fokus — KEIN Fokusfahren
        // während der Automarkierung, sonst zerschießt der Fokuswechsel die
        // Folge-Detections. Den scharfen Wert pro Markierung bestimmt der eigene
        // ROI-Autofokus beim Selektieren.
        StatusText = $"Auto: {vm.Name} …";
        Marking? detected = kind == MarkingKind.OazRand
            ? await DetectOazRandAveragedAsync(source, current, ct)
            : await DetectOnceAsync(source, kind, current, ct);
        if (detected is null)
        {
            StatusText = $"Automarkierung: {vm.Name} nicht erkannt — Belichtung/Schärfe anpassen oder manuell markieren.";
            return;
        }

        detected = detected with { AutoFocusTarget = FocusValue };
        UpdateMarking(kind, detected, persist: false);
    }

    // Zweiter Durchlauf der Automarkierung: pro Markierung am Kanten-ROI
    // scharfstellen und danach exakt nachziehen — jetzt auf dem scharfen Bild.
    // Voller Sweep (null Hint), damit ein einmal falsch gelernter Fokus den
    // Lauf nicht in der gleichen Fehlebene festnagelt.
    private async Task RefocusAndRefineAsync(ICameraSource source, MarkingKind kind, CancellationToken ct)
    {
        var m = CurrentMarkings[kind];
        if (!m.IsPlaced || !m.IsAutoEnabled) return;
        var vm = MarkingVms.First(v => v.Kind == kind);

        StatusText = $"Scharfstellen: {vm.Name} …";
        await RunRoiAutofocusAsync(kind, null);
        // Fokusmotor nach dem Sweep einschwingen lassen, bevor der Detektor misst.
        try { await Task.Delay(300, ct); } catch (OperationCanceledException) { return; }

        StatusText = $"Nachziehen: {vm.Name} …";
        var current = CurrentMarkings[kind];
        var refined = kind == MarkingKind.OazRand
            ? await DetectOazRandAveragedAsync(source, current, ct)
            : await DetectOnceAsync(source, kind, current, ct);
        if (refined is null)
        {
            StatusText = $"{vm.Name}: scharfgestellt, aber nicht nachgezogen.";
            return;
        }
        if (!IsPlausibleRefinement(current, refined))
        {
            StatusText = $"{vm.Name}: Nachzieh-Detection unplausibel, verworfen.";
            return;
        }
        UpdateMarking(kind, refined with { AutoFocusTarget = FocusValue }, persist: false);
    }

    private async Task<Marking?> DetectOazRandAveragedAsync(ICameraSource source, Marking baseMarking, CancellationToken ct)
    {
        const int FrameCount = 3;
        double sumX = 0, sumY = 0, sumR = 0;
        int hits = 0;
        for (var i = 0; i < FrameCount; i++)
        {
            var frame = await GrabFreshFrameAsync(source, ct);
            if (frame is null) continue;
            try
            {
                using var gray = new Mat();
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                var result = _detectors.OazRand.Detect(gray);
                if (result is null) continue;
                sumX += result.Center.X;
                sumY += result.Center.Y;
                sumR += result.Radius;
                hits++;
            }
            finally
            {
                frame.Dispose();
            }
        }
        if (hits == 0) return null;
        var avgX = sumX / hits;
        var avgY = sumY / hits;
        var avgR = sumR / hits;
        return baseMarking with
        {
            IsPlaced = true,
            CenterX = avgX,
            CenterY = avgY,
            RadiusX = avgR,
            RadiusY = avgR,
            AngleDeg = 0,
        };
    }

    private async Task<Marking?> DetectOnceAsync(ICameraSource source, MarkingKind kind, Marking baseMarking, CancellationToken ct)
    {
        var frame = await GrabFreshFrameAsync(source, ct);
        if (frame is null) return null;
        try
        {
            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            return kind switch
            {
                MarkingKind.OazRand => DetectOazRandMarking(gray, baseMarking),
                MarkingKind.HauptspiegelReflex => DetectHauptspiegelMarking(gray, baseMarking),
                MarkingKind.Sekundaer => DetectSekundaerMarking(gray, baseMarking),
                MarkingKind.Marker => DetectMarkerMarking(gray, baseMarking),
                MarkingKind.Linse => DetectLinseMarking(gray, baseMarking),
                _ => null,
            };
        }
        finally
        {
            frame.Dispose();
        }
    }

    private async Task<Mat?> GrabFreshFrameAsync(ICameraSource source, CancellationToken ct)
    {
        // Auf einen frischen Snapshot warten, der NACH dem aktuellen Sequenz-
        // stand gemacht wurde — damit ist das verwendete Frame jünger als alles,
        // was vor dem Aufruf bereits gepuffert war.
        long baseline;
        lock (_liveSnapshotLock) { baseline = _liveSnapshotSequence; }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            lock (_liveSnapshotLock)
            {
                if (_liveSnapshot is not null && _liveSnapshotSequence > baseline)
                {
                    return _liveSnapshot.Clone();
                }
            }
            await Task.Delay(50, ct);
        }

        // Fallback: kein Snapshot rechtzeitig — direkt grabben.
        return await Task.Run(() =>
        {
            Mat? last = null;
            for (var i = 0; i < AutoRunFrameDrainCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                last?.Dispose();
                last = source.GrabFrame();
            }
            return last;
        }, ct);
    }

    private Marking? DetectOazRandMarking(Mat gray, Marking current)
    {
        var result = _detectors.OazRand.Detect(gray);
        if (result is null) return null;
        return current with
        {
            IsPlaced = true,
            CenterX = result.Center.X,
            CenterY = result.Center.Y,
            RadiusX = result.Radius,
            RadiusY = result.Radius,
            AngleDeg = 0,
        };
    }

    private Marking? DetectHauptspiegelMarking(Mat gray, Marking current)
    {
        var oazRand = CurrentMarkings.OazRand;
        OazRandResult? hint = oazRand.IsPlaced
            ? new OazRandResult(new Point2f((float)oazRand.CenterX, (float)oazRand.CenterY), oazRand.RadiusX)
            : null;
        var result = _detectors.HauptspiegelReflex.Detect(gray, hint);
        if (result is null) return null;
        return current with
        {
            IsPlaced = true,
            CenterX = result.Center.X,
            CenterY = result.Center.Y,
            RadiusX = result.Radius,
            RadiusY = result.Radius,
            AngleDeg = 0,
        };
    }

    private Marking? DetectSekundaerMarking(Mat gray, Marking current)
    {
        var oazRand = CurrentMarkings.OazRand;
        var hsr = CurrentMarkings.HauptspiegelReflex;
        OazRandResult? outerHint = oazRand.IsPlaced
            ? new OazRandResult(new Point2f((float)oazRand.CenterX, (float)oazRand.CenterY), oazRand.RadiusX)
            : null;
        HauptspiegelReflexResult? innerHint = hsr.IsPlaced
            ? new HauptspiegelReflexResult(new Point2f((float)hsr.CenterX, (float)hsr.CenterY), hsr.RadiusX)
            : null;
        var result = _detectors.Sekundaer.Detect(gray, outerHint, innerHint);
        if (result is null) return null;
        return current with
        {
            IsPlaced = true,
            CenterX = result.Center.X,
            CenterY = result.Center.Y,
            RadiusX = result.RadiusX,
            RadiusY = result.RadiusY,
            AngleDeg = result.AngleDeg,
        };
    }

    /// <summary>
    /// Findet die dunkle Fangspiegel-Reflexionsscheibe aus dem platzierten HSR.
    /// Marker und Linse liegen immer darin — der Rand dient als äußere
    /// Plausibilitätsgrenze. Liefert null, wenn HSR fehlt oder die Scheibe nicht
    /// zuverlässig gefunden wird (dann greift der Gate nicht, alte Logik bleibt).
    /// </summary>
    private FangspiegelReflexResult? DetectFangspiegelReflex(Mat gray)
    {
        var hsr = CurrentMarkings.HauptspiegelReflex;
        if (!hsr.IsPlaced) return null;
        var hint = new HauptspiegelReflexResult(
            new Point2f((float)hsr.CenterX, (float)hsr.CenterY), hsr.RadiusX);
        return _detectors.FangspiegelReflex.Detect(gray, hint);
    }

    /// <summary>
    /// True, wenn (<paramref name="x"/>,<paramref name="y"/>) innerhalb der FS-
    /// Reflexion liegt (mit 20% Radius-Reserve). Ohne FS-Treffer immer true —
    /// der Gate darf gute Treffer nie verwerfen, nur grobe Ausreißer außerhalb
    /// der Scheibe.
    /// </summary>
    private static bool WithinFangspiegelReflex(FangspiegelReflexResult? fs, double x, double y)
    {
        if (fs is null) return true;
        var dx = x - fs.Center.X;
        var dy = y - fs.Center.Y;
        return Math.Sqrt(dx * dx + dy * dy) <= fs.Radius * 1.2;
    }

    private Marking? DetectMarkerMarking(Mat gray, Marking current)
    {
        var hsr = CurrentMarkings.HauptspiegelReflex;
        if (!hsr.IsPlaced) return null;
        // Hinweis = bisherige Marker-Position, sobald gesetzt → der Detektor
        // verfolgt den Marker, wenn er beim Hauptspiegel-Kippen wandert. Erst-
        // Erkennung (Marker noch nicht platziert) nutzt das HSR-Zentrum.
        // Wichtig in Phase 3: Das HSR ist dort veraltet (wird nicht mehr neu
        // erfasst); würde man weiter danach suchen, zöge der „nächste Kreis zum
        // Hinweis" den Marker bei jedem Aktualisieren Richtung stale HSR-Zentrum
        // (kumulatives Wegdriften).
        var hintX = current.IsPlaced ? current.CenterX : hsr.CenterX;
        var hintY = current.IsPlaced ? current.CenterY : hsr.CenterY;
        var result = _detectors.MarkerRing.Detect(gray, hintX, hintY);
        if (result is null) return null;
        // Außengrenze: ein Marker außerhalb der FS-Reflexion ist ein Fehltreffer
        // (heller Glanzpunkt im HSR o. ä.) und wird verworfen.
        var fs = DetectFangspiegelReflex(gray);
        if (!WithinFangspiegelReflex(fs, result.Center.X, result.Center.Y)) return null;
        return current with
        {
            IsPlaced = true,
            CenterX = result.Center.X,
            CenterY = result.Center.Y,
            RadiusX = result.Radius,
            RadiusY = result.Radius,
            AngleDeg = 0,
        };
    }

    private Marking? DetectLinseMarking(Mat gray, Marking current)
    {
        // Die Linse ist etwa so groß wie der Marker-Ring (sie sitzt bei korrekter
        // Kollimation darin). Ohne den platzierten Marker als Größen-/Lage-Referenz
        // greift Hough sonst die viel größere FS-Reflexion ab. Radius auf ±10% des
        // Marker-Radius begrenzen, Suchzentrum = Marker-Mitte (großzügiger Offset
        // für Dejustage, aber radius-gegated).
        var marker = CurrentMarkings.Marker;
        if (!marker.IsPlaced) return null;
        var rMarker = marker.RadiusX;
        if (rMarker <= 0) return null;
        var maxOffset = Math.Max(rMarker * 2.0, 40);
        var result = _detectors.Linse.Detect(
            gray, marker.CenterX, marker.CenterY,
            minRadius: rMarker * 0.9, maxRadius: rMarker * 1.1,
            maxCenterOffset: maxOffset);
        if (result is null) return null;
        // Außengrenze: die Linse liegt immer in der FS-Reflexion — ein Treffer
        // außerhalb (z. B. der FS-Reflexionsrand selbst) wird verworfen.
        var fs = DetectFangspiegelReflex(gray);
        if (!WithinFangspiegelReflex(fs, result.Center.X, result.Center.Y)) return null;
        return current with
        {
            IsPlaced = true,
            CenterX = result.Center.X,
            CenterY = result.Center.Y,
            RadiusX = result.Radius,
            RadiusY = result.Radius,
            AngleDeg = 0,
        };
    }

    private MarkingKind? GetSelectedKind()
    {
        foreach (var vm in MarkingVms)
        {
            if (vm.IsSelectedForEdit) return vm.Kind;
        }
        return null;
    }

    public void OnFramePointerDownRight()
    {
        if (GetSelectedKind() is MarkingKind kind) DeleteMarking(kind);
    }

    private const double CtrlClickHitToleranceFramePx = 15.0;

    public void OnFrameCtrlClick(double frameX, double frameY)
    {
        // Justage Phase 3: Strg+Klick setzt den Marker (SOLL, fixes Ziel) von Hand.
        // Einmalig auf den Marker-Ring der aktuellen Ansicht; danach bleibt er fix
        // (kein Re-Detect, der ihn im Überlappungsfall wandern ließe).
        if (IsPhase3ManualPlacement)
        {
            PlaceMarkingManually(MarkingKind.Marker, frameX, frameY, persist: true);
            StatusText = $"Marker (SOLL) gesetzt: ({frameX:0}, {frameY:0}).";
            return;
        }

        MarkingKind? bestKind = null;
        double bestDistance = double.MaxValue;
        foreach (var vm in MarkingVms)
        {
            var m = CurrentMarkings[vm.Kind];
            if (!m.IsPlaced || !m.IsVisible) continue;
            var dx = frameX - m.CenterX;
            var dy = frameY - m.CenterY;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            var r = (m.RadiusX + m.RadiusY) * 0.5;
            // Klick innerhalb des Rings ist immer ein Treffer; außerhalb nur
            // wenn der Abstand zum Ring innerhalb der Toleranz liegt.
            var ringDist = Math.Abs(dist - r);
            if (dist > r && ringDist > CtrlClickHitToleranceFramePx) continue;
            if (ringDist < bestDistance)
            {
                bestDistance = ringDist;
                bestKind = vm.Kind;
            }
        }

        if (bestKind is MarkingKind kind)
        {
            var vm = MarkingVms.First(v => v.Kind == kind);
            vm.IsSelectedForEdit = true;
        }
    }

    private double _dragOffsetX;
    private double _dragOffsetY;
    private bool _dragActive;

    public void OnFramePointerBeginDrag(double pressFrameX, double pressFrameY)
    {
        if (IsPhase3ManualPlacement) { _dragActive = true; return; }
        if (GetSelectedKind() is not MarkingKind kind) return;
        var m = CurrentMarkings[kind];
        if (!m.IsPlaced) return;
        _dragOffsetX = m.CenterX - pressFrameX;
        _dragOffsetY = m.CenterY - pressFrameY;
        _dragActive = true;
    }

    public void OnFramePointerDrag(double frameX, double frameY)
    {
        if (!_dragActive) return;
        if (IsPhase3ManualPlacement) { PlaceMarkingManually(MarkingKind.Linse, frameX, frameY, persist: false); return; }
        if (GetSelectedKind() is not MarkingKind kind) return;
        var m = CurrentMarkings[kind];
        if (!m.IsPlaced) return;
        // Beim Verschieben den Fokus-Anker um dieselbe Strecke mitführen, damit er
        // seine relative Lage (z.B. auf der Kante) behält — nicht zum Cursor (in
        // die Markierung hinein) springen.
        var newCx = frameX + _dragOffsetX;
        var newCy = frameY + _dragOffsetY;
        var dx = newCx - m.CenterX;
        var dy = newCy - m.CenterY;
        UpdateMarking(kind, m with
        {
            CenterX = newCx,
            CenterY = newCy,
            FocusPointX = m.FocusPointX is double fx ? fx + dx : (double?)null,
            FocusPointY = m.FocusPointY is double fy ? fy + dy : (double?)null,
        }, persist: false);
        // Von Hand verschoben → betroffene Mess-Phasen gelten wieder als frisch.
        MarkPhasesFreshlyMeasured(JustagePhaseModel.PhasesUsing(kind));
    }

    // Justage Phase 3: beide Kreuze von Hand setzen. Nahe der Ausrichtung sitzen
    // Marker-Ring und Linse fast konzentrisch und sind nicht zuverlässig auto-
    // trennbar — der Anwender sieht beide und setzt sie direkt: Klick = Linse
    // (IST, jede Iteration), Strg+Klick = Marker (SOLL, einmalig, dann fix).
    private bool IsPhase3ManualPlacement => IsJustageMode && ActiveJustagePhase == 3;

    // Kontextabhängiger Gesten-Hinweis am Videobild: macht die Maus-Interaktionen
    // (Klick/Strg+Klick/Ziehen/Rechtsklick) entdeckbar statt Insiderwissen.
    // Aktualisiert über die Modus-/Phasen-/Auswahl-Handler.
    public string FrameGestureHint
    {
        get
        {
            if (IsStarTestMode) return ""; // Mausrad-Zoom steht bereits in der Toolbar.
            if (IsPhase3ManualPlacement)
                return "Klick = Linse (IST) setzen · Strg+Klick = Marker (SOLL) setzen · Ziehen = Linse nachführen";
            if (GetSelectedKind() is MarkingKind kind)
            {
                var name = MarkingVms.FirstOrDefault(v => v.Kind == kind)?.Name ?? kind.ToString();
                return $"Klick = '{name}' platzieren · Ziehen = verschieben · Pfeiltasten = fein · Entf = löschen (Strg+Z macht rückgängig)";
            }
            return "";
        }
    }

    public bool HasFrameGestureHint => FrameGestureHint.Length > 0;

    private void NotifyFrameGestureHint()
    {
        OnPropertyChanged(nameof(FrameGestureHint));
        OnPropertyChanged(nameof(HasFrameGestureHint));
    }

    private void PlaceMarkingManually(MarkingKind kind, double frameX, double frameY, bool persist)
    {
        var m = CurrentMarkings[kind];
        var r = m.RadiusX > 0
            ? m.RadiusX
            : (CurrentMarkings.Marker.RadiusX > 0 ? CurrentMarkings.Marker.RadiusX : 9.0);
        UpdateMarking(kind, m with
        {
            IsPlaced = true,
            CenterX = frameX,
            CenterY = frameY,
            RadiusX = r,
            RadiusY = r,
            AngleDeg = 0,
        }, persist);
        // Von Hand gesetzt → die Mess-Phasen, deren IST/SOLL-Paar diese
        // Markierung enthält, gelten wieder als frisch (siehe PhasesUsing).
        MarkPhasesFreshlyMeasured(JustagePhaseModel.PhasesUsing(kind));
    }

    public void OnFramePointerClick(double frameX, double frameY)
    {
        if (IsPhase3ManualPlacement)
        {
            PlaceMarkingManually(MarkingKind.Linse, frameX, frameY, persist: true);
            StatusText = $"Linse (IST) gesetzt: ({frameX:0}, {frameY:0}).";
            return;
        }

        if (GetSelectedKind() is not MarkingKind kind) return;

        var m = CurrentMarkings[kind];
        double cx, cy;
        if (m.IsPlaced)
        {
            cx = m.CenterX;
            cy = m.CenterY;
        }
        else if (CurrentCalibration is { } cal)
        {
            cx = cal.OpticalCenter.X;
            cy = cal.OpticalCenter.Y;
        }
        else
        {
            cx = _frameWidth / 2.0;
            cy = _frameHeight / 2.0;
        }
        var dx = frameX - cx;
        var dy = frameY - cy;
        var r = Math.Max(2.0, Math.Sqrt(dx * dx + dy * dy));
        UpdateMarking(kind, m with
        {
            IsPlaced = true,
            CenterX = cx,
            CenterY = cy,
            RadiusX = r,
            RadiusY = r,
            AngleDeg = 0,
            FocusPointX = frameX,
            FocusPointY = frameY,
        });
        // Von Hand platziert → betroffene Mess-Phasen gelten wieder als frisch.
        MarkPhasesFreshlyMeasured(JustagePhaseModel.PhasesUsing(kind));
    }

    public void OnFramePointerCommit()
    {
        _dragActive = false;
        PersistMarkings();
        // KEIN automatischer Autofokus mehr beim Setzen — bei hoher Auflösung
        // dauert ein Sweep lange und würde das Platzieren blockieren. Der Fokus
        // läuft jetzt nur auf Anforderung (Befehl FocusSelectedMarking).
    }

    public bool CanFocusSelectedMarking
        => IsFocusControlAvailable && GetSelectedKind() is MarkingKind k && CurrentMarkings[k].IsPlaced;

    [RelayCommand]
    private async Task FocusSelectedMarkingAsync()
    {
        if (GetSelectedKind() is MarkingKind kind && CurrentMarkings[kind].IsPlaced)
        {
            // Voller Sweep (Hint=null): die gespeicherten AutoFocusTarget-Werte sind
            // pro Feature unzuverlässig; eine enge Feinsuche um einen falschen Hint
            // erreicht die echte Fokusebene nicht (OAZ-Rand blieb so bei ~163 hängen).
            // Bewusst awaited (statt fire-and-forget), damit der Busy-Zustand die
            // gesamte Sweep-Dauer abdeckt.
            EnterBusy();
            try { await RunRoiAutofocusAsync(kind, null); }
            finally { ExitBusy(); }
        }
    }

    public bool CanFocusAndDetectSelectedMarking => CanFocusSelectedMarking;

    // Die Einzel-Detektoren laufen global aufs ganze Bild; bei Fehlfokus fangen
    // sie das falsche Feature (z.B. FS-/Velour-Rand statt OAZ-Rand, Radius ~0.4x).
    // NUR der Radius wird als Sanity-Check geprüft — das Zentrum darf weit wandern,
    // denn der Sinn von „Scharfstellen + Erkennen" ist gerade, eine grob/deutlich
    // verschoben gesetzte Markierung an die korrekte Position zu snappen. Eine
    // Zentrumsgrenze würde genau diese Korrektur verhindern. Die Detektoren sind an
    // verlässliche Referenzen verankert (OAZ-Rand=größter Kreis, Marker=HSR-Zentrum).
    private static bool IsPlausibleRefinement(Marking current, Marking detected)
    {
        var curR = Math.Max(current.RadiusX, 1.0);
        var detR = Math.Max(detected.RadiusX, 1.0);
        var ratio = detR / curR;
        return ratio is >= 0.6 and <= 1.6;
    }

    // Scharfstellen + Erkennen für genau das selektierte Feature: Bei hoher
    // Auflösung liegen OAZ-Rand (nah) und Marker (fern) auf verschiedenen
    // Fokusebenen — ein einzelner Frame kann nie alle scharf haben. Deshalb pro
    // Feature: erst ROI-Autofokus, dann nur dessen Detektor auf dem frisch
    // fokussierten Bild (statt langsamer Voll-Pipeline-mit-Refokus).
    [RelayCommand]
    private async Task FocusAndDetectSelectedMarkingAsync()
    {
        EnterBusy();
        try { await FocusAndDetectSelectedMarkingCoreAsync(); }
        finally { ExitBusy(); }
    }

    private async Task FocusAndDetectSelectedMarkingCoreAsync()
    {
        if (GetSelectedKind() is not MarkingKind kind || !CurrentMarkings[kind].IsPlaced) return;
        var source = _source;
        if (source is null) return;
        var name = MarkingVms.FirstOrDefault(v => v.Kind == kind)?.Name ?? "Markierung";

        // 1. Scharfstellen auf das selektierte Feature (voller Sweep, kein Hint).
        await RunRoiAutofocusAsync(kind, null);

        // 2. Auf dem frisch fokussierten Frame nur dieses eine Feature erkennen.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            // Fokusmotor nach dem Sweep zur Ruhe kommen lassen.
            await Task.Delay(300, cts.Token);
            var current = CurrentMarkings[kind];
            StatusText = $"Erkenne {name} …";
            Marking? detected = kind == MarkingKind.OazRand
                ? await DetectOazRandAveragedAsync(source, current, cts.Token)
                : await DetectOnceAsync(source, kind, current, cts.Token);
            if (detected is null)
            {
                StatusText = $"{name}: scharfgestellt, aber nicht erkannt — Belichtung anpassen oder die Markierung manuell setzen.";
                return;
            }
            if (!IsPlausibleRefinement(current, detected))
            {
                StatusText = $"{name}: Erkennung unplausibel (Sprung), verworfen.";
                return;
            }
            detected = detected with { AutoFocusTarget = FocusValue };
            UpdateMarking(kind, detected, persist: true);
            StatusText = $"{name}: scharfgestellt und erkannt.";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"{name}: Erkennung abgebrochen.";
        }
        catch (Exception ex)
        {
            StatusText = $"{name}: {ex.Message}";
        }
    }

    public event System.Action? OverlayLayoutChanged;

    public DisplayTransform CurrentDisplayTransform => _displayTransform;

    public void NotifyControlSize(double width, double height)
    {
        _lastControlWidth = width;
        _lastControlHeight = height;
        RecomputeDisplayTransform();
    }

    private void RecomputeDisplayTransform()
    {
        _displayTransform = (IsStarTestMode && _starGray is not null)
            ? DisplayTransform.Compute(
                _lastControlWidth, _lastControlHeight,
                _croppedWidth, _croppedHeight,
                _starCropOffsetX, _starCropOffsetY)
            : DisplayTransform.Compute(
                _lastControlWidth, _lastControlHeight,
                _croppedWidth, _croppedHeight,
                _frameWidth, _frameHeight);
        RecomputeOverlayDisplay();
        OverlayLayoutChanged?.Invoke();
    }

    private void RecomputeOverlayDisplay()
    {
        // Hauptspiegel-Kipp-Phase: alle Markierungs-Overlays verdecken die echte
        // Linse im Bild → ausblenden. Es bleibt nur der gestrichelte SOLL-Kreis
        // als Orientierung (siehe RecomputeJustageOverlay).
        var lensTiltPhase = IsJustageMode && ActiveJustagePhase == 3;

        // Im Sterntest-Modus zeigt nur das Donut-Overlay; die OCAL-Markierungen
        // (stale Koordinaten auf einem fremden Stern-Frame) bleiben aus. Bei
        // Auflösungs-Mismatch ebenfalls unterdrücken — die Frame-Pixel-Koordinaten
        // stammen von einer anderen Capture-Größe und würden falsch skaliert als
        // sinnloser Riesen-/Zwerg-Bogen über dem aktuellen Bild liegen (siehe
        // MarkingsResolutionMismatch, KEINE automatische Umskalierung).
        foreach (var vm in MarkingVms)
        {
            vm.SetDisplayFrom(CurrentMarkings[vm.Kind], _displayTransform,
                suppress: lensTiltPhase || IsStarTestMode || MarkingsResolutionMismatch);
        }

        // Optisches-Zentrum-Fadenkreuz nur außerhalb der Justage: dort führt das
        // IST/SOLL-Overlay, und in Phase 3 ist das optische Zentrum kein Ziel mehr
        // (Linse → Marker-Punkt) — das Fadenkreuz würde nur verwirren. Bei
        // Kalibrier-Auflösungs-Mismatch ebenfalls aus (siehe CalibrationResolutionMismatch).
        if (_displayTransform.IsValid && CurrentCalibration is { } cal && !IsJustageMode && !IsStarTestMode
            && !CalibrationResolutionMismatch)
        {
            var (rx, ry) = _displayTransform.MapToDisplay(cal.OpticalCenter.X, cal.OpticalCenter.Y);
            OpticalCenterDisplayX = rx;
            OpticalCenterDisplayY = ry;
            ShowReticle = true;
        }
        else
        {
            ShowReticle = false;
        }

        if (_displayTransform.IsValid && !lensTiltPhase && !MarkingsResolutionMismatch
            && GetSelectedKind() is MarkingKind k
            && CurrentMarkings[k] is { IsPlaced: true, IsVisible: true } activeMarking)
        {
            var (cx, cy) = _displayTransform.MapToDisplay(activeMarking.CenterX, activeMarking.CenterY);
            ActiveCrossDisplayX = cx;
            ActiveCrossDisplayY = cy;
            ActiveCrossBrush = MarkingVms.First(v => v.Kind == k).Swatch;
            ShowActiveCross = true;

            // Fokus-ROI (Box am Fokus-Anker) der selektierten Markierung einzeichnen.
            if (IsFocusControlAvailable)
            {
                var roi = RoiForMarking(activeMarking);
                var (rcx, rcy) = _displayTransform.MapToDisplay(roi.CenterX, roi.CenterY);
                var size = _displayTransform.MapLengthToDisplay(roi.HalfSize) * 2;
                FocusRoiLeft = rcx - size / 2;
                FocusRoiTop = rcy - size / 2;
                FocusRoiSize = size;
                FocusRoiBrush = ActiveCrossBrush;
                ShowFocusRoi = true;
            }
            else
            {
                ShowFocusRoi = false;
            }
        }
        else
        {
            ShowActiveCross = false;
            ShowFocusRoi = false;
        }

        // Linsen-Mittelpunkt einzeichnen, sobald die Linse platziert/sichtbar ist.
        // In der Hauptspiegel-Kipp-Phase setzt der Anwender die Linse von Hand
        // (Klick auf die Linsenmitte) — dort ist das Kreuz das einzige Feedback,
        // wo der IST-Punkt liegt, also bewusst auch hier zeigen.
        if (_displayTransform.IsValid && !IsStarTestMode && !MarkingsResolutionMismatch
            && CurrentMarkings.Linse is { IsPlaced: true, IsVisible: true } linse)
        {
            var (lcx, lcy) = _displayTransform.MapToDisplay(linse.CenterX, linse.CenterY);
            LinseCrossDisplayX = lcx;
            LinseCrossDisplayY = lcy;
            ShowLinseCross = true;
        }
        else
        {
            ShowLinseCross = false;
        }

        RecomputeJustageOverlay();
        RecomputeStarTestOverlay();
    }

    private void RecomputeJustageOverlay()
    {
        // Defaults; werden am Ende gesetzt, wenn ein gültiges Overlay vorliegt.
        ShowJustageArrow = false;
        // Bei Auflösungs-Mismatch gelten die Markierungen als nicht verwertbar
        // (siehe MarkingsResolutionMismatch) — kein IST/SOLL-Overlay auf Basis
        // falsch skalierter Koordinaten.
        if (!IsJustageMode || !_displayTransform.IsValid || MarkingsResolutionMismatch)
        {
            ShowJustageOverlay = false;
            return;
        }

        Marking ist;
        double sollFrameX, sollFrameY;
        IBrush brush;
        switch (ActiveJustagePhase)
        {
            case 1:
                if (!CurrentMarkings.Sekundaer.IsPlaced || !CurrentMarkings.OazRand.IsPlaced)
                {
                    ShowJustageOverlay = false; return;
                }
                ist = CurrentMarkings.Sekundaer;
                sollFrameX = CurrentMarkings.OazRand.CenterX;
                sollFrameY = CurrentMarkings.OazRand.CenterY;
                brush = SekundaerVm.Swatch;
                break;
            case 2:
                if (!CurrentMarkings.HauptspiegelReflex.IsPlaced || !CurrentMarkings.Sekundaer.IsPlaced)
                {
                    ShowJustageOverlay = false; return;
                }
                ist = CurrentMarkings.HauptspiegelReflex;
                sollFrameX = CurrentMarkings.Sekundaer.CenterX;
                sollFrameY = CurrentMarkings.Sekundaer.CenterY;
                brush = HauptspiegelReflexVm.Swatch;
                break;
            case 3:
                // IST = Linse, SOLL = Marker-Punkt (siehe ActivePhaseOffset).
                if (!CurrentMarkings.Linse.IsPlaced || !CurrentMarkings.Marker.IsPlaced)
                {
                    ShowJustageOverlay = false; return;
                }
                ist = CurrentMarkings.Linse;
                sollFrameX = CurrentMarkings.Marker.CenterX;
                sollFrameY = CurrentMarkings.Marker.CenterY;
                brush = LinseVm.Swatch;
                break;
            default:
                ShowJustageOverlay = false; return;
        }

        var (istX, istY) = _displayTransform.MapToDisplay(ist.CenterX, ist.CenterY);
        var (sollX, sollY) = _displayTransform.MapToDisplay(sollFrameX, sollFrameY);
        var rDisp = _displayTransform.MapLengthToDisplay(Math.Max(ist.RadiusX, ist.RadiusY));

        JustageSollLeft = sollX - rDisp;
        JustageSollTop = sollY - rDisp;
        JustageSollWidth = rDisp * 2;
        JustageSollHeight = rDisp * 2;
        JustageIstPoint = new Avalonia.Point(istX, istY);
        JustageSollPoint = new Avalonia.Point(sollX, sollY);
        JustageOverlayBrush = brush;
        ShowJustageOverlay = true;
        // In der Kipp-Phase nur den SOLL-Kreis zeigen, keinen IST→SOLL-Pfeil.
        ShowJustageArrow = ActiveJustagePhase != 3;
    }

    // === Sterntest-Modus (defokussierte Achs-Kollimation, datei-basiert) ========

    // Donut-Overlay: äußerer Scheibchen-Kreis (grün, SOLL) + innerer Obstruktions-
    // Kreis (rot, IST) + Versatzlinie. Koordinaten in Display-Pixeln.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlayLegend))]
    private bool _showStarTestOverlay;
    [ObservableProperty] private double _starOuterLeft;
    [ObservableProperty] private double _starOuterTop;
    [ObservableProperty] private double _starOuterSize;
    [ObservableProperty] private double _starInnerLeft;
    [ObservableProperty] private double _starInnerTop;
    [ObservableProperty] private double _starInnerSize;
    [ObservableProperty] private Avalonia.Point _starSollPoint;
    [ObservableProperty] private Avalonia.Point _starIstPoint;

    // Bildquelle: Datei (manuell), Ordner überwachen (Auto-Load) oder Live über
    // eine Alpaca-Kamera (INDIGO-Alpaca-Agent/ASCOM Remote, crossplattform).
    public string[] StarTestSourceOptions { get; } = { "Datei (FITS)", "Ordner überwachen", "Live (Alpaca)", "Live (ASI)" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStarFileSource))]
    [NotifyPropertyChangedFor(nameof(IsStarWatchSource))]
    [NotifyPropertyChangedFor(nameof(IsStarAlpacaSource))]
    [NotifyPropertyChangedFor(nameof(IsStarAsiSource))]
    [NotifyPropertyChangedFor(nameof(IsLiveStarTestSource))]
    [NotifyPropertyChangedFor(nameof(StarTestQualityHint))]
    private string _selectedStarTestSource = "Datei (FITS)";

    public bool IsStarFileSource => SelectedStarTestSource?.StartsWith("Datei") ?? true;
    public bool IsStarWatchSource => SelectedStarTestSource?.StartsWith("Ordner") ?? false;
    public bool IsStarAlpacaSource => SelectedStarTestSource?.Contains("Alpaca") ?? false;
    public bool IsStarAsiSource => SelectedStarTestSource?.Contains("ASI") ?? false;

    // Der Fokuser hängt am Alpaca-/INDIGO-Server, unabhängig davon, ob die
    // Sterntest-Bilder selbst über Alpaca oder nativ über ASI kommen (siehe
    // MainWindow.axaml, Fokuser-Panel) — deshalb eigene Sichtbarkeits-
    // Bedingung statt an eine einzelne Quelle gekoppelt.
    public bool IsLiveStarTestSource => IsStarAlpacaSource || IsStarAsiSource;

    partial void OnSelectedStarTestSourceChanged(string value)
    {
        if (!IsStarWatchSource) StopStarWatch();
        if (!IsStarAlpacaSource) { StopAlpaca(); StopFocuser(); }
        if (!IsStarAsiSource) StopAsi();
        if (IsStarWatchSource)
        {
            // Gemerkten Ordner gleich wieder überwachen — kein erneutes Auswählen nötig.
            if (_watchFolder is { } f && _starWatcher is null) StartStarWatch(f);
            else StatusText = "Ordner überwachen: Ordner wählen — neue Aufnahmen werden automatisch geladen.";
        }
        else if (IsStarAlpacaSource)
            StatusText = "Live (Alpaca): Host/Port/Gerät eintragen, verbinden, dann 'Belichten'. (INDIGO: Port 7624)";
        else if (IsStarAsiSource)
            StatusText = "Live (ASI): Kamera suchen, verbinden, dann 'Belichten' (USB direkt).";
    }

    // --- Live über Alpaca-Kamera ---------------------------------------------
    [ObservableProperty] private string _alpacaHost = "localhost";
    [ObservableProperty] private int _alpacaPort = 11111;   // INDIGO: 7624, ASCOM-Default: 11111
    [ObservableProperty] private int _alpacaDevice;
    [ObservableProperty] private double _alpacaExposure = 1.0;
    [ObservableProperty] private double _alpacaGain = 100;

    // Eine laufende Alpaca-Operation (Verbinden/Aufnehmen/Suchen) sperrt die anderen.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCaptureAlpaca))]
    [NotifyCanExecuteChangedFor(nameof(CaptureAlpacaCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverAlpacaCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAlpacaConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAlpacaLoopCommand))]
    private bool _isAlpacaBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCaptureAlpaca))]
    [NotifyPropertyChangedFor(nameof(AlpacaConnectionText))]
    [NotifyCanExecuteChangedFor(nameof(CaptureAlpacaCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverAlpacaCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAlpacaLoopCommand))]
    private bool _isAlpacaConnected;

    public string AlpacaConnectionText => IsAlpacaConnected ? "Verbindung trennen" : "Verbinden";

    // Im Netz gefundene Alpaca-Kameras (Discovery); Auswahl füllt Host/Port/Gerät.
    [ObservableProperty]
    private System.Collections.Generic.IReadOnlyList<AlpacaFoundCamera> _alpacaFoundDevices
        = System.Array.Empty<AlpacaFoundCamera>();

    private AlpacaFoundCamera? _selectedAlpacaFound;
    public AlpacaFoundCamera? SelectedAlpacaFound
    {
        get => _selectedAlpacaFound;
        set
        {
            SetProperty(ref _selectedAlpacaFound, value);
            if (value is { } c) { AlpacaHost = c.Host; AlpacaPort = c.Port; AlpacaDevice = c.DeviceNumber; }
        }
    }

    private AlpacaCameraSource? _alpaca;

    public bool CanCaptureAlpaca => IsAlpacaConnected && !IsAlpacaBusy;
    public bool CanToggleAlpaca => !IsAlpacaBusy;
    public bool CanDiscoverAlpaca => !IsAlpacaBusy && !IsAlpacaConnected;

    [RelayCommand(CanExecute = nameof(CanDiscoverAlpaca))]
    private async Task DiscoverAlpaca()
    {
        IsAlpacaBusy = true;
        try
        {
            StatusText = "Suche Alpaca-Kameras im Netzwerk …";
            var found = await AlpacaCameraSource.DiscoverCamerasAsync();
            AlpacaFoundDevices = found;
            if (found.Count > 0) SelectedAlpacaFound = found[0];
            StatusText = found.Count == 0
                ? "Keine Alpaca-Kameras gefunden (läuft ein Alpaca-Server/INDIGO-Agent?)."
                : $"{found.Count} Alpaca-Kamera(s) gefunden.";
        }
        catch (Exception ex)
        {
            StatusText = $"Alpaca-Suche fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsAlpacaBusy = false;
        }
    }

    // Verbindung explizit auf-/abbauen (Toggle „Verbinden" / „Verbindung trennen").
    [RelayCommand(CanExecute = nameof(CanToggleAlpaca))]
    private async Task ToggleAlpacaConnection()
    {
        if (IsAlpacaConnected)
        {
            StopAlpaca();
            StatusText = "Alpaca-Verbindung getrennt.";
            return;
        }
        IsAlpacaBusy = true;
        var host = AlpacaHost; var port = AlpacaPort; var dev = AlpacaDevice;
        try
        {
            var src = await Task.Run(() =>
            {
                var s = _cameraSources.CreateAlpaca(host, port, dev);
                s.Start();
                return s;
            });
            _alpaca = src;
            IsAlpacaConnected = true;
            StatusText = $"Verbunden: {host}:{port}/{dev} — Belichtung {src.MinExposure:0.###}..{src.MaxExposure:0} s, Gain {src.GainMin:0}..{src.GainMax:0}.";
        }
        catch (Exception ex)
        {
            StopAlpaca();
            StatusText = $"Verbinden fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsAlpacaBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCaptureAlpaca))]
    private async Task CaptureAlpaca()
    {
        IsAlpacaBusy = true;
        try { await CaptureAlpacaFrameAsync(); }
        finally { IsAlpacaBusy = false; }
    }

    // Eine Belichtung ohne Busy-Verwaltung — geteilt von Einzelbild (Busy-Klammer
    // im Command) und Loop (Busy über die gesamte Laufzeit). false = Fehler
    // (Meldung steht dann schon im Status), stoppt den Loop.
    private async Task<bool> CaptureAlpacaFrameAsync()
    {
        if (_alpaca is not { } cam) { StatusText = "Erst verbinden."; return false; }
        var exp = AlpacaExposure; var gain = AlpacaGain;
        try
        {
            var gray8 = await Task.Run(() =>
            {
                cam.Exposure = exp;
                cam.Gain = gain;
                using var raw = cam.GrabFrame();
                return raw is null ? null : StarFramePrep.ToDisplayGray8(raw);
            });
            if (gray8 is null) { StatusText = "Alpaca: keine Aufnahme erhalten."; return false; }
            ApplyStarGray(gray8, $"Alpaca {AlpacaHost}:{AlpacaPort}/{AlpacaDevice}");
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Alpaca-Aufnahme fehlgeschlagen: {ex.Message}";
            return false;
        }
    }

    private void StopAlpaca()
    {
        StopStarLoop();
        var cam = _alpaca;
        _alpaca = null;
        IsAlpacaConnected = false;
        // Trennen macht HTTP-Aufrufe (Disconnect) → im Hintergrund, damit der
        // UI-Thread nicht blockiert/deadlockt. Best effort.
        if (cam is not null) _ = Task.Run(() => { try { cam.Dispose(); } catch { /* ignore */ } });
    }

    // --- Live über native ASI-Kamera (USB direkt) ----------------------------
    [ObservableProperty] private double _asiExposure = 1.0;
    [ObservableProperty] private double _asiGain = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCaptureAsi))]
    [NotifyCanExecuteChangedFor(nameof(CaptureAsiCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverAsiCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAsiConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAsiLoopCommand))]
    private bool _isAsiBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCaptureAsi))]
    [NotifyPropertyChangedFor(nameof(AsiConnectionText))]
    [NotifyCanExecuteChangedFor(nameof(CaptureAsiCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverAsiCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAsiLoopCommand))]
    private bool _isAsiConnected;

    public string AsiConnectionText => IsAsiConnected ? "Verbindung trennen" : "Verbinden";

    [ObservableProperty]
    private System.Collections.Generic.IReadOnlyList<AsiFoundCamera> _asiFoundDevices
        = System.Array.Empty<AsiFoundCamera>();

    private AsiFoundCamera? _selectedAsiFound;
    public AsiFoundCamera? SelectedAsiFound
    {
        get => _selectedAsiFound;
        set { SetProperty(ref _selectedAsiFound, value); if (value is { } c) _asiDeviceIndex = c.Index; }
    }
    private int _asiDeviceIndex;

    private AsiCameraSource? _asi;

    public bool CanCaptureAsi => IsAsiConnected && !IsAsiBusy;
    public bool CanToggleAsi => !IsAsiBusy;
    public bool CanDiscoverAsi => !IsAsiBusy && !IsAsiConnected;

    [RelayCommand(CanExecute = nameof(CanDiscoverAsi))]
    private async Task DiscoverAsi()
    {
        IsAsiBusy = true;
        try
        {
            StatusText = "Suche ASI-Kameras (USB) …";
            var found = await Task.Run(AsiCameraSource.DiscoverCameras);
            AsiFoundDevices = found;
            if (found.Count > 0) SelectedAsiFound = found[0];
            StatusText = found.Count == 0
                ? "Keine ASI-Kamera gefunden (SDK installiert? Kamera frei?)."
                : $"{found.Count} ASI-Kamera(s) gefunden.";
        }
        catch (DllNotFoundException)
        {
            // Die ZWO-Bibliothek wird aus Lizenzgründen nicht mitgeliefert —
            // konkreten Ausweg je Plattform nennen statt der nackten
            // P/Invoke-Fehlermeldung.
            StatusText = OperatingSystem.IsMacOS()
                ? "ASI-SDK nicht gefunden: scripts/setup-asi-macos.sh ausführen (holt libASICamera2.dylib "
                  + "arm64 aus dem ZWO-Camera-SDK). Die dylib aus ASI Studio ist x86_64 und funktioniert nicht."
                : "ASI-SDK nicht gefunden: ASICamera2.dll (64-bit, aus ZWO ASI Studio/SDK) neben die "
                  + "FreeCol.exe legen oder ASI Studio installieren, dann App neu starten.";
        }
        catch (Exception ex)
        {
            StatusText = $"ASI-Suche fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsAsiBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleAsi))]
    private async Task ToggleAsiConnection()
    {
        if (IsAsiConnected)
        {
            StopAsi();
            StatusText = "ASI-Verbindung getrennt.";
            return;
        }
        IsAsiBusy = true;
        var idx = _asiDeviceIndex;
        try
        {
            var src = await Task.Run(() =>
            {
                var s = _cameraSources.CreateAsi(idx);
                s.Start();
                return s;
            });
            _asi = src;
            IsAsiConnected = true;
            StatusText = $"ASI verbunden (Gerät {idx}) — Belichtung {src.MinExposure:0.###}..{src.MaxExposure:0} s, Gain {src.GainMin:0}..{src.GainMax:0}.";
        }
        catch (Exception ex)
        {
            StopAsi();
            StatusText = $"ASI-Verbinden fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsAsiBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCaptureAsi))]
    private async Task CaptureAsi()
    {
        IsAsiBusy = true;
        try { await CaptureAsiFrameAsync(); }
        finally { IsAsiBusy = false; }
    }

    private async Task<bool> CaptureAsiFrameAsync()
    {
        if (_asi is not { } cam) { StatusText = "Erst verbinden."; return false; }
        var exp = AsiExposure; var gain = AsiGain;
        try
        {
            var gray8 = await Task.Run(() =>
            {
                cam.Exposure = exp;
                cam.Gain = gain;
                using var raw = cam.GrabFrame();
                return raw is null ? null : StarFramePrep.ToDisplayGray8(raw);
            });
            if (gray8 is null) { StatusText = "ASI: keine Aufnahme erhalten."; return false; }
            ApplyStarGray(gray8, $"ASI Gerät {_asiDeviceIndex}");
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"ASI-Aufnahme fehlgeschlagen: {ex.Message}";
            return false;
        }
    }

    private void StopAsi()
    {
        StopStarLoop();
        var cam = _asi;
        _asi = null;
        IsAsiConnected = false;
        if (cam is not null) _ = Task.Run(() => { try { cam.Dispose(); } catch { /* ignore */ } });
    }

    // --- Loop-Aufnahme: nach jedem Bild automatisch die nächste Belichtung ----
    // Fürs iterative Schrauben-Drehen am Teleskop: drehen → das nächste Bild kommt
    // von allein. Busy bleibt für die gesamte Loop-Dauer gesetzt (sperrt Trennen/
    // Einzelbild); nur der Loop-Button selbst bleibt über IsStarLoopRunning aktiv.
    private CancellationTokenSource? _starLoopCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarLoopButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAlpacaLoopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAsiLoopCommand))]
    private bool _isStarLoopRunning;

    [ObservableProperty] private string _starLoopInfoText = "";

    public string StarLoopButtonText => IsStarLoopRunning ? "■ Loop stoppen" : "▶ Loop starten";

    public bool CanToggleAlpacaLoop => IsStarLoopRunning || CanCaptureAlpaca;
    public bool CanToggleAsiLoop => IsStarLoopRunning || CanCaptureAsi;

    [RelayCommand(CanExecute = nameof(CanToggleAlpacaLoop))]
    private void ToggleAlpacaLoop()
    {
        if (IsStarLoopRunning) { RequestStarLoopStop(); return; }
        _ = RunStarLoopAsync(CaptureAlpacaFrameAsync, b => IsAlpacaBusy = b);
    }

    [RelayCommand(CanExecute = nameof(CanToggleAsiLoop))]
    private void ToggleAsiLoop()
    {
        if (IsStarLoopRunning) { RequestStarLoopStop(); return; }
        _ = RunStarLoopAsync(CaptureAsiFrameAsync, b => IsAsiBusy = b);
    }

    private void RequestStarLoopStop()
    {
        _starLoopCts?.Cancel();
        StarLoopInfoText = "Loop stoppt nach der laufenden Belichtung …";
    }

    // Bricht einen laufenden Loop ab (Trennen, Quell-/Moduswechsel, Fenster zu).
    public void StopStarLoop() => _starLoopCts?.Cancel();

    private async Task RunStarLoopAsync(Func<Task<bool>> captureFrame, Action<bool> setBusy)
    {
        var cts = new CancellationTokenSource();
        _starLoopCts = cts;
        IsStarLoopRunning = true;
        setBusy(true);
        var frames = 0;
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                StarLoopInfoText = $"Belichtung {frames + 1} läuft …";
                if (!await captureFrame()) break;
                frames++;
            }
        }
        finally
        {
            IsStarLoopRunning = false;
            _starLoopCts = null;
            cts.Dispose();
            setBusy(false);
            StarLoopInfoText = "";
            if (frames > 0) StatusText = $"Loop beendet nach {frames} Bild(ern).";
        }
    }

    // --- Fokuser über Alpaca (Sterntest) --------------------------------------
    // Gleicher Server wie die Alpaca-Kamera (Host/Port von oben), eigener Gerätetyp
    // 'focuser' mit eigener Geräte-Nummer. Damit lässt sich der Donut-Defokus direkt
    // aus FreeCol einstellen, ohne zu einer Fremdsoftware zu wechseln.
    private AlpacaFocuserClient? _focuser;
    private bool _focuserAbsolute;
    private int _focuserMaxStep;

    [ObservableProperty] private int _focuserDevice;
    [ObservableProperty] private int _focuserStepSize = 100;
    [ObservableProperty] private string _focuserTargetText = "";
    [ObservableProperty] private string _focuserPositionText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FocuserConnectionText))]
    [NotifyCanExecuteChangedFor(nameof(FocuserInCommand))]
    [NotifyCanExecuteChangedFor(nameof(FocuserOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(FocuserGoToCommand))]
    [NotifyCanExecuteChangedFor(nameof(FocuserHaltCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkFocusCenterCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToIntraFocusCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToExtraFocusCommand))]
    [NotifyCanExecuteChangedFor(nameof(AutoFindDefocusCommand))]
    [NotifyCanExecuteChangedFor(nameof(MeasurePairCommand))]
    private bool _isFocuserConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FocuserInCommand))]
    [NotifyCanExecuteChangedFor(nameof(FocuserOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(FocuserGoToCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToIntraFocusCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToExtraFocusCommand))]
    [NotifyCanExecuteChangedFor(nameof(AutoFindDefocusCommand))]
    [NotifyCanExecuteChangedFor(nameof(MeasurePairCommand))]
    private bool _isFocuserMoving;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleFocuserConnectionCommand))]
    private bool _isFocuserBusy;

    public string FocuserConnectionText => IsFocuserConnected ? "Fokuser trennen" : "Fokuser verbinden";

    public bool CanToggleFocuser => !IsFocuserBusy;
    // Während der automatischen Defokus-Suche fährt der Fokuser eigenständig —
    // manuelle Bewegung würde die Suche verwirren, siehe AutoFindDefocus.
    public bool CanMoveFocuser => IsFocuserConnected && !IsFocuserMoving && !IsAutoFindDefocusRunning;
    public bool CanHaltFocuser => IsFocuserConnected;

    [RelayCommand]
    private void SetFocuserStep(string step)
    {
        if (int.TryParse(step, out var v) && v > 0) FocuserStepSize = v;
    }

    [RelayCommand(CanExecute = nameof(CanToggleFocuser))]
    private async Task ToggleFocuserConnection()
    {
        if (IsFocuserConnected)
        {
            StopFocuser();
            StatusText = "Fokuser getrennt.";
            return;
        }
        IsFocuserBusy = true;
        var host = AlpacaHost; var port = AlpacaPort; var dev = FocuserDevice;
        try
        {
            var (client, pos, abs, max, temp) = await Task.Run(() =>
            {
                var c = _cameraSources.CreateAlpacaFocuser(host, port, dev);
                c.Start();
                return (c, c.Position, c.IsAbsolute, c.MaxStep, c.Temperature);
            });
            _focuser = client;
            _focuserAbsolute = abs;
            _focuserMaxStep = max;
            IsFocuserConnected = true;
            UpdateFocuserPositionText(pos, moving: false, temp);
            StatusText = abs
                ? $"Fokuser verbunden ({host}:{port}/{dev}) — Position {pos}, max {max} Schritte."
                : $"Fokuser verbunden ({host}:{port}/{dev}) — relatives Gerät: nur ±Schritte, keine Zielposition.";
        }
        catch (Exception ex)
        {
            StopFocuser();
            StatusText = $"Fokuser-Verbinden fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsFocuserBusy = false;
        }
    }

    private void StopFocuser()
    {
        var f = _focuser;
        _focuser = null;
        IsFocuserConnected = false;
        IsFocuserMoving = false;
        FocuserPositionText = "";
        // Trennen macht HTTP-Aufrufe → im Hintergrund, best effort (wie StopAlpaca).
        if (f is not null) _ = Task.Run(() => { try { f.Dispose(); } catch { /* ignore */ } });
    }

    [RelayCommand(CanExecute = nameof(CanMoveFocuser))]
    private Task FocuserIn() => FocuserMoveRelativeAsync(-FocuserStepSize);

    [RelayCommand(CanExecute = nameof(CanMoveFocuser))]
    private Task FocuserOut() => FocuserMoveRelativeAsync(FocuserStepSize);

    [RelayCommand(CanExecute = nameof(CanMoveFocuser))]
    private async Task FocuserGoTo()
    {
        if (_focuser is not { } f) return;
        if (!_focuserAbsolute)
        {
            StatusText = "Dieser Fokuser ist relativ — Zielposition wird nicht unterstützt, ±Schritte nutzen.";
            return;
        }
        if (!int.TryParse(FocuserTargetText.Trim(), out var target) || target < 0)
        {
            StatusText = $"Fokuser: Zielposition '{FocuserTargetText}' ungültig (ganze Zahl ≥ 0).";
            return;
        }
        await RunFocuserMoveAsync(f, () => f.MoveTo(target));
    }

    private async Task FocuserMoveRelativeAsync(int delta)
    {
        if (_focuser is not { } f) return;
        await RunFocuserMoveAsync(f, () => f.MoveRelative(delta));
    }

    // Halt bleibt während der Fahrt bedienbar (nur an Verbindung gekoppelt) und
    // beendet das Polling über IsMoving=false auf dem Gerät.
    [RelayCommand(CanExecute = nameof(CanHaltFocuser))]
    private async Task FocuserHalt()
    {
        if (_focuser is not { } f) return;
        await Task.Run(f.Halt);
    }

    // ct: optional Abbruch für die automatische Defokus-Suche (AutoFindDefocus)
    // — bricht das Nachführ-Polling ab, ohne das Verhalten der übrigen Aufrufer
    // (Default = CancellationToken.None) zu ändern.
    private async Task RunFocuserMoveAsync(AlpacaFocuserClient f, Action move, CancellationToken ct = default)
    {
        IsFocuserMoving = true;
        try
        {
            await Task.Run(move, ct);
            // Position live nachführen, bis das Gerät steht (max ~2,5 min als
            // Sicherheitsnetz gegen Geräte, deren IsMoving hängen bleibt).
            for (var i = 0; i < 600; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (pos, moving, temp) = await Task.Run(() => (f.Position, f.IsMoving, f.Temperature), ct);
                UpdateFocuserPositionText(pos, moving, temp);
                if (!moving || !IsFocuserConnected) break;
                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusText = $"Fokuser-Bewegung fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsFocuserMoving = false;
        }
    }

    private void UpdateFocuserPositionText(int pos, bool moving, double? temp)
    {
        var posPart = _focuserAbsolute && _focuserMaxStep > 0 ? $"Position: {pos} / {_focuserMaxStep}" : $"Position: {pos}";
        FocuserPositionText = posPart
            + (moving ? " · fährt …" : "")
            + (temp is { } t ? $" · {t:0.0} °C" : "");
    }

    // --- Fokus-Paar (Sterntest): reproduzierbar zwischen Intra-/Extrafokal ---
    // Der Fangspiegel sitzt beim Newton absichtlich versetzt — ein Rest-Versatz
    // zur Obstruktion ist normal. Der eigentliche Kollimationsfehler zeigt sich
    // erst im Vergleich zweier Aufnahmen beidseits des Fokus (Mittel der
    // Versatzvektoren; die Auswertung selbst folgt in einem separaten Schritt).
    // Dafür merkt sich die App eine Fokus-Mitte + einen Defokus-Betrag (Schritte)
    // und kann beide Zielpositionen reproduzierbar anfahren — reine Arithmetik
    // dazu in FreeCol.Core.Focuser.FocusPairModel (isoliert testbar).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IntraFocusPosition))]
    [NotifyPropertyChangedFor(nameof(ExtraFocusPosition))]
    [NotifyPropertyChangedFor(nameof(HasFocusPair))]
    [NotifyPropertyChangedFor(nameof(FocusPairStatusText))]
    [NotifyCanExecuteChangedFor(nameof(GoToIntraFocusCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToExtraFocusCommand))]
    [NotifyCanExecuteChangedFor(nameof(AutoFindDefocusCommand))]
    [NotifyCanExecuteChangedFor(nameof(MeasurePairCommand))]
    private int _focusCenterPosition = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IntraFocusPosition))]
    [NotifyPropertyChangedFor(nameof(ExtraFocusPosition))]
    [NotifyPropertyChangedFor(nameof(HasFocusPair))]
    [NotifyPropertyChangedFor(nameof(FocusPairStatusText))]
    [NotifyCanExecuteChangedFor(nameof(GoToIntraFocusCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToExtraFocusCommand))]
    [NotifyCanExecuteChangedFor(nameof(MeasurePairCommand))]
    private int _defocusSteps;

    /// <summary>Intrafokale Zielposition (reine Anzeige, nicht geklemmt) —
    /// die Fahrbereichsprüfung passiert beim Anfahren, siehe
    /// <see cref="GoToFocusPairPositionAsync"/>.</summary>
    public int IntraFocusPosition => FocusPairModel.IntraFocusPosition(FocusCenterPosition, DefocusSteps);

    /// <summary>Extrafokale Zielposition (reine Anzeige, nicht geklemmt).</summary>
    public int ExtraFocusPosition => FocusPairModel.ExtraFocusPosition(FocusCenterPosition, DefocusSteps);

    /// <summary>Ein gültiges Fokus-Paar liegt vor, sobald eine Fokus-Mitte
    /// gemerkt UND ein Defokus-Betrag &gt; 0 gesetzt ist.</summary>
    public bool HasFocusPair => FocusCenterPosition >= 0 && DefocusSteps > 0;

    public string FocusPairStatusText => FocusCenterPosition < 0
        ? "Noch keine Fokus-Mitte gemerkt."
        : HasFocusPair
            ? $"intra {IntraFocusPosition} · Fokus {FocusCenterPosition} · extra {ExtraFocusPosition}"
            : $"Fokus {FocusCenterPosition} gemerkt — Defokus-Betrag (Schritte) eintragen.";

    public bool CanMarkFocusCenter => IsFocuserConnected && !IsAutoFindDefocusRunning;

    [RelayCommand(CanExecute = nameof(CanMarkFocusCenter))]
    private void MarkFocusCenter()
    {
        if (_focuser is not { } f) return;
        FocusCenterPosition = f.Position;
        StatusText = $"Fokus-Mitte gemerkt: Position {FocusCenterPosition}.";
    }

    public bool CanGoToIntraFocus => CanMoveFocuser && HasFocusPair;
    public bool CanGoToExtraFocus => CanMoveFocuser && HasFocusPair;

    [RelayCommand(CanExecute = nameof(CanGoToIntraFocus))]
    private Task GoToIntraFocus() => GoToFocusPairPositionAsync(IntraFocusPosition, "intrafokal");

    [RelayCommand(CanExecute = nameof(CanGoToExtraFocus))]
    private Task GoToExtraFocus() => GoToFocusPairPositionAsync(ExtraFocusPosition, "extrafokal");

    // Prüft VOR dem Fahrbefehl gegen den Fahrbereich (statt stillschweigend zu
    // klemmen) — eine verschobene Fokus-Mitte oder ein zu großer Defokus-Betrag
    // soll eine klare Meldung geben, kein unerwartetes Anfahren einer anderen
    // als der eingetragenen Position.
    private async Task GoToFocusPairPositionAsync(int target, string label)
    {
        if (_focuser is not { } f) return;
        if (!FocusPairModel.IsWithinRange(target, _focuserMaxStep))
        {
            StatusText = $"Fokus {label}: Zielposition {target} liegt außerhalb des Fahrbereichs "
                + $"0..{_focuserMaxStep} — Defokus-Betrag verringern.";
            return;
        }
        await RunFocuserMoveAsync(f, () => f.MoveTo(target));
        StatusText = $"Fokuser auf {label} gefahren (Position {target}).";
    }

    // --- Defokus automatisch suchen -------------------------------------------
    // Startschrittweite bewusst unabhängig von FocuserStepSize gewählt: dieses
    // Feld dient der manuellen Grob-/Feinjustage (Voreinstellungen 10/100/1000)
    // und kann z. B. auf 1000 stehen, wenn zuletzt grob verfahren wurde — für
    // die automatische Suche wäre das ein zu grobes Raster, das das schmale
    // Zielband leicht überspringt. 50 Schritte treffen es bei den bisher
    // vermessenen Fokusern innerhalb weniger Iterationen, ohne es in einem
    // einzigen Sprung zu überspringen.
    private const int AutoFindDefocusStartStep = 50;
    private const int AutoFindDefocusMaxSteps = 15;

    // Zielband deckt sich bewusst mit StarTestQualityHint (dort abgelesen,
    // nicht neu erfunden): OuterRadius < 30 px = zu klein/unempfindlich,
    // > 150 px = evtl. Feldrand/Außenfit unsicher.
    private const double AutoFindDefocusMinRadius = 30;
    private const double AutoFindDefocusMaxRadius = 150;

    // Ab dieser relativen Abweichung zwischen intra- und extrafokalem Radius
    // wird zusätzlich gewarnt (z. B. Stern nicht zentriert, Feldrand-Effekte) —
    // der gefundene Defokus-Betrag wird trotzdem übernommen (siehe Auftrag).
    private const double AutoFindDefocusAsymmetryWarnFraction = 0.30;

    private CancellationTokenSource? _autoFindDefocusCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAutoFindDefocusPanel))]
    [NotifyCanExecuteChangedFor(nameof(FocuserInCommand))]
    [NotifyCanExecuteChangedFor(nameof(FocuserOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(FocuserGoToCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkFocusCenterCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToIntraFocusCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToExtraFocusCommand))]
    [NotifyCanExecuteChangedFor(nameof(AutoFindDefocusCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelAutoFindDefocusCommand))]
    private bool _isAutoFindDefocusRunning;

    // Live-Fortschritt — wird während des Laufs laufend aktualisiert (siehe
    // Ergänzung: eine flüchtige StatusText-Zeile allein reicht nicht).
    [ObservableProperty] private string _autoFindDefocusPhaseText = "";
    [ObservableProperty] private string _autoFindDefocusStepText = "";
    [ObservableProperty] private string _autoFindDefocusMeasurementText = "";

    // Bleibt NACH dem Lauf stehen (Erfolg wie Fehlschlag) — Ergebnis soll
    // dauerhaft sichtbar bleiben, nicht nur als vorbeihuschende Statuszeile.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAutoFindDefocusPanel))]
    private string _autoFindDefocusResultText = "";

    /// <summary>Zeigt den Fortschritts-/Ergebnis-Block: während des Laufs immer,
    /// danach weiter, solange ein Ergebnistext steht (bis zum nächsten Lauf).</summary>
    public bool ShowAutoFindDefocusPanel =>
        IsAutoFindDefocusRunning || !string.IsNullOrEmpty(AutoFindDefocusResultText);

    public string AutoFindDefocusTargetText =>
        $"Ziel: Außenradius {AutoFindDefocusMinRadius:0}-{AutoFindDefocusMaxRadius:0} px "
        + "(siehe Donut-Hinweis)";

    public bool CanAutoFindDefocus =>
        IsFocuserConnected && !IsFocuserMoving && !IsAutoFindDefocusRunning && FocusCenterPosition >= 0;

    [RelayCommand(CanExecute = nameof(CanAutoFindDefocus))]
    private async Task AutoFindDefocus()
    {
        if (_focuser is not { } f) return;
        // Nur mit Live-Quelle sinnvoll — Datei/Ordner liefern keine Einzelbilder
        // auf Abruf. CanExecute bleibt bewusst unabhängig davon (siehe oben),
        // damit der Nutzer eine erklärende Meldung statt eines nur gesperrten
        // Buttons sieht.
        if (!IsStarAlpacaSource && !IsStarAsiSource)
        {
            StatusText = "Defokus-Suche braucht eine Live-Quelle (Alpaca/ASI) — bei Datei/Ordner "
                + "kein automatischer Lauf möglich.";
            return;
        }
        if ((IsStarAlpacaSource && _alpaca is null) || (IsStarAsiSource && _asi is null))
        {
            StatusText = "Defokus-Suche: erst die Live-Kamera verbinden.";
            return;
        }
        var captureFrame = IsStarAlpacaSource
            ? (Func<Task<bool>>)CaptureAlpacaFrameAsync
            : CaptureAsiFrameAsync;

        var cts = new CancellationTokenSource();
        _autoFindDefocusCts = cts;
        var ct = cts.Token;
        var center = FocusCenterPosition;

        EnterBusy();
        IsAutoFindDefocusRunning = true;
        AutoFindDefocusResultText = "";
        AutoFindDefocusMeasurementText = "";
        AutoFindDefocusStepText = $"Schritt 0 von max. {AutoFindDefocusMaxSteps}";
        AutoFindDefocusPhaseText = "Suche startet …";
        try
        {
            var found = await SearchIntraDefocusAsync(f, center, captureFrame, ct);
            if (found is int steps)
            {
                var intraRadius = _donut?.OuterRadius ?? 0;
                DefocusSteps = steps;
                await VerifyOppositeSideAsync(f, center, steps, intraRadius, captureFrame, ct);
            }
            else
            {
                AutoFindDefocusResultText = "Keine passende Defokus-Position im Zielband gefunden "
                    + $"(max. {AutoFindDefocusMaxSteps} Schritte oder Fahrbereich erreicht).";
                StatusText = "Defokus-Suche erfolglos — Fokuser fährt zurück auf die Fokus-Mitte.";
            }
        }
        catch (OperationCanceledException)
        {
            AutoFindDefocusResultText = "Defokus-Suche abgebrochen.";
            StatusText = "Defokus-Suche abgebrochen — Fokuser fährt zurück auf die Fokus-Mitte.";
        }
        finally
        {
            // Immer zurück auf die Fokus-Mitte — Erfolg, Fehlschlag und Abbruch
            // sollen den Nutzer nie an einer unbekannten Position zurücklassen.
            // Mit CancellationToken.None (Default), da ct hier bereits
            // abgebrochen sein kann.
            AutoFindDefocusPhaseText = $"Fahre zurück auf Fokus-Mitte {center} …";
            await RunFocuserMoveAsync(f, () => f.MoveTo(center));
            IsAutoFindDefocusRunning = false;
            ExitBusy();
            _autoFindDefocusCts = null;
            cts.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(IsAutoFindDefocusRunning))]
    private void CancelAutoFindDefocus() => _autoFindDefocusCts?.Cancel();

    // Sucht vom Center ausgehend in eine feste Richtung (hier: intrafokal,
    // Center minus wachsendem Defokus) — welche physische Fahrtrichtung das
    // ist, hängt vom Gerät ab, die Benennung selbst ist eine reine
    // Konvention dieser Suche. Der Defokus-Betrag gilt danach für BEIDE
    // Seiten (siehe FocusPairModel); die Gegenprobe fährt bewusst die andere
    // (extrafokale) Seite an.
    private async Task<int?> SearchIntraDefocusAsync(
        AlpacaFocuserClient f, int center, Func<Task<bool>> captureFrame, CancellationToken ct)
    {
        for (var step = 1; step <= AutoFindDefocusMaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            var defocus = step * AutoFindDefocusStartStep;
            var target = FocusPairModel.IntraFocusPosition(center, defocus);
            if (!FocusPairModel.IsWithinRange(target, _focuserMaxStep))
            {
                AutoFindDefocusPhaseText = "Fahrbereich erreicht — Suche abgebrochen.";
                return null;
            }

            AutoFindDefocusStepText = $"Schritt {step} von max. {AutoFindDefocusMaxSteps}";
            AutoFindDefocusPhaseText = $"Fahre auf Position {target} …";
            await RunFocuserMoveAsync(f, () => f.MoveTo(target), ct);

            AutoFindDefocusPhaseText = "Belichte …";
            var ok = await captureFrame();
            ct.ThrowIfCancellationRequested();

            AutoFindDefocusPhaseText = "Messe Donut …";
            if (!ok || _donut is not { } d)
            {
                AutoFindDefocusMeasurementText = "Kein Donut erkannt — weiter defokussieren.";
                continue;
            }

            AutoFindDefocusMeasurementText = DescribeDefocusRadius(d.OuterRadius);
            if (d.OuterRadius >= AutoFindDefocusMinRadius && d.OuterRadius <= AutoFindDefocusMaxRadius)
                return defocus;
        }
        return null;
    }

    private static string DescribeDefocusRadius(double radius) =>
        radius < AutoFindDefocusMinRadius
            ? $"Radius {radius:0} px — noch zu klein, weiter defokussieren."
            : radius > AutoFindDefocusMaxRadius
                ? $"Radius {radius:0} px — schon zu groß für das Zielband."
                : $"Radius {radius:0} px — im Zielband ({AutoFindDefocusMinRadius:0}-{AutoFindDefocusMaxRadius:0} px).";

    // Gegenprobe auf der jeweils anderen Seite (siehe SearchIntraDefocusAsync):
    // fährt die extrafokale Position an, misst den Radius und vergleicht ihn
    // mit dem intrafokalen Wert. Bei starker Abweichung wird gewarnt, der
    // gefundene Defokus-Betrag bleibt trotzdem gesetzt (siehe Auftrag).
    private async Task VerifyOppositeSideAsync(
        AlpacaFocuserClient f, int center, int defocusSteps, double intraRadius,
        Func<Task<bool>> captureFrame, CancellationToken ct)
    {
        var target = FocusPairModel.ExtraFocusPosition(center, defocusSteps);
        if (!FocusPairModel.IsWithinRange(target, _focuserMaxStep))
        {
            AutoFindDefocusResultText = $"Defokus {defocusSteps} Schritte — intra R={intraRadius:0} px. "
                + "Extra-Gegenprobe übersprungen (Position außerhalb des Fahrbereichs).";
            return;
        }

        AutoFindDefocusPhaseText = $"Gegenprobe: fahre auf Extra-Position {target} …";
        await RunFocuserMoveAsync(f, () => f.MoveTo(target), ct);
        AutoFindDefocusPhaseText = "Gegenprobe: belichte …";
        var ok = await captureFrame();
        ct.ThrowIfCancellationRequested();

        if (!ok || _donut is not { } d)
        {
            AutoFindDefocusResultText = $"Defokus {defocusSteps} Schritte — intra R={intraRadius:0} px, "
                + "Extra-Gegenprobe ohne erkannten Donut.";
            return;
        }

        var extraRadius = d.OuterRadius;
        var deviation = intraRadius > 0 ? Math.Abs(extraRadius - intraRadius) / intraRadius : 0;
        var warn = deviation > AutoFindDefocusAsymmetryWarnFraction
            ? $" ⚠ weicht {deviation * 100:0}% ab — Stern zentriert? Wert wird trotzdem übernommen."
            : "";
        AutoFindDefocusResultText = $"Defokus {defocusSteps} Schritte gefunden — "
            + $"intra R={intraRadius:0} px · extra R={extraRadius:0} px.{warn}";
        StatusText = $"Defokus-Suche abgeschlossen: {defocusSteps} Schritte "
            + $"(intra {intraRadius:0} px / extra {extraRadius:0} px).";
    }

    // --- FileWatcher: Ordner auf neue Aufnahmen überwachen --------------------
    private FileSystemWatcher? _starWatcher;
    private long _watchLoadToken;
    private string? _watchFolder;

    [ObservableProperty] private string _watchedFolderText = "Kein Ordner gewählt.";

    // Zuletzt überwachter Ordner — wird in window-state.json persistiert und beim
    // nächsten Start als Default gesetzt.
    public string? LastWatchFolder => _watchFolder;

    public void SetRememberedWatchFolder(string? folder)
    {
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            _watchFolder = folder;
            WatchedFolderText = $"Zuletzt überwacht: {folder}";
        }
    }

    private static bool IsStarImageFile(string path)
    {
        var e = Path.GetExtension(path).ToLowerInvariant();
        return e is ".fits" or ".fit" or ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff";
    }

    // Vom Code-Behind nach dem Ordner-Dialog aufgerufen (UI-Thread).
    public void StartStarWatch(string folder)
    {
        StopStarWatch();
        if (!Directory.Exists(folder))
        {
            StatusText = $"Ordner nicht gefunden: {folder}";
            return;
        }

        var w = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        w.Created += OnWatchedFileEvent;
        w.Renamed += OnWatchedFileEvent;
        w.EnableRaisingEvents = true;
        _starWatcher = w;
        _watchFolder = folder; // für Persistenz/Default beim nächsten Start
        WatchedFolderText = $"Überwache: {folder}\nWartet auf neue Aufnahmen …";
        StatusText = WatchedFolderText.Replace('\n', ' ');

        // Die neueste vorhandene Aufnahme gleich anzeigen.
        try
        {
            var newest = Directory.EnumerateFiles(folder)
                .Where(IsStarImageFile)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
            if (newest is not null) _ = LoadStarTestFrameAsync(newest);
        }
        catch { /* Erst-Anzeige ist optional */ }
    }

    private void StopStarWatch()
    {
        if (_starWatcher is { } w)
        {
            w.EnableRaisingEvents = false;
            w.Created -= OnWatchedFileEvent;
            w.Renamed -= OnWatchedFileEvent;
            w.Dispose();
            _starWatcher = null;
        }
    }

    private void OnWatchedFileEvent(object sender, FileSystemEventArgs e)
    {
        if (!IsStarImageFile(e.FullPath)) return;
        // Mehrere Events / schnell aufeinanderfolgende Dateien: nur die jeweils
        // neueste tatsächlich laden (ältere Tokens verwerfen).
        var token = System.Threading.Interlocked.Increment(ref _watchLoadToken);
        _ = Task.Run(async () =>
        {
            if (!await WaitUntilReadableAsync(e.FullPath)) return;
            if (System.Threading.Interlocked.Read(ref _watchLoadToken) != token) return;
            await Dispatcher.UIThread.InvokeAsync(() => LoadStarTestFrameAsync(e.FullPath));
        });
    }

    // Wartet, bis die Datei vollständig geschrieben ist (Größe stabil + exklusiv
    // lesbar öffenbar) — Aufnahmesoftware schreibt das FITS sonst noch.
    private static async Task<bool> WaitUntilReadableAsync(string path)
    {
        long lastLen = -1;
        for (int i = 0; i < 40; i++)
        {
            try
            {
                var len = new FileInfo(path).Length;
                if (len > 0 && len == lastLen)
                {
                    using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return true;
                }
                lastLen = len;
            }
            catch { /* noch in Arbeit */ }
            await Task.Delay(150);
        }
        return false;
    }

    // --- Sterntest-Schrauben (Hauptspiegel-Tilt, entkoppelt von den OCAL-Phasen) -
    // Eigener 3-Schrauben-Satz mit eigenen Effekt-Vektoren (andere Kamera/Skala/
    // Defokus als OCAL), persistiert unter screws-startest.json.
    private const string StarScrewKey = "startest";
    private ScrewSet? _starScrews;
    private string? _calibratingStarScrew;
    private double _starCalibBaseX, _starCalibBaseY;
    private string? _starLastPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStarScrewIdle))]
    [NotifyPropertyChangedFor(nameof(ShowStarDiagram))]
    [NotifyPropertyChangedFor(nameof(IsStarCollimationDone))]
    private bool _isStarScrewCalibrating;
    public bool IsStarScrewIdle => !IsStarScrewCalibrating;

    [ObservableProperty] private string _starCalibrationInstruction = "";

    private static ScrewSet StarTestDefaultScrews() => new(new[]
    {
        Screw.Untrained("Hauptspiegel 1", 1),
        Screw.Untrained("Hauptspiegel 2", 1),
        Screw.Untrained("Hauptspiegel 3", 1),
    });

    private ScrewSet StarScrews
    {
        get
        {
            if (_starScrews is null)
            {
                var path = _screwStore.GetPathFor(StarScrewKey);
                _starScrews = File.Exists(path) && _screwStore.Load(StarScrewKey) is { } s && s.Screws.Count == 3
                    ? _screwStore.Load(StarScrewKey)
                    : StarTestDefaultScrews();
            }
            return _starScrews;
        }
    }

    public bool StarScrewsFullyCalibrated => StarScrews.Screws.All(s => s.IsCalibrated);
    public bool ShowStarScrewGate => !StarScrewsFullyCalibrated;
    public string StarScrewGateText =>
        "Alle 3 Hauptspiegel-Schrauben kalibrieren: ‚Kalibrieren' bei einer Schraube "
        + "starten, etwas drehen, neues Bild laden — dann erscheinen Drehempfehlungen.";

    // Entscheidungs-Banner „Schrauben-Kalibrierung verwenden vs. neu bestimmen":
    // beim Eintritt in den Sterntest wird eine vollständig kalibrierte
    // Schraubenmenge weiterhin still übernommen (bisheriger Default) — der
    // Banner macht die Entscheidung nur sichtbar.
    [ObservableProperty]
    private bool _showStarScrewDecision;

    public string StarScrewDecisionText
    {
        get
        {
            var timestamps = StarScrews.Screws
                .Where(s => s.CalibratedAt is not null)
                .Select(s => s.CalibratedAt!.Value)
                .ToList();
            var when = timestamps.Count > 0
                ? timestamps.Max().LocalDateTime.ToString("dd.MM.yyyy")
                : "unbekannt";
            return $"Schrauben-Kalibrierung vorhanden (zuletzt {when}).";
        }
    }

    [RelayCommand]
    private void UseExistingStarScrewCalibration()
    {
        ShowStarScrewDecision = false;
        StatusText = "Vorhandene Schrauben-Kalibrierung wird verwendet.";
    }

    [RelayCommand]
    private void RecalibrateStarScrews()
    {
        ShowStarScrewDecision = false;
        if (StarScrews.Screws.FirstOrDefault() is { } first)
        {
            StartStarScrewCalibration(first.Name);
        }
    }

    // --- Teleskop-Typ (Newton vs. RC/SC) --------------------------------------
    // Bestimmt, ob der Versatz Obstruktion↔Scheibchen in EINEM Bild ein gültiges
    // Kollimationsmaß ist (RC/SC: Fangspiegel sitzt zentrisch) oder ob dafür erst
    // der Vergleich eines intra-/extrafokalen Paars nötig ist (Newton: Fangspiegel
    // sitzt konstruktiv versetzt, siehe CollimationPair). Default Newton — die weit
    // häufigere Bauart bei visuellen/fotografischen Amateur-Teleskopen.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewtonTelescope))]
    [NotifyPropertyChangedFor(nameof(IsRcOrScTelescope))]
    [NotifyPropertyChangedFor(nameof(GuideText))]
    [NotifyPropertyChangedFor(nameof(StarOverlayExplanationText))]
    private TelescopeType _telescopeType = TelescopeType.Newton;

    public bool IsNewtonTelescope
    {
        get => TelescopeType == TelescopeType.Newton;
        set { if (value) TelescopeType = TelescopeType.Newton; }
    }

    public bool IsRcOrScTelescope
    {
        get => TelescopeType == TelescopeType.RcOrSc;
        set { if (value) TelescopeType = TelescopeType.RcOrSc; }
    }

    // --- Paar-Messung: intra-/extrafokale Gegenprobe (Newton) -----------------
    // Zwei Messplätze (A/B) mit je einer Donut-Messung + Fokus-Seite (Vorzeichen
    // reicht, siehe CollimationPair.Evaluate) — befüllbar live (Fokuser fährt
    // beide Positionen an) oder aus Dateien (Fokuser-Position aus dem FITS-Header
    // bzw. manuelle Zuordnung, wenn der Header nichts liefert).
    private sealed record PairSlot(
        DonutResult Donut, int FocusOffsetSteps, string SourceLabel, DateTimeOffset CapturedAt);

    private PairSlot? _pairSlotA;
    private PairSlot? _pairSlotB;

    public bool HasPairSlotA => _pairSlotA is not null;
    public bool HasPairSlotB => _pairSlotB is not null;

    private static string PairSideLabel(int offsetSteps) => offsetSteps < 0 ? "intrafokal" : "extrafokal";

    private static string PairBrightnessInfo(DonutResult d) =>
        $"Info: Helligkeits-Ungleichmäßigkeit {d.BrightnessImbalance * 100:0} % "
        + $"Richtung {OffsetArrow(d.BrightnessDarkDirection.X, d.BrightnessDarkDirection.Y)} "
        + $"({Math.Atan2(d.BrightnessDarkDirection.Y, d.BrightnessDarkDirection.X) * 180.0 / Math.PI:0}°) "
        + "— fließt NICHT in die Bewertung ein (siehe Hinweis).";

    public string PairSlotAText => _pairSlotA is { } a
        ? $"A: {PairSideLabel(a.FocusOffsetSteps)} · R={a.Donut.OuterRadius:0} px · "
          + $"{a.SourceLabel} ({a.CapturedAt:HH:mm:ss})\n"
          + $"    {PairBrightnessInfo(a.Donut)}"
        : "A: noch keine Aufnahme.";

    public string PairSlotBText => _pairSlotB is { } b
        ? $"B: {PairSideLabel(b.FocusOffsetSteps)} · R={b.Donut.OuterRadius:0} px · "
          + $"{b.SourceLabel} ({b.CapturedAt:HH:mm:ss})\n"
          + $"    {PairBrightnessInfo(b.Donut)}"
        : "B: noch keine Aufnahme.";

    // Auswertung des aktuellen Paares — null, solange nicht beide Messplätze
    // befüllt sind. IsEvaluable kann trotzdem false sein (siehe CollimationPair),
    // was bei den festen Vorzeichen unten (A immer negativ, B immer positiv)
    // praktisch nicht vorkommt, aber sicherheitshalber weiter geprüft wird.
    private CollimationPairResult? CurrentPairResult =>
        _pairSlotA is { } a && _pairSlotB is { } b
            ? CollimationPair.Evaluate(a.Donut, a.FocusOffsetSteps, b.Donut, b.FocusOffsetSteps)
            : null;

    public bool ShowStarTestUnequalDefocusWarning =>
        CurrentPairResult is { IsEvaluable: true, UnequalDefocusWarning: true };

    public string StarTestUnequalDefocusWarningText =>
        CurrentPairResult is { IsEvaluable: true } pair && _pairSlotA is { } a && _pairSlotB is { } b
            ? $"⚠ Defokus-Beträge zu ungleich (Radius A {a.Donut.OuterRadius:0} px vs. "
              + $"B {b.Donut.OuterRadius:0} px, Verhältnis {pair.RadiusRatio:0.00}) — Ergebnis unsicherer."
            : "";

    private void SetPairSlotA(DonutResult donut, int focusOffsetSteps, string sourceLabel)
    {
        _pairSlotA = new PairSlot(donut, focusOffsetSteps, sourceLabel, DateTimeOffset.Now);
        NotifyStarTestReadouts();
    }

    private void SetPairSlotB(DonutResult donut, int focusOffsetSteps, string sourceLabel)
    {
        _pairSlotB = new PairSlot(donut, focusOffsetSteps, sourceLabel, DateTimeOffset.Now);
        NotifyStarTestReadouts();
    }

    // Nur das Vorzeichen von focusOffsetSteps geht in die Auswertung ein (siehe
    // CollimationPair.Evaluate) — die manuelle Zuordnung braucht deshalb keine
    // echte Fokuser-Position, ein reiner Seiten-Sentinel (-1/+1) reicht.
    public bool CanAssignPairSlot => _donut is not null;

    [RelayCommand(CanExecute = nameof(CanAssignPairSlot))]
    private void AssignCurrentAsPairA()
    {
        if (_donut is not { } d) return;
        SetPairSlotA(d, -1, "manuell zugeordnet");
        StatusText = "Aktuelles Bild als Aufnahme A (intrafokal) übernommen.";
    }

    [RelayCommand(CanExecute = nameof(CanAssignPairSlot))]
    private void AssignCurrentAsPairB()
    {
        if (_donut is not { } d) return;
        SetPairSlotB(d, 1, "manuell zugeordnet");
        StatusText = "Aktuelles Bild als Aufnahme B (extrafokal) übernommen.";
    }

    [RelayCommand]
    private void ResetPair()
    {
        _pairSlotA = null;
        _pairSlotB = null;
        NotifyStarTestReadouts();
        StatusText = "Paar-Messung zurückgesetzt.";
    }

    // Datei-Weg: Fokuser-Position aus dem FITS-Header lesen und, sofern eine
    // Fokus-Mitte gemerkt ist, automatisch dem passenden Messplatz zuordnen.
    // Liefert der Header nichts (kein FITS, kein Keyword) oder ist keine
    // Fokus-Mitte gemerkt, bleibt es beim manuellen Weg (als A/B übernehmen).
    private void TryAutoAssignPairSlot(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".fits" or ".fit")) return;
        if (FocusCenterPosition < 0 || _donut is not { } d) return;
        if (FitsReader.GetFocuserPosition(path) is not { } pos) return;
        var offset = pos - FocusCenterPosition;
        if (offset == 0) return; // im Fokus — keiner Seite zuordenbar
        var label = $"Datei (Fokuser {pos})";
        if (offset < 0) SetPairSlotA(d, offset, label);
        else SetPairSlotB(d, offset, label);
    }

    // --- Paar-Messung live: Fokuser fährt beide Positionen automatisch an ----
    private CancellationTokenSource? _pairMeasurementCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MeasurePairCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelPairMeasurementCommand))]
    private bool _isPairMeasurementRunning;

    [ObservableProperty] private string _pairMeasurementPhaseText = "";
    [ObservableProperty] private string _pairMeasurementResultText = "";

    public bool ShowPairMeasurementPanel =>
        IsPairMeasurementRunning || !string.IsNullOrEmpty(PairMeasurementResultText);

    public bool CanMeasurePair =>
        IsFocuserConnected && !IsFocuserMoving && !IsPairMeasurementRunning && HasFocusPair;

    [RelayCommand(CanExecute = nameof(CanMeasurePair))]
    private async Task MeasurePair()
    {
        if (_focuser is not { } f) return;
        if (!IsStarAlpacaSource && !IsStarAsiSource)
        {
            StatusText = "Paar-Messung braucht eine Live-Quelle (Alpaca/ASI) — bei Datei/Ordner "
                + "die Aufnahmen einzeln laden und per 'als A/B übernehmen' zuordnen.";
            return;
        }
        if ((IsStarAlpacaSource && _alpaca is null) || (IsStarAsiSource && _asi is null))
        {
            StatusText = "Paar-Messung: erst die Live-Kamera verbinden.";
            return;
        }
        var captureFrame = IsStarAlpacaSource
            ? (Func<Task<bool>>)CaptureAlpacaFrameAsync
            : CaptureAsiFrameAsync;

        var cts = new CancellationTokenSource();
        _pairMeasurementCts = cts;
        var ct = cts.Token;
        var center = FocusCenterPosition;
        var defocus = DefocusSteps;

        EnterBusy();
        IsPairMeasurementRunning = true;
        PairMeasurementResultText = "";
        PairMeasurementPhaseText = "Paar-Messung startet …";
        try
        {
            if (!await CapturePairSideAsync(f, center, -defocus, captureFrame, "A (intrafokal)", SetPairSlotA, ct))
                return;
            if (!await CapturePairSideAsync(f, center, defocus, captureFrame, "B (extrafokal)", SetPairSlotB, ct))
                return;
            PairMeasurementResultText = "Paar-Messung abgeschlossen — Auswertung siehe Readout oben.";
            StatusText = "Paar-Messung abgeschlossen.";
        }
        catch (OperationCanceledException)
        {
            PairMeasurementResultText = "Paar-Messung abgebrochen.";
            StatusText = "Paar-Messung abgebrochen — Fokuser fährt zurück auf die Fokus-Mitte.";
        }
        finally
        {
            PairMeasurementPhaseText = $"Fahre zurück auf Fokus-Mitte {center} …";
            await RunFocuserMoveAsync(f, () => f.MoveTo(center));
            IsPairMeasurementRunning = false;
            ExitBusy();
            _pairMeasurementCts = null;
            cts.Dispose();
        }
    }

    // Fährt eine Fokus-Paar-Seite an, belichtet und übernimmt das Ergebnis in den
    // übergebenen Messplatz. false = abgebrochen (Fahrbereich/kein Donut) — der
    // Aufrufer beendet die Messung dann mit dem bereits gesetzten Ergebnistext.
    private async Task<bool> CapturePairSideAsync(
        AlpacaFocuserClient f, int center, int offsetSteps, Func<Task<bool>> captureFrame,
        string sideLabel, Action<DonutResult, int, string> setSlot, CancellationToken ct)
    {
        var target = center + offsetSteps;
        if (!FocusPairModel.IsWithinRange(target, _focuserMaxStep))
        {
            PairMeasurementResultText = $"Aufnahme {sideLabel}: Zielposition {target} außerhalb des Fahrbereichs.";
            return false;
        }
        PairMeasurementPhaseText = $"Fahre auf Position {target} ({sideLabel}) …";
        await RunFocuserMoveAsync(f, () => f.MoveTo(target), ct);
        PairMeasurementPhaseText = $"Belichte Aufnahme {sideLabel} …";
        var ok = await captureFrame();
        ct.ThrowIfCancellationRequested();
        if (!ok || _donut is not { } d)
        {
            PairMeasurementResultText = $"Aufnahme {sideLabel}: kein Donut erkannt — Paar-Messung abgebrochen.";
            return false;
        }
        setSlot(d, offsetSteps, $"Live {sideLabel}");
        return true;
    }

    [RelayCommand(CanExecute = nameof(IsPairMeasurementRunning))]
    private void CancelPairMeasurement() => _pairMeasurementCts?.Cancel();

    // Ziel-Vektor für die Schrauben-Drehempfehlung: beim Newton nur mit gültigem
    // Paar (siehe CollimationPair — der Rest-Versatz im Einzelbild ist dort kein
    // Kollimationsmaß), bei RC/SC unverändert der rohe Einzelbild-Versatz.
    private Point2f? NewtonRecommendationTarget() =>
        TelescopeType == TelescopeType.RcOrSc
            ? (_donut is { } d ? d.Offset : (Point2f?)null)
            : (CurrentPairResult is { IsEvaluable: true } pair ? pair.ErrorPixels : (Point2f?)null);

    // Drehempfehlung pro Schraube: Versatz (Obstruktion→Scheibchen bzw. beim
    // Newton den paarermittelten Fehler) auf 0 ziehen. Σ=0 (gemeinsamer Offset =
    // reiner Piston, kein Tilt) → nie „alle gleichsinnig".
    private Dictionary<string, double> RecommendedStarTurns()
    {
        var result = new Dictionary<string, double>();
        if (!StarScrewsFullyCalibrated || NewtonRecommendationTarget() is not { } target) return result;
        var screws = StarScrews.Screws;
        var turns = ScrewSolver.ComputeTurns(screws, -target.X, -target.Y);
        var mean = turns.Length > 0 ? turns.Average() : 0;
        for (int i = 0; i < screws.Count; i++) result[screws[i].Name] = turns[i] - mean;
        return result;
    }

    public IReadOnlyList<ScrewViewModel> StarScrewVms
    {
        get
        {
            var rec = RecommendedStarTurns();
            return StarScrews.Screws.Select(s => new ScrewViewModel(
                s, rec.TryGetValue(s.Name, out var t) ? t : (double?)null)
            {
                OnCalibrateRequested = vm => StartStarScrewCalibration(vm.Name),
            }).ToList();
        }
    }

    // Ziel erreicht: Donut erkannt, alle Schrauben kalibriert, alle Empfehlungen ≈ 0
    // (gleiche 0,02-Umdr-Schwelle wie die ✓-Labels). Dann fehlt am Teleskop noch der
    // letzte Handgriff: Arretierung anziehen und per Gegenkontrolle sichern.
    public bool IsStarCollimationDone
    {
        get
        {
            if (!StarScrewsFullyCalibrated || IsStarScrewCalibrating) return false;
            if (NewtonRecommendationTarget() is null) return false;
            var rec = RecommendedStarTurns();
            return rec.Count > 0 && rec.Values.All(t => Math.Abs(t) < 0.02);
        }
    }

    // --- Sterntest-HS-Diagramm (Skizze + Drehpfeile, wie OCAL-Phase 3) --------
    // 3 Hauptspiegel-Schrauben 120° versetzt auf dem 200er-Overlay; ohne OAZ-
    // Rotation (im Sterntest gibt es keine OAZ-Orientierung). Schraube 1 unten
    // (180°), 2/3 im Uhrzeigersinn — gleiche Konvention wie die OCAL-Skizze.
    private System.Collections.Generic.List<ScrewLayout> StarScrewLayout()
    {
        var screws = StarScrews.Screws;
        var result = new System.Collections.Generic.List<ScrewLayout>(screws.Count);
        double cx = 100, cy = 100, dot = 22, radius = PrimaryScrewR200, baseAngle = 180, step = 120;
        for (var i = 0; i < screws.Count; i++)
        {
            var a = (baseAngle + i * step) * Math.PI / 180.0;
            result.Add(new ScrewLayout(screws[i], cx + radius * Math.Sin(a), cy - radius * Math.Cos(a), dot, i));
        }
        return result;
    }

    private Avalonia.Media.Imaging.Bitmap? _starSketch;
    public Avalonia.Media.Imaging.Bitmap? StarSketch => _starSketch ??= LoadPhaseSketch(3);
    public bool HasStarSketch => StarSketch is not null;

    // Diagramm erst zeigen, wenn alle Schrauben kalibriert sind UND eine
    // Empfehlung vorliegt (Donut erkannt). NICHT während einer Kalibrierung —
    // dort liegt (noch) keine gültige Berechnung vor.
    public bool ShowStarDiagram =>
        StarScrewsFullyCalibrated && !IsStarScrewCalibrating && NewtonRecommendationTarget() is not null;

    public System.Collections.Generic.IReadOnlyList<ScrewMarkerVm> StarScrewMarkers
    {
        get
        {
            var list = new System.Collections.Generic.List<ScrewMarkerVm>();
            foreach (var l in StarScrewLayout())
                list.Add(new ScrewMarkerVm
                {
                    Diameter = l.Dot,
                    Left = l.X - l.Dot / 2,
                    Top = l.Y - l.Dot / 2,
                    Label = MarkerLabel(l.Screw),
                    IsActive = false,
                    IsCalibrated = l.Screw.IsCalibrated,
                });
            return list;
        }
    }

    public Avalonia.Media.Geometry? StarArrowsGeometry
    {
        get
        {
            var turns = RecommendedStarTurns();
            if (turns.Count == 0) return null;
            var sb = new System.Text.StringBuilder();
            foreach (var l in StarScrewLayout())
                if (turns.TryGetValue(l.Screw.Name, out var t) && Math.Abs(t) >= 0.02)
                    AppendTurnArrow(sb, l.X, l.Y, l.Dot / 2 + 7, t);
            return sb.Length == 0 ? null : Avalonia.Media.Geometry.Parse(sb.ToString());
        }
    }

    public System.Collections.Generic.IReadOnlyList<TurnLabelVm> StarTurnLabels
    {
        get
        {
            var list = new System.Collections.Generic.List<TurnLabelVm>();
            var turns = RecommendedStarTurns();
            if (turns.Count == 0) return list;
            const double canvas = 200, cx = 100, cy = 100;
            foreach (var l in StarScrewLayout())
            {
                if (!turns.TryGetValue(l.Screw.Name, out var t)) continue;
                var text = Math.Abs(t) < 0.02 ? "✓" : $"{Math.Abs(t):0.0}";
                var dx = l.X - cx; var dy = l.Y - cy;
                var len = Math.Sqrt(dx * dx + dy * dy); if (len < 1) len = 1;
                // Marker liegen am Rand → Text nach innen (wie Phase 3).
                var ox = l.X - dx / len * (l.Dot / 2 + 32);
                var oy = l.Y - dy / len * (l.Dot / 2 + 32);
                list.Add(new TurnLabelVm
                {
                    Left = Math.Clamp(ox - 10, 2, canvas - 24),
                    Top = Math.Clamp(oy - 10, 2, canvas - 20),
                    Text = text,
                });
            }
            return list;
        }
    }

    private void StartStarScrewCalibration(string name)
    {
        if (_donut is not { } d)
        {
            StatusText = "Kalibrieren: erst ein Bild mit erkanntem Donut laden.";
            return;
        }
        _calibratingStarScrew = name;
        _starCalibBaseX = d.Offset.X;
        _starCalibBaseY = d.Offset.Y;
        StarCalibrationInstruction =
            $"Drehe '{name}' — ¼ Umdrehung als Startwert — und lade ein neues Bild. Reicht der "
            + "Versatz nicht, weiter drehen und unten die insgesamt gedrehte Menge eintragen.";
        IsStarScrewCalibrating = true;
    }

    [RelayCommand]
    private void CancelStarScrewCalibration()
    {
        IsStarScrewCalibrating = false;
        _calibratingStarScrew = null;
        StatusText = "Sterntest-Kalibrierung abgebrochen.";
    }

    // Mindest-Versatz seit Baseline, damit eine Kalibrierung zählt — über dem
    // Detektions-Rauschen (~1-2 px), sonst ergäbe sich ein vom Rauschen
    // dominierter Effekt-Vektor und damit Müll-Empfehlungen.
    private const double MinCalibDeltaPx = 2.0;

    private (double Mag, double Dx, double Dy)? StarCalibDelta()
    {
        if (_calibratingStarScrew is null || _donut is not { } d) return null;
        var dx = d.Offset.X - _starCalibBaseX;
        var dy = d.Offset.Y - _starCalibBaseY;
        return (System.Math.Sqrt(dx * dx + dy * dy), dx, dy);
    }

    // Prominente Live-Anzeige in der Kalibrier-Box: gemessener Versatz + ob er für
    // die Kalibrierung ausreicht.
    public string StarCalibDeltaText
    {
        get
        {
            if (StarCalibDelta() is not { } q) return "";
            var state = q.Mag >= MinCalibDeltaPx
                ? "✓ ausreichend"
                : $"⚠ zu klein – mind. {MinCalibDeltaPx:0} px nötig";
            return $"Versatz seit Baseline: Δ {q.Mag:0.0} px  ({q.Dx:+0.0;-0.0}, {q.Dy:+0.0;-0.0})   {state}";
        }
    }

    // Schließt eine laufende Kalibrierung mit dem aktuell geladenen Bild ab, wenn
    // gegenüber der Baseline ein echter Versatz gemessen wurde (sonst ist es noch
    // das Baseline-Bild → weiter warten). Wird beim Laden/Aktualisieren aufgerufen
    // (implizite Bestätigung). Liefert eine Statuszeile oder null.
    private string? TryFinalizeStarCalibration()
    {
        if (_calibratingStarScrew is not { } name) return null;
        if (StarCalibDelta() is not { } q) return null;
        if (q.Mag < MinCalibDeltaPx)
            return $"Kalibriere '{name}': Versatz Δ {q.Mag:0.0} px noch zu klein (≥ {MinCalibDeltaPx:0} px nötig) — Schraube weiter drehen (Gesamtmenge unten eintragen), neues Bild laden.";
        if (ScrewCalibrationMath.ParseTurns(CalibrationTurnsText) is not { } turns)
            return $"Kalibriere '{name}': Drehmenge '{CalibrationTurnsText}' ungültig — z. B. 0,25 / 0,5 / 1 eingeben.";
        var (effectDx, effectDy) = ScrewCalibrationMath.EffectPerTurn(q.Dx, q.Dy, turns, IsScrewCwSelected);
        var sc = StarScrews.Screws.First(s => s.Name == name);
        _starScrews = StarScrews.Replace(sc with
        {
            EffectDx = effectDx,
            EffectDy = effectDy,
            IsCalibrated = true,
            CalibratedAt = DateTimeOffset.Now,
        });
        _screwStore.Save(StarScrewKey, _starScrews);
        _calibratingStarScrew = null;
        IsStarScrewCalibrating = false;
        return $"Schraube '{name}' kalibriert ({turns:0.##} Umdr): Δ/Umdr ≈ ({effectDx:0.0}, {effectDy:0.0}) px.";
    }

    // Lädt das zuletzt geladene Bild neu (Refresh). Im FileWatcher-Modus später
    // automatisch; manuell nützlich, wenn die Datei überschrieben wurde. Durchläuft
    // denselben Pfad wie ein normales Laden → implizite Kalibrier-Bestätigung.
    public bool CanRefreshStarFrame => _starLastPath is not null;

    [RelayCommand(CanExecute = nameof(CanRefreshStarFrame))]
    private async Task RefreshStarFrame()
    {
        if (_starLastPath is { } p) await LoadStarTestFrameAsync(p);
        else StatusText = "Aktualisieren: noch kein Bild geladen.";
    }

    public bool StarTestHasDonut => _donut is not null;

    // Quellen-Kennzeichnung: welches Bild wurde zuletzt analysiert und wann —
    // macht sichtbar, ob die Anzeige zu einem neuen Bild gehört oder von einer
    // älteren Aufnahme stammt. In ApplyStarGray gesetzt, beim Verlassen des
    // Sterntest-Modus geleert.
    private string _starFrameSourceText = "";
    public string StarFrameSourceText => _starFrameSourceText;

    // RC/SC: unverändert der rohe Einzelbild-Versatz. Newton: die zweite Zeile
    // zeigt statt des rohen Versatzes den systematischen Anteil (Fangspiegel-
    // Offset) aus der Paar-Auswertung — bzw., ohne gültiges Paar, den Hinweis,
    // dass dafür zwei Aufnahmen beidseits des Fokus nötig sind.
    public string StarTestOffsetText
    {
        get
        {
            if (TelescopeType == TelescopeType.RcOrSc)
            {
                return _donut is { } d
                    ? $"Versatz {d.OffsetMagnitude:0.0} px  "
                      + $"(Δx={d.Offset.X:+0.0;-0.0}, Δy={d.Offset.Y:+0.0;-0.0})  "
                      + $"{OffsetArrow(d.Offset.X, d.Offset.Y)}"
                    : "—";
            }
            return CurrentPairResult switch
            {
                { IsEvaluable: true } pair =>
                    $"Systematischer Anteil (Fangspiegel-Offset): {pair.SystematicPercent:0.0} % vom Radius "
                    + "— Kennzahl des Teleskops, bleibt über Sessions stabil, NICHT wegjustieren.",
                { IsEvaluable: false } notEvaluable => notEvaluable.Reason ?? "",
                _ => "Für den echten Kollimationsfehler zwei Aufnahmen beidseits des Fokus "
                    + "(intra-/extrafokal) nötig — siehe Paar-Messung unten.",
            };
        }
    }

    // RC/SC: unverändert der rohe Einzelbild-Versatz als Kollimationsmaß.
    // Newton: der Rest-Versatz im Einzelbild ist beim Newton KEIN Kollimationsmaß
    // (Fangspiegel sitzt absichtlich versetzt) — erst der Paar-Vergleich zeigt den
    // echten Fehler (ErrorPercent).
    public string StarTestMetricText
    {
        get
        {
            if (TelescopeType == TelescopeType.RcOrSc)
            {
                return _donut is { } d
                    ? $"Kollimation: {d.OffsetMagnitude / Math.Max(d.OuterRadius, 1) * 100:0.0} % vom Radius"
                    : "Kein Donut erkannt";
            }
            return CurrentPairResult is { IsEvaluable: true } pair
                ? $"Kollimationsfehler: {pair.ErrorPercent:0.0} % vom Radius"
                : "Kollimationsfehler: Paar-Messung nötig (siehe unten)";
        }
    }

    public string StarTestGeometryText => _donut is { } d
        ? $"Außen {d.OuterRadius:0} px · Innen {d.InnerRadius:0} px · Obstr {d.Obstruction:0.00}"
        : "—";

    // Overlay-Erklärtext: bei RC/SC ist der gelbe Versatz direkt das
    // Kollimationsmaß (Fangspiegel sitzt zentrisch). Beim Newton sitzt der
    // Fangspiegel konstruktiv versetzt — ein Rest-Versatz in DIESEM Einzelbild
    // ist normal und KEIN Kollimationsmaß; erst der Vergleich beider Fokus-
    // Seiten (Paar-Messung) zeigt den echten Fehler.
    public string StarOverlayExplanationText => TelescopeType == TelescopeType.RcOrSc
        ? "Grün = Scheibchen (SOLL), Rot = Obstruktion (IST), Gelb = Versatz zwischen beiden. "
          + "Je kleiner der gelbe Versatz, desto besser kollimiert.\n"
          + "Anleitung: Stern mittig stellen und mittel defokussieren, bis ein Donut sichtbar "
          + "ist. Dann den Drehempfehlungen folgen, bis der Versatz minimal ist."
        : "Grün = Scheibchen (SOLL), Rot = Obstruktion (IST), Gelb = Versatz zwischen beiden. "
          + "Beim Newton sitzt der Fangspiegel absichtlich versetzt — ein Rest-Versatz in DIESEM "
          + "Einzelbild ist normal und KEIN Kollimationsmaß. Erst der Vergleich beider Fokus-"
          + "Seiten (Paar-Messung unten) zeigt den echten Fehler.\n"
          + "Anleitung: Stern mittig stellen und mittel defokussieren, bis ein Donut sichtbar "
          + "ist. Dann je ein Bild vor dem Fokus (intrafokal) und nach dem Fokus (extrafokal) "
          + "bei gleichem Defokus aufnehmen (Paar-Messung) und den Empfehlungen aus der "
          + "Auswertung folgen.";

    public string StarTestQualityHint
    {
        get
        {
            // Leerzustand (noch kein Bild) ist kein Fehlerfall — je Quelle ein
            // eigener Hinweis, statt der "Kein Donut erkannt"-Fehlermeldung.
            if (_starGray is null)
            {
                if (IsStarWatchSource) return "Wartet auf neue Aufnahmen im überwachten Ordner.";
                if (IsStarAlpacaSource || IsStarAsiSource) return "Noch keine Aufnahme — Einzelbild oder Loop starten.";
                return "Noch kein Bild geladen — ‚Stern-Bild laden (FITS)…' drücken.";
            }
            if (_donut is not { } d) return "Kein Donut erkannt — Stern stärker defokussieren oder Bild prüfen.";
            if (d.OuterRadius < 30) return "Donut klein (nah am Fokus) — wenig empfindlich, etwas mehr defokussieren.";
            if (d.OuterRadius > 150) return "Donut sehr groß — evtl. am Feldrand, Außenfit unsicher. Etwas weniger defokussieren / Stern zentrieren.";
            return "Donut-Größe gut für die Analyse.";
        }
    }

    private static string OffsetArrow(double dx, double dy)
    {
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return "·";
        var a = Math.Atan2(dy, dx) * 180.0 / Math.PI; // y nach unten
        var arrows = new[] { "→", "↘", "↓", "↙", "←", "↖", "↑", "↗" };
        int idx = ((int)Math.Round(a / 45.0) + 8) % 8;
        return arrows[idx];
    }

    private void NotifyStarTestReadouts()
    {
        OnPropertyChanged(nameof(StarTestHasDonut));
        OnPropertyChanged(nameof(StarFrameSourceText));
        OnPropertyChanged(nameof(StarTestOffsetText));
        OnPropertyChanged(nameof(StarTestMetricText));
        OnPropertyChanged(nameof(StarTestGeometryText));
        OnPropertyChanged(nameof(StarTestQualityHint));
        OnPropertyChanged(nameof(StarScrewVms));
        OnPropertyChanged(nameof(StarScrewsFullyCalibrated));
        OnPropertyChanged(nameof(ShowStarScrewGate));
        OnPropertyChanged(nameof(IsStarCollimationDone));
        OnPropertyChanged(nameof(StarCalibDeltaText));
        OnPropertyChanged(nameof(CanRefreshStarFrame));
        RefreshStarFrameCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowStarDiagram));
        OnPropertyChanged(nameof(StarScrewMarkers));
        OnPropertyChanged(nameof(StarArrowsGeometry));
        OnPropertyChanged(nameof(StarTurnLabels));
        // Paar-Messung: Messplätze + Auswertung hängen ebenfalls an _donut
        // (manuelle Zuordnung) bzw. ändern sich, sobald ein Messplatz gesetzt wird.
        OnPropertyChanged(nameof(HasPairSlotA));
        OnPropertyChanged(nameof(HasPairSlotB));
        OnPropertyChanged(nameof(PairSlotAText));
        OnPropertyChanged(nameof(PairSlotBText));
        OnPropertyChanged(nameof(ShowStarTestUnequalDefocusWarning));
        OnPropertyChanged(nameof(StarTestUnequalDefocusWarningText));
        AssignCurrentAsPairACommand.NotifyCanExecuteChanged();
        AssignCurrentAsPairBCommand.NotifyCanExecuteChanged();
    }

    private void RecomputeStarTestOverlay()
    {
        if (!IsStarTestMode || !_displayTransform.IsValid || _donut is not { } d)
        {
            ShowStarTestOverlay = false;
            return;
        }
        var (ox, oy) = _displayTransform.MapToDisplay(d.OuterCenter.X, d.OuterCenter.Y);
        var (ix, iy) = _displayTransform.MapToDisplay(d.InnerCenter.X, d.InnerCenter.Y);
        var ro = _displayTransform.MapLengthToDisplay(d.OuterRadius);
        var ri = _displayTransform.MapLengthToDisplay(d.InnerRadius);
        StarOuterLeft = ox - ro; StarOuterTop = oy - ro; StarOuterSize = ro * 2;
        StarInnerLeft = ix - ri; StarInnerTop = iy - ri; StarInnerSize = ri * 2;
        StarSollPoint = new Avalonia.Point(ox, oy); // SOLL = Scheibchen-Mitte
        StarIstPoint = new Avalonia.Point(ix, iy);  // IST = Obstruktion
        ShowStarTestOverlay = true;
    }

    // Gemeinsamer Verarbeitungspfad für ein aufbereitetes Graustufen-Frame —
    // egal ob aus Datei, FileWatcher oder Alpaca-Kamera: anzeigen, Donut erkennen,
    // auto-zoomen, laufende Kalibrierung implizit abschließen, Anzeigen aktualisieren.
    private void ApplyStarGray(Mat gray8, string label)
    {
        _starGray?.Dispose();
        _starGray = gray8;
        _donut = _detectors.Donut.Detect(gray8);
        SetStarAutoView();
        var calibStatus = TryFinalizeStarCalibration();
        // Quellen-Kennzeichnung: welches Bild wurde gerade analysiert, wann.
        _starFrameSourceText = $"Analysiert: {label} ({DateTime.Now:HH:mm:ss})";
        NotifyStarTestReadouts();
        // Latch NUR anhand einer echten, vollständig kalibrierten Messung setzen
        // (siehe Feldkommentar an StarCollimationAchieved) — kein Donut, keine
        // laufende Kalibrierung oder ein Modus-Wechsel dürfen ihn zurücksetzen.
        var wasAchieved = StarCollimationAchieved;
        var justLost = false;
        if (_donut is not null && StarScrewsFullyCalibrated && !IsStarScrewCalibrating)
        {
            StarCollimationAchieved = IsStarCollimationDone;
            if (StarCollimationAchieved)
            {
                // Ziel (wieder) erreicht → eine frühere Regressions-Meldung entschärfen.
                StarCollimationLost = false;
            }
            else if (wasAchieved)
            {
                // Latch fiel durch die neue Messung von true auf false — das
                // zuvor erreichte Ziel wurde wieder verlassen.
                StarCollimationLost = true;
                justLost = true;
            }
        }
        StatusText = justLost
            ? "⚠ Kollimation wieder außerhalb des Ziels."
            : calibStatus
                ?? (_donut is null
                    ? $"{label} — kein Donut erkannt."
                    : $"{label} — Donut erkannt ({_donut!.OuterRadius:0}/{_donut.InnerRadius:0} px).");
        RefreshWorkflowSteps();
    }

    // Vom Code-Behind nach dem Datei-Dialog aufgerufen. Lädt ein defokussiertes
    // Stern-Bild (FITS der ASI oder ein Graubild), bereitet es auf (Binning +
    // Streckung), zeigt es an und erkennt den Donut.
    public async Task LoadStarTestFrameAsync(string path)
    {
        if (!IsStarTestMode) IsStarTestMode = true;
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var gray8 = await Task.Run(() =>
            {
                if (ext is ".fits" or ".fit")
                {
                    using var raw = FitsReader.ReadGray16(path);
                    return StarFramePrep.ToDisplayGray8(raw);
                }
                using var img = Cv2.ImRead(path, ImreadModes.Grayscale);
                if (img.Empty()) throw new InvalidOperationException("Bild leer/unlesbar.");
                return StarFramePrep.ToDisplayGray8(img);
            });

            _starLastPath = path;
            ApplyStarGray(gray8, $"Sterntest: {Path.GetFileName(path)}");
            // Datei-Weg der Paar-Messung: Fokuser-Position aus dem FITS-Header lesen
            // und, sofern eine Fokus-Mitte gemerkt ist, automatisch dem passenden
            // Messplatz zuordnen (siehe TryAutoAssignPairSlot).
            TryAutoAssignPairSlot(path);
        }
        catch (Exception ex)
        {
            StatusText = $"Sterntest-Bild laden fehlgeschlagen: {ex.Message}";
        }
    }

    // Auto-Zoom-Parameter.
    private const double DonutFillFraction = 0.6;  // Donut-Durchmesser = 60% des Crop-Fensters (20% Rand je Seite)
    private const int MinStarCropPx = 200;         // Zoom-Obergrenze: Crop nie kleiner → kein Pixelbrei
    private const double AutoZoomMinRadius = 30;   // brauchbare Sterngröße für Auto-Zoom (gebinnte px)
    private const double AutoZoomMaxRadius = 150;

    // Setzt das Crop-Fenster: Auto-Zoom auf den Donut, wenn seine Größe brauchbar
    // ist (zentriert, so groß wie möglich ohne dass das Overlay den Rand berührt,
    // mit Zoom-Obergrenze). Sonst ganzes Bild.
    private void SetStarAutoView()
    {
        if (_starGray is not { } g) return;
        int W = g.Width, H = g.Height;
        if (_donut is { } d && d.OuterRadius >= AutoZoomMinRadius && d.OuterRadius <= AutoZoomMaxRadius)
        {
            int side = (int)Math.Round(2 * d.OuterRadius / DonutFillFraction);
            side = Math.Max(side, MinStarCropPx);
            side = Math.Min(side, Math.Min(W, H));
            int x = Math.Clamp((int)Math.Round(d.OuterCenter.X - side / 2.0), 0, W - side);
            int y = Math.Clamp((int)Math.Round(d.OuterCenter.Y - side / 2.0), 0, H - side);
            _starCropRect = new OpenCvSharp.Rect(x, y, side, side);
        }
        else
        {
            _starCropRect = new OpenCvSharp.Rect(0, 0, W, H);
        }
        RenderStarView();
    }

    // Schneidet das aktuelle Crop-Fenster aus _starGray, zeigt es an und aktualisiert
    // die Display-Transform (damit das Donut-Overlay weiter passt).
    private void RenderStarView()
    {
        if (_starGray is not { } g) return;
        var full = new OpenCvSharp.Rect(0, 0, g.Width, g.Height);
        var r = (_starCropRect.Width > 0 && _starCropRect.Height > 0) ? _starCropRect & full : full;
        if (r.Width <= 0 || r.Height <= 0) r = full;
        _starCropRect = r;

        Bitmap bmp;
        using (var roi = new Mat(g, r))
        using (var crop = roi.Clone())
            bmp = MatBitmapConverter.ToBitmap(crop);

        CurrentFrame?.Dispose();
        CurrentFrame = bmp;
        _frameWidth = g.Width; _frameHeight = g.Height;
        _croppedWidth = r.Width; _croppedHeight = r.Height;
        _starCropOffsetX = r.X; _starCropOffsetY = r.Y;
        RecomputeDisplayTransform();
    }

    // Manueller Zoom (Mausrad) im Sterntest, um das aktuelle Crop-Zentrum.
    public void StarZoomStep(int direction)
    {
        if (!IsStarTestMode || _starGray is not { } g) return;
        int W = g.Width, H = g.Height;
        var r = (_starCropRect.Width > 0) ? _starCropRect : new OpenCvSharp.Rect(0, 0, W, H);
        double cx = r.X + r.Width / 2.0, cy = r.Y + r.Height / 2.0;
        double f = direction > 0 ? 0.8 : 1.25; // rein / raus
        int side = (int)Math.Round(Math.Max(r.Width, r.Height) * f);
        side = Math.Clamp(side, MinStarCropPx, Math.Min(W, H));
        int x = Math.Clamp((int)Math.Round(cx - side / 2.0), 0, W - side);
        int y = Math.Clamp((int)Math.Round(cy - side / 2.0), 0, H - side);
        _starCropRect = new OpenCvSharp.Rect(x, y, side, side);
        RenderStarView();
    }

    partial void OnCurrentMarkingsChanged(MarkingSet value)
    {
        RecomputeOverlayDisplay();
        NotifyJustageReadouts();
        NotifyPhaseTitles();
        RefreshWorkflowSteps();
        // Neue Messung → das scharfe Schnellverfahren gilt nur für den Stand,
        // bei dem der Nutzer es ausgelöst hat.
        IsPhaseCompleteArmed = false;
    }

    partial void OnCurrentCalibrationChanged(CalibrationResult? value)
    {
        RecomputeOverlayDisplay();
        NotifyJustageReadouts();
        RefreshWorkflowSteps();
    }

    // Versatz-abhängige Justage-Anzeigen (Drehempfehlung, Pfeile, Status) neu
    // berechnen, wenn sich Markierungen oder Kalibrierung ändern.
    private void NotifyJustageReadouts()
    {
        OnPropertyChanged(nameof(ActivePhaseScrewVms));
        OnPropertyChanged(nameof(ActivePhaseScrewMarkers));
        OnPropertyChanged(nameof(ActivePhaseArrowsGeometry));
        OnPropertyChanged(nameof(ActivePhaseTurnLabels));
        OnPropertyChanged(nameof(ActivePhaseStatusText));
        OnPropertyChanged(nameof(ActivePhaseUnderTolerance));
        OnPropertyChanged(nameof(ActivePhaseFullyCalibrated));
        OnPropertyChanged(nameof(ActivePhaseHasScrews));
        OnPropertyChanged(nameof(ShowCalibrationGate));
        OnPropertyChanged(nameof(CalibrationGateText));
    }
    partial void OnZoomPercentChanged(double value)
    {
        // Crop ändert sich → DisplayTransform muss neu, sobald die nächste Frame-
        // Iteration die neuen _cropped*-Werte gesetzt hat. Bis dahin reicht ein
        // Recompute mit den alten Werten — der nächste Frame korrigiert.
        RecomputeDisplayTransform();
    }

    public void DeleteSelectedMarking()
    {
        if (GetSelectedKind() is MarkingKind kind)
        {
            DeleteMarking(kind);
        }
    }

    [RelayCommand]
    private void Nudge(string? direction)
    {
        var (dx, dy) = direction switch
        {
            "up" => (0, -1),
            "down" => (0, 1),
            "left" => (-1, 0),
            "right" => (1, 0),
            _ => (0, 0),
        };
        if (dx == 0 && dy == 0) return;
        if (GetSelectedKind() is not MarkingKind kind) return;
        var m = CurrentMarkings[kind];
        if (!m.IsPlaced) return;
        UpdateMarking(kind, m with
        {
            CenterX = m.CenterX + dx,
            CenterY = m.CenterY + dy,
        });
    }

    // Ein-Schritt-Undo fürs Markierungs-Löschen (Entf ist sonst unwiderruflich).
    private (MarkingKind Kind, Marking Marking)? _lastDeletedMarking;

    private void DeleteMarking(MarkingKind kind)
    {
        var m = CurrentMarkings[kind];
        if (!m.IsPlaced) return;
        _lastDeletedMarking = (kind, m);
        UpdateMarking(kind, Marking.Default(kind) with
        {
            IsAutoEnabled = m.IsAutoEnabled,
            IsVisible = m.IsVisible,
            AutoFocusTarget = m.AutoFocusTarget,
        });
        var name = MarkingVms.FirstOrDefault(v => v.Kind == kind)?.Name ?? kind.ToString();
        StatusText = $"Markierung '{name}' gelöscht — Strg+Z stellt sie wieder her.";
    }

    /// <summary>Stellt die zuletzt gelöschte Markierung wieder her (Strg+Z).</summary>
    public void UndoDeleteMarking()
    {
        if (_lastDeletedMarking is not { } last) return;
        _lastDeletedMarking = null;
        UpdateMarking(last.Kind, last.Marking);
        var name = MarkingVms.FirstOrDefault(v => v.Kind == last.Kind)?.Name ?? last.Kind.ToString();
        StatusText = $"Markierung '{name}' wiederhergestellt.";
    }

    private void UpdateMarking(MarkingKind kind, Marking updated, bool persist = true)
    {
        CurrentMarkings = CurrentMarkings.With(updated);
        if (persist) PersistMarkings();
    }

    private void ApplyFocusCapabilityFromSource()
    {
        if (_source is IFocusControl fc)
        {
            IsFocusControlAvailable = true;
            FocusMin = fc.MinFocus;
            FocusMax = fc.MaxFocus;
            if (FocusValue < FocusMin) FocusValue = FocusMin;
            if (FocusValue > FocusMax) FocusValue = FocusMax;
            fc.AutoFocus = IsAutoFocus;
            if (!IsAutoFocus)
            {
                fc.Focus = FocusValue;
            }
        }
        else
        {
            IsFocusControlAvailable = false;
        }
    }

    partial void OnIsAutoFocusChanged(bool value)
    {
        if (_source is IFocusControl fc)
        {
            fc.AutoFocus = value;
            if (!value)
            {
                fc.Focus = FocusValue;
            }
        }
    }

    partial void OnFocusValueChanged(double value)
    {
        if (!IsAutoFocus)
        {
            if (_source is IFocusControl fc)
            {
                fc.Focus = value;
            }
            // Fokus-Änderung verschiebt die scharfe Ebene → wie bei der
            // Belichtung gelten bestehende Markierungen als potenziell veraltet.
            ResetFreshlyMeasuredPhases();
        }
    }

    private void ApplyExposureCapabilityFromSource()
    {
        if (_source is IExposureControl ec)
        {
            IsExposureControlAvailable = true;
            ExposureMin = ec.MinExposure;
            ExposureMax = ec.MaxExposure;
            if (ExposureValue < ExposureMin) ExposureValue = ExposureMin;
            if (ExposureValue > ExposureMax) ExposureValue = ExposureMax;
            // V4L2-Auto bleibt durchgehend aus — die Software-Regelung in der
            // CaptureLoop steuert die manuelle Belichtungszeit selbst.
            ec.AutoExposure = false;
            ec.Exposure = ExposureValue;
        }
        else
        {
            IsExposureControlAvailable = false;
        }
    }

    partial void OnExposureValueChanged(double value)
    {
        // Slider-Bewegungen nur im manuellen Modus an die Kamera durchreichen.
        // Updates aus der Auto-Loop (postet die Property auf dem UI-Thread)
        // werden hier ignoriert, weil dort IsAutoExposure==true gilt — die
        // Kamera-Schreibung erledigt schon die Auto-Loop direkt.
        if (!IsAutoExposure)
        {
            if (_source is IExposureControl ec)
            {
                ec.Exposure = value;
            }
            // Nur eine bewusste manuelle Änderung macht das Bild anders genug,
            // um bestehende Markierungen als potenziell veraltet zu markieren —
            // die Auto-Loop feuert sonst pro Frame und würde das Frische-Set
            // dauerhaft leeren.
            ResetFreshlyMeasuredPhases();
        }
    }

    private void CaptureLoop(CancellationToken ct)
    {
        var source = _source;
        if (source is null)
        {
            return;
        }

        var exposureCtl = source as IExposureControl;
        var focusCtl = source as IFocusControl;
        var autoCounter = 0;
        var focusPollCounter = 0;

        while (!ct.IsCancellationRequested)
        {
            using var frame = source.GrabFrame();
            if (frame is null)
            {
                Thread.Sleep(50);
                continue;
            }

            // Live-Snapshot für die Auto-Detection: garantiert, dass Detection
            // und Display dasselbe Frame sehen — sonst entsteht durch Frame-
            // Buffer-Lag ein systematischer Versatz aller Markierungen.
            lock (_liveSnapshotLock)
            {
                _liveSnapshot?.Dispose();
                _liveSnapshot = frame.Clone();
                _liveSnapshotSequence++;
            }

            if (IsOverlayEnabled)
            {
                DrawOverlay(frame);
            }

            var prevFrameW = _frameWidth;
            var prevFrameH = _frameHeight;
            var prevCroppedW = _croppedWidth;
            var prevCroppedH = _croppedHeight;
            _frameWidth = frame.Width;
            _frameHeight = frame.Height;
            _croppedWidth = frame.Width;
            _croppedHeight = frame.Height;

            Bitmap bitmap;
            Mat? cropped = null;
            try
            {
                var displayFrame = frame;
                var zoom = ZoomPercent / 100.0;
                if (zoom > 1.0001)
                {
                    var w = Math.Max(1, (int)Math.Round(frame.Width / zoom));
                    var h = Math.Max(1, (int)Math.Round(frame.Height / zoom));
                    var x = (frame.Width - w) / 2;
                    var y = (frame.Height - h) / 2;
                    _croppedWidth = w;
                    _croppedHeight = h;
                    // Clone, damit das Bild zusammenhängend im Speicher liegt —
                    // ImEncode auf einem Sub-Mat (mit fremdem Row-Stride) ist
                    // unzuverlässig und führte zu nativen Abbrüchen.
                    using var roi = new Mat(frame, new Rect(x, y, w, h));
                    cropped = roi.Clone();
                    displayFrame = cropped;
                }
                bitmap = MatBitmapConverter.ToBitmap(displayFrame);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => StatusText = $"Konvertierung fehlgeschlagen: {ex.Message}");
                Thread.Sleep(100);
                continue;
            }
            finally
            {
                cropped?.Dispose();
            }

            Dispatcher.UIThread.Post(() =>
            {
                // Im Sterntest-Modus zeigt das geladene Datei-Bild — ein laufender
                // Kamera-Loop darf es nicht überschreiben.
                if (IsStarTestMode) { bitmap.Dispose(); return; }
                CurrentFrame?.Dispose();
                CurrentFrame = bitmap;
            });

            if (_frameWidth != prevFrameW || _frameHeight != prevFrameH
                || _croppedWidth != prevCroppedW || _croppedHeight != prevCroppedH)
            {
                Dispatcher.UIThread.Post(RecomputeDisplayTransform);
            }

            if (IsAutoExposure && exposureCtl is not null && ++autoCounter >= AutoAdjustInterval)
            {
                autoCounter = 0;
                AdjustExposure(frame, exposureCtl);
            }

            // V4L2 meldet bei UVC den aktuellen Fokuswert auch im Autofocus-Modus
            // oft korrekt zurück — alle ~10 Frames lesen und Slider nachziehen.
            if (IsAutoFocus && focusCtl is not null && ++focusPollCounter >= 10)
            {
                focusPollCounter = 0;
                var actualFocus = focusCtl.Focus;
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsAutoFocus)
                    {
                        FocusValue = actualFocus;
                    }
                });
            }

            Thread.Sleep(33);
        }
    }

    private void DrawOverlay(Mat frame)
    {
        // Außerhalb des Kalibrier-Wizards gibt es nichts ins Bild zu zeichnen:
        // Markierungen/Reticle/Justage-Overlay liegen als Avalonia-Shapes über dem
        // Bild. Die schwere Ellipsen-Detektion läuft daher nur noch für den Wizard.
        if (!CalibrationWizard.IsActive)
        {
            // Cache leeren, damit der nächste Wizard-Start frisch beginnt.
            _lastCalibrationOazRand = null;
            return;
        }

        // OAZ-Rand kommt aus dem Standard-Detector (Canny → Konturen → Ellipsen).
        // Während der Kalibrierung nutzen wir für den Marker den BrightSpotDetector
        // — der überlebt Bewegungsunschärfe besser, weil er keine geschlossene
        // Kontur braucht.
        using var gray = Preprocessor.ToGrayscaleBlurred(frame, blurKernel: 5);
        var raw = _detector.Detect(gray);
        var clustered = _clusterer.Merge(raw);
        var analysis = _analyzer.Analyze(clustered);

        // OAZ-Rand aus aktueller Detektion akzeptieren, wenn plausibel groß;
        // sonst den letzten guten weiterverwenden.
        EllipseFit? calibOazRand;
        if (analysis.OazRand is { } freshOazRand && freshOazRand.ContourArea >= MinCalibrationOazRandArea)
        {
            _lastCalibrationOazRand = freshOazRand;
            calibOazRand = freshOazRand;
        }
        else
        {
            calibOazRand = _lastCalibrationOazRand;
        }

        var spot = BrightSpotDetector.FindDarkInBright(gray, insideEllipse: calibOazRand);
        EllipseFit? calibMarker = spot.Position is { } mc
            ? new EllipseFit(mc, new Size2f(16, 16), 0f, 50)
            : null;

        DrawCalibrationOverlay(frame, calibOazRand, calibMarker);
        var diagnostic = spot;
        // Frame-Größe durchreichen — der Wizard hat selbst keinen Zugriff auf das
        // Mat und braucht sie für den CalibrationResult beim Abschluss der
        // Rotation (siehe CalibrationWizardViewModel.CompleteRotation).
        var frameW = frame.Width;
        var frameH = frame.Height;
        Dispatcher.UIThread.Post(() =>
        {
            CalibrationWizard.FrameWidth = frameW;
            CalibrationWizard.FrameHeight = frameH;
            CalibrationWizard.Tick(calibOazRand, calibMarker, diagnostic);
        });
    }

    private void DrawCalibrationOverlay(Mat frame, EllipseFit? oazRand, EllipseFit? marker)
    {
        if (oazRand is { } t)
        {
            Cv2.Ellipse(frame, t.ToRotatedRect(), OazRandColor, thickness: 1);
            Cv2.DrawMarker(frame, ToPoint(t.Center), OazRandColor,
                MarkerTypes.Cross, markerSize: 12, thickness: 1);
        }

        if (marker is { } m)
        {
            Cv2.Circle(frame, ToPoint(m.Center), 8, MarkerColor, thickness: 1);
            Cv2.DrawMarker(frame, ToPoint(m.Center), MarkerColor,
                MarkerTypes.Cross, markerSize: 10, thickness: 1);
        }

        if (CalibrationWizard.IsAwaitingOrientation && oazRand is { } t2)
        {
            var origin = ToPoint(t2.Center);
            var lineColor = CalibrationWizard.IsAligned
                ? CalibrationOrientOkColor
                : CalibrationOrientFailColor;
            Cv2.Line(frame, origin, new Point(origin.X, 0), lineColor, thickness: 2);
        }

        var samples = CalibrationWizard.Samples;
        for (var i = 0; i < samples.Count; i++)
        {
            var pt = ToPoint(samples[i]);
            Cv2.Circle(frame, pt, 4, CalibrationSampleColor, thickness: 1);
        }

        if (CalibrationWizard.Preview is { } fit)
        {
            Cv2.Circle(frame, ToPoint(fit.OpticalCenter),
                (int)Math.Round(fit.FitRadius), CalibrationFitColor, thickness: 1);
            Cv2.DrawMarker(frame, ToPoint(fit.OpticalCenter), CalibrationFitColor,
                MarkerTypes.Cross, markerSize: 22, thickness: 2);
        }
    }

    private static Point ToPoint(Point2f p) => new((int)Math.Round(p.X), (int)Math.Round(p.Y));

    private void AdjustExposure(Mat frame, IExposureControl ec)
    {
        var meanScalar = Cv2.Mean(frame);
        var brightness = (meanScalar.Val0 + meanScalar.Val1 + meanScalar.Val2) / 3.0;
        if (brightness < 1.0)
        {
            brightness = 1.0;
        }

        var ratio = TargetBrightness / brightness;
        var maxUp = MaxStepRatio;
        var maxDown = 1.0 / MaxStepRatio;
        if (ratio > maxUp) ratio = maxUp;
        if (ratio < maxDown) ratio = maxDown;

        var current = ec.Exposure;
        if (current < ec.MinExposure) current = ec.MinExposure;

        // Wie ein Helligkeitsfaktor auf den Belichtungswert wirkt, weiß nur das Backend:
        // V4L2/ASI/Alpaca rechnen linear, DirectShow logarithmisch (log2-Sekunden).
        var next = ec.ScaleExposure(current, ratio);
        if (next < ec.MinExposure) next = ec.MinExposure;
        if (next > ec.MaxExposure) next = ec.MaxExposure;

        if (Math.Abs(next - current) < 0.5)
        {
            return;
        }

        ec.Exposure = next;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsAutoExposure)
            {
                ExposureValue = next;
            }
        });
    }
}

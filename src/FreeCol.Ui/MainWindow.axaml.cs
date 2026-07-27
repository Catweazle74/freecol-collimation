using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using FreeCol.Core.Startest;
using FreeCol.Ui.ViewModels;

namespace FreeCol.Ui;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private readonly WindowStateStore _windowStateStore = new();
    private Image? _liveImage;
    private Canvas? _overlayCanvas;
    private Canvas? _guideBoxHost;
    private Border? _guideBoxBorder;
    private Control? _guideBoxGrip;
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Opened += OnOpened;
        Closing += OnClosing;
        KeyDown += OnKeyDown;
        _liveImage = this.FindControl<Image>("LiveImage");
        _overlayCanvas = this.FindControl<Canvas>("OverlayCanvas");
        _guideBoxHost = this.FindControl<Canvas>("GuideBoxHost");
        _guideBoxBorder = this.FindControl<Border>("GuideBoxOverlay");
        _guideBoxGrip = this.FindControl<Control>("GuideBoxGrip");
        if (_liveImage is not null)
        {
            _liveImage.PointerPressed += OnImagePointerPressed;
            _liveImage.PointerMoved += OnImagePointerMoved;
            _liveImage.PointerReleased += OnImagePointerReleased;
            _liveImage.PointerWheelChanged += OnImagePointerWheelChanged;
        }
        if (_guideBoxGrip is not null)
        {
            _guideBoxGrip.PointerPressed += OnGuideBoxGripPointerPressed;
            _guideBoxGrip.PointerMoved += OnGuideBoxGripPointerMoved;
            _guideBoxGrip.PointerReleased += OnGuideBoxGripPointerReleased;
        }
        // Nur Window-LayoutUpdated: feuert nach abgeschlossenem Layout-Pass.
        // Mehrfach-Subscriptions (Image + Window + PropertyChanged) führten zu
        // alternierenden Bounds-Werten — Image's Bounds und Canvas's Bounds
        // weichen während des Resizes voneinander ab.
        this.LayoutUpdated += OnLiveImageLayoutUpdated;
        this.LayoutUpdated += OnGuideBoxHostLayoutUpdated;
        _viewModel.OverlayLayoutChanged += UpdateOverlayClip;
    }

    private async void OnLoadStarTestImage(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Sterntest-Bild laden",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Stern-Aufnahmen") { Patterns = new[] { "*.fits", "*.fit", "*.png", "*.jpg", "*.jpeg", "*.tif", "*.tiff" } },
                    FilePickerFileTypes.All,
                },
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                await _viewModel.LoadStarTestFrameAsync(path);
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Bildauswahl fehlgeschlagen: {ex.Message}";
        }
    }

    private async void OnPickWatchFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Aufnahme-Ordner überwachen",
                AllowMultiple = false,
            });
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                _viewModel.StartStarWatch(path);
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Ordnerauswahl fehlgeschlagen: {ex.Message}";
        }
    }

    private double _lastNotifiedWidth = -1;
    private double _lastNotifiedHeight = -1;

    private void OnLiveImageLayoutUpdated(object? sender, System.EventArgs e)
    {
        if (_overlayCanvas is null) return;
        var b = _overlayCanvas.Bounds;
        if (System.Math.Abs(b.Width - _lastNotifiedWidth) < 0.5
            && System.Math.Abs(b.Height - _lastNotifiedHeight) < 0.5)
            return;
        _lastNotifiedWidth = b.Width;
        _lastNotifiedHeight = b.Height;
        _viewModel.NotifyControlSize(b.Width, b.Height);
    }

    private void UpdateOverlayClip()
    {
        if (_overlayCanvas is null || _liveImage is null) return;
        var b = _liveImage.Bounds;
        var t = _viewModel.CurrentDisplayTransform;
        if (!t.IsValid || b.Width <= 0 || b.Height <= 0)
        {
            _overlayCanvas.Clip = null;
            return;
        }
        var displayedW = _viewModel.CroppedWidth * t.Ratio;
        var displayedH = _viewModel.CroppedHeight * t.Ratio;
        _overlayCanvas.Clip = new RectangleGeometry(new Rect(t.MarginX, t.MarginY, displayedW, displayedH));
    }

    private void OnLiveImagePropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (_liveImage is null) return;
        if (e.Property == Avalonia.Visual.BoundsProperty)
        {
            _viewModel.NotifyControlSize(_liveImage.Bounds.Width, _liveImage.Bounds.Height);
        }
    }

    private void OnImagePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_viewModel.IsStarTestMode)
        {
            // Sterntest: Mausrad zoomt das geladene Bild (eigener Crop-Mechanismus).
            _viewModel.StarZoomStep(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }
        const double step = 10.0;
        var newZoom = _viewModel.ZoomPercent + (e.Delta.Y > 0 ? step : -step);
        _viewModel.ZoomPercent = Math.Clamp(newZoom, _viewModel.ZoomMin, _viewModel.ZoomMax);
        e.Handled = true;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        var state = _windowStateStore.Load();

        if (state is not null && state.Width > 0 && state.Height > 0)
        {
            Width = state.Width;
            Height = state.Height;
        }

        // Gespeicherte Position nur übernehmen, wenn sie auf einem aktuell
        // vorhandenen Bildschirm sichtbar ist. Sonst (kein State, Monitor
        // abgesteckt, anderes Setup) zentriert auf dem Hauptbildschirm starten —
        // sonst landet das Fenster bei Multi-Monitor leicht auf dem falschen
        // Display oder ganz off-screen und wirkt "unsichtbar".
        //
        // Geklemmt wird jeweils gegen den Bildschirm, auf dem das Fenster
        // tatsächlich landet: bei wiederhergestellter Position kann das der
        // Zweitmonitor sein, und der ist am Astro-PC gerade der große. Gegen den
        // Hauptbildschirm zu klemmen würde die 1800-px-Wunschbreite dort unnötig
        // auf das kleinere Panel stutzen.
        if (state is not null && IsPositionOnAnyScreen(state.X, state.Y))
        {
            Position = new PixelPoint(state.X, state.Y);
            ClampSizeToWorkingArea(ScreenForPosition(state.X, state.Y));
        }
        else
        {
            // Zentriert wird auf dem Hauptbildschirm, also auch gegen den klemmen —
            // und zwar vorher, weil die Zentrierung die endgültige Größe braucht.
            ClampSizeToWorkingArea(Screens?.Primary ?? Screens?.All.FirstOrDefault());
            CenterOnPrimaryScreen();
        }

        if (state is null)
        {
            return;
        }

        if (state.IsMaximized)
        {
            WindowState = Avalonia.Controls.WindowState.Maximized;
        }

        // Zuletzt aktive Kamera automatisch wieder starten.
        _viewModel.AutoStartCamera(state.LastCameraName);
        // Zuletzt überwachten Ordner als Default für den Sterntest-Watcher merken.
        _viewModel.SetRememberedWatchFolder(state.LastWatchFolder);

        // Zuletzt genutzte Alpaca-Verbindung wiederherstellen (nur gespeicherte Werte).
        if (!string.IsNullOrEmpty(state.AlpacaHost)) _viewModel.AlpacaHost = state.AlpacaHost;
        if (state.AlpacaPort > 0) _viewModel.AlpacaPort = state.AlpacaPort;
        if (state.AlpacaDevice >= 0) _viewModel.AlpacaDevice = state.AlpacaDevice;
        if (state.AlpacaExposure > 0) _viewModel.AlpacaExposure = state.AlpacaExposure;
        if (state.AlpacaGain >= 0) _viewModel.AlpacaGain = state.AlpacaGain;
        if (state.FocuserDevice >= 0) _viewModel.FocuserDevice = state.FocuserDevice;
        if (state.FocuserStepSize > 0) _viewModel.FocuserStepSize = state.FocuserStepSize;
        if (state.FocusCenterPosition >= 0) _viewModel.FocusCenterPosition = state.FocusCenterPosition;
        if (state.DefocusSteps > 0) _viewModel.DefocusSteps = state.DefocusSteps;
        if (!string.IsNullOrEmpty(state.TelescopeType)
            && Enum.TryParse<TelescopeType>(state.TelescopeType, out var telescopeType))
        {
            _viewModel.TelescopeType = telescopeType;
        }

        // „So geht's"-Box-Position: nur übernehmen, wenn zuvor tatsächlich
        // verschoben (Sentinel -1 = Default unten links, siehe
        // OnGuideBoxHostLayoutUpdated). Eine übernommene Position gilt als
        // Nutzer-Entscheidung und wird nicht mehr automatisch neu verankert.
        if (state.GuideBoxX >= 0 && state.GuideBoxY >= 0)
        {
            _viewModel.GuideBoxX = state.GuideBoxX;
            _viewModel.GuideBoxY = state.GuideBoxY;
            _guideBoxUserPositioned = true;
        }

        // Justage-Abschluss-Handoff + Sterntest-Gesamtabschluss-Latch: VOR
        // RestoreMode setzen — landet der Nutzer wieder im Justage-Modus,
        // entschärft OnIsJustageModeChanged den Banner ohnehin bewusst wieder.
        _viewModel.IsJustageComplete = state.JustageCompleted;
        _viewModel.StarCollimationAchieved = state.StarCollimationAchieved;

        // Navigations-Zustand wiederherstellen: Modus + Justage-Phase, damit ein
        // Neustart mitten in der Justage dort weitermacht (Kamera startet oben
        // bereits automatisch; ohne Kamera zeigt der Gate-Banner den Zustand an).
        _viewModel.RestoreMode(state.LastMode, state.LastJustagePhase);
    }

    // Der Bildschirm, auf dem ein Fenster mit dieser Position liegt. Position ist
    // zu diesem Zeitpunkt bereits gesetzt; die Punkt-Abfrage geht trotzdem vor
    // ScreenFromWindow, weil sie unabhängig davon ist, ob das Backend den Umzug
    // schon vollzogen hat. Der Punkt selbst kann knapp neben einem Schirm liegen
    // (IsPositionOnAnyScreen prüft eine ganze Titelleisten-Zone) — dann greifen
    // die Fallbacks.
    private Screen? ScreenForPosition(int x, int y)
    {
        var screens = Screens;
        if (screens is null)
        {
            return null;
        }

        return screens.ScreenFromPoint(new PixelPoint(x, y))
            ?? screens.ScreenFromWindow(this)
            ?? screens.Primary
            ?? screens.All.FirstOrDefault();
    }

    // Begrenzt die Fenstergröße auf den nutzbaren Bereich des übergebenen
    // Bildschirms. Die XAML-Wunschgröße (1800×850) ist auf das große
    // Astro-PC-Display gemünzt und überragt Notebook-Panels deutlich: das
    // MacBook Air misst 1440×900 logische Punkte, nach Menüleiste und Dock
    // bleiben 1440×797. Ohne diese Klemmung ragte das Fenster dort schon beim
    // Öffnen über den Rand — und weil die 320-px-Sidebar in der rechten
    // Grid-Spalte sitzt, lag ausgerechnet sie außerhalb des Sichtbaren.
    private void ClampSizeToWorkingArea(Screen? screen)
    {
        if (screen is null)
        {
            return;
        }

        // WorkingArea ist in physischen Pixeln, Width/Height in logischen.
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;

        // WorkingArea meint die Außenmaße, Width/Height nur den Client-Bereich.
        // Ohne Abzug von Titelleiste und Rahmen bleibt das Fenster außen größer
        // als der nutzbare Bereich — unter Windows um 16×39 px gemessen, und auf
        // dem MacBook mit nur 797 px nutzbarer Höhe frisst die Titelleiste den
        // Klemm-Gewinn spürbar wieder auf. Kennt das Backend die Rahmengröße
        // noch nicht (FrameSize null) oder hat das OS den Rahmen bereits selbst
        // beschnitten (Differenz negativ), bleibt es beim Verhalten ohne Abzug.
        // Gemessen wird gegen ClientSize, nicht gegen Width/Height: beide Werte
        // beschreiben das real dargestellte Fenster, ihre Differenz ist also der
        // reine Dekorations-Zuschlag. Width kann zu diesem Zeitpunkt eine noch
        // nicht umgesetzte Wunschgröße sein (etwa 3000 aus einem State vom
        // größeren Display) — die Differenz dagegen wäre negativ und der
        // Zuschlag fiele fälschlich auf 0.
        var frame = FrameSize;
        var frameWidth = frame.HasValue ? Math.Max(0, frame.Value.Width - ClientSize.Width) : 0;
        var frameHeight = frame.HasValue ? Math.Max(0, frame.Value.Height - ClientSize.Height) : 0;

        var maxWidth = Math.Max(MinWidth, screen.WorkingArea.Width / scaling - frameWidth);
        var maxHeight = Math.Max(MinHeight, screen.WorkingArea.Height / scaling - frameHeight);

        if (Width > maxWidth)
        {
            Width = maxWidth;
        }
        if (Height > maxHeight)
        {
            Height = maxHeight;
        }
    }

    // Zentriert das Fenster auf dem Hauptbildschirm. WindowStartupLocation=
    // CenterScreen zentriert auf dem Display, auf dem das OS das Fenster initial
    // platziert (bei Multi-Monitor oft der Zweitmonitor) — wir wollen aber
    // verlässlich den Hauptbildschirm.
    private void CenterOnPrimaryScreen()
    {
        var screen = Screens?.Primary ?? Screens?.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea; // physische Pixel
        var winWidth = (int)(Width * screen.Scaling);
        var winHeight = (int)(Height * screen.Scaling);
        var x = area.X + Math.Max(0, (area.Width - winWidth) / 2);
        var y = area.Y + Math.Max(0, (area.Height - winHeight) / 2);
        Position = new PixelPoint(x, y);
    }

    // Liegt die (zuvor gespeicherte) Fensterposition auf einem aktuell
    // vorhandenen Bildschirm? Geprüft wird die Titelleisten-Zone oben, damit das
    // Fenster zumindest greif- und verschiebbar bleibt.
    private bool IsPositionOnAnyScreen(int x, int y)
    {
        var screens = Screens;
        if (screens is null || screens.All.Count == 0)
        {
            return true; // Keine Bildschirm-Info → gespeicherter Position vertrauen.
        }

        const int titleBarZone = 80;
        var grab = new PixelRect(x, y, Math.Max(1, (int)Width), titleBarZone);
        foreach (var screen in screens.All)
        {
            if (screen.Bounds.Intersects(grab))
            {
                return true;
            }
        }
        return false;
    }

    // Fire-and-forget Closing-Handler würde Avalonia das Fenster sofort schließen
    // lassen, während ShutdownAsync noch läuft. Stattdessen Close abbrechen, sauber
    // herunterfahren und erst dann wirklich schließen.
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        e.Cancel = true;
        PersistWindowState();
        await _viewModel.ShutdownAsync();
        _shutdownComplete = true;
        Close();
    }

    private void PersistWindowState()
    {
        var isMaximized = WindowState == Avalonia.Controls.WindowState.Maximized;
        var state = new PersistedWindowState(
            Width: Width,
            Height: Height,
            X: Position.X,
            Y: Position.Y,
            IsMaximized: isMaximized,
            LastCameraName: _viewModel.LastStartedCameraName,
            LastWatchFolder: _viewModel.LastWatchFolder,
            AlpacaHost: _viewModel.AlpacaHost,
            AlpacaPort: _viewModel.AlpacaPort,
            AlpacaDevice: _viewModel.AlpacaDevice,
            AlpacaExposure: _viewModel.AlpacaExposure,
            AlpacaGain: _viewModel.AlpacaGain,
            LastMode: _viewModel.CurrentModeKey,
            LastJustagePhase: _viewModel.ActiveJustagePhase,
            FocuserDevice: _viewModel.FocuserDevice,
            FocuserStepSize: _viewModel.FocuserStepSize,
            FocusCenterPosition: _viewModel.FocusCenterPosition,
            DefocusSteps: _viewModel.DefocusSteps,
            TelescopeType: _viewModel.TelescopeType.ToString(),
            JustageCompleted: _viewModel.IsJustageComplete,
            StarCollimationAchieved: _viewModel.StarCollimationAchieved,
            // Nur eine bewusst gesetzte Position persistieren — sonst bleibt der
            // Sentinel erhalten und der Unten-links-Default folgt künftigen
            // Fenstergrößen (siehe OnGuideBoxHostLayoutUpdated).
            GuideBoxX: _guideBoxUserPositioned ? _viewModel.GuideBoxX : -1,
            GuideBoxY: _guideBoxUserPositioned ? _viewModel.GuideBoxY : -1);
        _windowStateStore.Save(state);
    }

    private (double X, double Y)? TransformToFrame(Point local)
    {
        var t = _viewModel.CurrentDisplayTransform;
        if (!t.IsValid) return null;

        var displayedW = _viewModel.CroppedWidth * t.Ratio;
        var displayedH = _viewModel.CroppedHeight * t.Ratio;
        var localX = local.X - t.MarginX;
        var localY = local.Y - t.MarginY;
        if (localX < 0 || localY < 0 || localX > displayedW || localY > displayedH)
            return null;

        // Inverse zum Half-Pixel-Offset in DisplayTransform.MapToDisplay.
        return (localX / t.Ratio + t.CropOffsetX - 0.5, localY / t.Ratio + t.CropOffsetY - 0.5);
    }

    private const double DragThresholdLocalPx = 8.0;
    private Point? _pointerDownLocal;
    private (double X, double Y)? _pointerDownFrame;
    private bool _isDragging;

    private Visual? PointerRef => (Visual?)_overlayCanvas ?? _liveImage;

    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (PointerRef is null) return;
        var local = e.GetPosition(PointerRef);
        var frame = TransformToFrame(local);
        if (frame is null) return;

        var props = e.GetCurrentPoint(_liveImage).Properties;
        if (props.IsRightButtonPressed)
        {
            _viewModel.OnFramePointerDownRight();
            e.Handled = true;
            return;
        }

        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            _viewModel.OnFrameCtrlClick(frame.Value.X, frame.Value.Y);
            e.Handled = true;
            return;
        }

        _pointerDownLocal = local;
        _pointerDownFrame = frame;
        _isDragging = false;
        e.Pointer.Capture(_liveImage);
        e.Handled = true;
    }

    private void OnImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pointerDownLocal is null || PointerRef is null) return;
        var local = e.GetPosition(PointerRef);

        if (!_isDragging)
        {
            var ddx = local.X - _pointerDownLocal.Value.X;
            var ddy = local.Y - _pointerDownLocal.Value.Y;
            if (ddx * ddx + ddy * ddy < DragThresholdLocalPx * DragThresholdLocalPx) return;
            _isDragging = true;
            if (_pointerDownFrame is { } pf)
            {
                _viewModel.OnFramePointerBeginDrag(pf.X, pf.Y);
            }
        }

        var frame = TransformToFrame(local);
        if (frame is null) return;
        _viewModel.OnFramePointerDrag(frame.Value.X, frame.Value.Y);
    }

    private void OnImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pointerDownLocal is null) return;

        if (_isDragging)
        {
            _viewModel.OnFramePointerCommit();
        }
        else if (_pointerDownFrame is { } f)
        {
            _viewModel.OnFramePointerClick(f.X, f.Y);
            _viewModel.OnFramePointerCommit();
        }

        _pointerDownLocal = null;
        _pointerDownFrame = null;
        _isDragging = false;
        e.Pointer.Capture(null);
    }

    private const double GuideBoxDefaultMargin = 12.0;
    // true, sobald der Nutzer die Box je verschoben hat (in dieser oder einer
    // früheren Sitzung) — erst dann wird die Position eingefroren/persistiert;
    // vorher folgt der Unten-links-Default jedem Layout-/Größenwechsel.
    private bool _guideBoxUserPositioned;
    private bool _isDraggingGuideBox;
    private Point _guideBoxDragStartPointer;
    private double _guideBoxDragStartX;
    private double _guideBoxDragStartY;

    // Klemmt die „So geht's"-Box beim ersten Layout-Pass auf ihre Default-
    // Position (unten links) und hält sie danach bei jedem Layout-Pass
    // (insbesondere Fenster-Resize) vollständig innerhalb des Bild-Grids.
    private void OnGuideBoxHostLayoutUpdated(object? sender, System.EventArgs e)
    {
        if (_guideBoxHost is null || _guideBoxBorder is null) return;
        var hostW = _guideBoxHost.Bounds.Width;
        var hostH = _guideBoxHost.Bounds.Height;
        var boxW = _guideBoxBorder.Bounds.Width;
        var boxH = _guideBoxBorder.Bounds.Height;
        if (hostW <= 0 || hostH <= 0 || boxW <= 0 || boxH <= 0) return;

        if (!_guideBoxUserPositioned)
        {
            // Solange nie von Hand verschoben: bei JEDEM Layout-Pass unten links
            // verankern — ein einmalig festgeschriebener Wert bliebe sonst nach
            // Maximieren/Restore auf der Höhe des ersten (kleineren) Passes hängen.
            _viewModel.GuideBoxX = GuideBoxDefaultMargin;
            _viewModel.GuideBoxY = Math.Max(0, hostH - boxH - GuideBoxDefaultMargin);
            return;
        }

        _viewModel.GuideBoxX = Math.Clamp(_viewModel.GuideBoxX, 0, Math.Max(0, hostW - boxW));
        _viewModel.GuideBoxY = Math.Clamp(_viewModel.GuideBoxY, 0, Math.Max(0, hostH - boxH));
    }

    private void OnGuideBoxGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_guideBoxHost is null || _guideBoxGrip is null) return;
        if (!e.GetCurrentPoint(_guideBoxGrip).Properties.IsLeftButtonPressed) return;

        _guideBoxDragStartPointer = e.GetPosition(_guideBoxHost);
        _guideBoxDragStartX = _viewModel.GuideBoxX;
        _guideBoxDragStartY = _viewModel.GuideBoxY;
        _guideBoxUserPositioned = true;
        _isDraggingGuideBox = true;
        e.Pointer.Capture(_guideBoxGrip);
        e.Handled = true;
    }

    private void OnGuideBoxGripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingGuideBox || _guideBoxHost is null || _guideBoxBorder is null) return;

        var current = e.GetPosition(_guideBoxHost);
        var dx = current.X - _guideBoxDragStartPointer.X;
        var dy = current.Y - _guideBoxDragStartPointer.Y;
        var hostW = _guideBoxHost.Bounds.Width;
        var hostH = _guideBoxHost.Bounds.Height;
        var boxW = _guideBoxBorder.Bounds.Width;
        var boxH = _guideBoxBorder.Bounds.Height;

        _viewModel.GuideBoxX = Math.Clamp(_guideBoxDragStartX + dx, 0, Math.Max(0, hostW - boxW));
        _viewModel.GuideBoxY = Math.Clamp(_guideBoxDragStartY + dy, 0, Math.Max(0, hostH - boxH));
        e.Handled = true;
    }

    private void OnGuideBoxGripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingGuideBox) return;
        _isDraggingGuideBox = false;
        e.Pointer.Capture(null);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Tippt der Nutzer gerade in einem Eingabefeld (z. B. Alpaca-Host/Port),
        // gehören Entf/Pfeile/Strg+Z der TextBox — nicht den Markierungs-Shortcuts.
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        if (e.Key == Key.Delete)
        {
            _viewModel.DeleteSelectedMarking();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _viewModel.UndoDeleteMarking();
            e.Handled = true;
            return;
        }

        string? direction = e.Key switch
        {
            Key.Up => "up",
            Key.Down => "down",
            Key.Left => "left",
            Key.Right => "right",
            _ => null,
        };
        if (direction is not null)
        {
            _viewModel.NudgeCommand.Execute(direction);
            e.Handled = true;
        }
    }
}

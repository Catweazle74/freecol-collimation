namespace FreeCol.Ui;

public sealed record PersistedWindowState(
    double Width,
    double Height,
    int X,
    int Y,
    bool IsMaximized,
    string? LastCameraName = null,
    string? LastWatchFolder = null,
    // Sterntest-Alpaca-Verbindung (Sentinel-Defaults = „nicht gespeichert", damit
    // ältere window-state.json die VM-Defaults nicht überschreiben).
    string? AlpacaHost = null,
    int AlpacaPort = 0,
    int AlpacaDevice = -1,
    double AlpacaExposure = 0,
    double AlpacaGain = -1,
    // Navigations-Zustand: wo der Nutzer zuletzt war ("markings"/"justage"/"startest"
    // + Justage-Phase), damit ein Neustart mitten in der Justage dort weitermacht.
    string? LastMode = null,
    int LastJustagePhase = -1,
    // Alpaca-Fokuser (Sterntest): Geräte-Nummer + zuletzt genutzte Schrittweite.
    int FocuserDevice = -1,
    int FocuserStepSize = 0,
    // Justage-Abschluss-Handoff + Sterntest-Gesamtabschluss-Latch: Default
    // false, damit ältere window-state.json ohne diese Felder weiter lädt
    // (Nutzer sieht dann einfach keinen Abschluss-Banner/kein Häkchen, statt
    // eines Ladefehlers).
    bool JustageCompleted = false,
    bool StarCollimationAchieved = false,
    // Position der „So geht's"-Box (frei verschiebbares Overlay über dem
    // Livebild). Sentinel -1 = noch nicht positioniert — MainWindow setzt
    // dann die Default-Position (unten links).
    double GuideBoxX = -1,
    double GuideBoxY = -1);

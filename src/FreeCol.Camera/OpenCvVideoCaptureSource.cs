using OpenCvSharp;

namespace FreeCol.Camera;

/// <summary>
/// Liest Frames per <see cref="VideoCapture"/> aus einem UVC-Gerät, identifiziert
/// über einen Index (entspricht <c>/dev/video&lt;n&gt;</c> auf Linux bzw. DirectShow-
/// Index auf Windows). Damit ist die OCAL als generische UVC-Kamera nutzbar.
/// </summary>
public sealed class OpenCvVideoCaptureSource : ICameraSource, IExposureControl, IFocusControl
{
    // CAP_PROP_AUTO_EXPOSURE kennt je Backend eigene Werte:
    // V4L2  (Linux):   1 = manuell, 3 = aperture-priority (Auto), Wert ist auslesbar.
    // DSHOW (Windows): 0,25 = manuell, 0,75 = Auto — der Ist-Wert ist bei UVC-Kameras
    //                  NICHT verlässlich auslesbar (die OCAL meldet konstant -1).
    private const double V4L2AutoExposureManual = 1.0;
    private const double V4L2AutoExposureAuto = 3.0;
    private const double DshowAutoExposureManual = 0.25;
    private const double DshowAutoExposureAuto = 0.75;

    // Belichtungs-Skala:
    // V4L2  absolute Belichtung in 100-µs-Schritten → linear, 1..10000.
    // DSHOW log2(Belichtungszeit in Sekunden) → additiv, Bereich geräteabhängig
    //       (OCAL: -8..0, also 1/256 s bis 1 s). Die Grenzen ermittelt Start() am
    //       Gerät, weil sie sich je Kamera unterscheiden.
    private const double V4L2MinExposure = 1.0;
    private const double V4L2MaxExposure = 10000.0;

    // Werte, mit denen die DSHOW-Grenzen abgetastet werden: das Backend klemmt einen
    // außerhalb liegenden Wert auf die jeweilige Gerätegrenze, die danach auslesbar ist.
    private const double DshowExposureProbeLow = -1000.0;
    private const double DshowExposureProbeHigh = 1000.0;

    // Fallback, falls das Abtasten scheitert: der für UVC übliche log2-Bereich.
    private const double DshowFallbackMinExposure = -13.0;
    private const double DshowFallbackMaxExposure = 0.0;

    private static bool IsDshow => System.OperatingSystem.IsWindows();

    // V4L2-Backend von OpenCV ist nicht thread-sicher. Read im Capture-Loop und
    // Property-Sets aus der UI dürfen sich nicht überholen, sonst segfaultet die
    // native Bibliothek wortlos. Daher: jeder Zugriff auf _capture unter Lock.
    private readonly object _captureLock = new();

    private readonly int _deviceIndex;
    private readonly int _desiredWidth;
    private readonly int _desiredHeight;
    private VideoCapture? _capture;

    // Unter DSHOW melden UVC-Kameras den Auto-Zustand nicht zurück (AUTO_EXPOSURE
    // liefert konstant -1, AUTOFOCUS 2). Damit die Checkboxen in der UI nicht
    // zurückspringen, merken wir uns den zuletzt gesetzten Soll-Zustand und geben ihn
    // aus, wenn der gelesene Wert keinem der beiden Modi entspricht. Unter V4L2 ist
    // der gelesene Wert maßgeblich — dort ändert sich nichts.
    private bool _autoExposureRequested;
    private bool _autoFocusRequested;

    /// <summary>Tatsächlich gesetzte Aufnahme-Auflösung (nach dem Öffnen), 0 falls unbekannt.</summary>
    public int ActualWidth { get; private set; }
    public int ActualHeight { get; private set; }

    public OpenCvVideoCaptureSource(int deviceIndex, int desiredWidth = 0, int desiredHeight = 0)
    {
        _deviceIndex = deviceIndex;
        _desiredWidth = desiredWidth;
        _desiredHeight = desiredHeight;
    }

    public bool IsRunning
    {
        get
        {
            lock (_captureLock)
            {
                return _capture is not null && _capture.IsOpened();
            }
        }
    }

    public void Start()
    {
        lock (_captureLock)
        {
            if (_capture is not null && _capture.IsOpened())
            {
                return;
            }

            _capture?.Dispose();
            // Unter Windows explizit das DSHOW-Backend erzwingen: der Default
            // (MSMF) kann Kameras in einer anderen Reihenfolge aufzählen als
            // DirectShow, wodurch der Index nicht mehr zum von
            // CameraEnumerator.ListWindows() ermittelten Gerät passen würde.
            // Linux bleibt unverändert beim Default-Backend (V4L2).
            var capture = System.OperatingSystem.IsWindows()
                ? new VideoCapture(_deviceIndex, VideoCaptureAPIs.DSHOW)
                : new VideoCapture(_deviceIndex);
            if (!capture.IsOpened())
            {
                capture.Dispose();
                throw new InvalidOperationException(
                    $"VideoCapture konnte Gerät {_deviceIndex} nicht öffnen.");
            }

            // Gewünschte Aufnahme-Auflösung setzen (vor dem ersten Grab). Die Kamera
            // wählt ggf. die nächstgelegene unterstützte Größe.
            if (_desiredWidth > 0 && _desiredHeight > 0)
            {
                capture.Set(VideoCaptureProperties.FrameWidth, _desiredWidth);
                capture.Set(VideoCaptureProperties.FrameHeight, _desiredHeight);
            }
            ActualWidth = (int)capture.Get(VideoCaptureProperties.FrameWidth);
            ActualHeight = (int)capture.Get(VideoCaptureProperties.FrameHeight);

            if (IsDshow)
            {
                DetectDshowExposureRange(capture);
            }

            _capture = capture;
        }
    }

    /// <summary>
    /// Ermittelt die geräteeigenen Belichtungsgrenzen unter DirectShow, indem je ein
    /// weit außerhalb liegender Wert gesetzt und der davon geklemmte Ist-Wert
    /// zurückgelesen wird. Anschließend wird die Ausgangsbelichtung wiederhergestellt.
    /// Liefert das Gerät unbrauchbare Werte (Grenzen nicht aufsteigend), bleibt es beim
    /// UVC-üblichen Bereich. Nur unter Windows aufgerufen — Linux behält die festen
    /// V4L2-Grenzen.
    /// </summary>
    private void DetectDshowExposureRange(VideoCapture capture)
    {
        var original = capture.Get(VideoCaptureProperties.Exposure);

        capture.Set(VideoCaptureProperties.Exposure, DshowExposureProbeLow);
        var min = capture.Get(VideoCaptureProperties.Exposure);
        capture.Set(VideoCaptureProperties.Exposure, DshowExposureProbeHigh);
        var max = capture.Get(VideoCaptureProperties.Exposure);

        capture.Set(VideoCaptureProperties.Exposure, original);

        if (min < max)
        {
            MinExposure = min;
            MaxExposure = max;
        }
        else
        {
            MinExposure = DshowFallbackMinExposure;
            MaxExposure = DshowFallbackMaxExposure;
        }
    }

    public void Stop()
    {
        lock (_captureLock)
        {
            _capture?.Dispose();
            _capture = null;
        }
    }

    public Mat? GrabFrame()
    {
        lock (_captureLock)
        {
            if (_capture is null || !_capture.IsOpened())
            {
                return null;
            }

            var frame = new Mat();
            if (!_capture.Read(frame) || frame.Empty())
            {
                frame.Dispose();
                return null;
            }

            return frame;
        }
    }

    public void Dispose() => Stop();

    // Unter DSHOW von Start() am Gerät ermittelt, unter V4L2 die festen Grenzen.
    public double MinExposure { get; private set; } = V4L2MinExposure;
    public double MaxExposure { get; private set; } = V4L2MaxExposure;

    /// <summary>
    /// DSHOW-Belichtung ist log2(Sekunden): eine Verdopplung der Helligkeit ist EINE
    /// Stufe mehr, nicht der doppelte Wert. Multiplikativ zu rechnen würde hier die
    /// Regelrichtung umkehren (der Bereich ist negativ) und bei 0 festhängen.
    /// Unter V4L2 bleibt es bei der linearen Standard-Rechnung.
    /// </summary>
    public double ScaleExposure(double current, double brightnessRatio)
    {
        if (!IsDshow)
        {
            return current * brightnessRatio;
        }

        return brightnessRatio <= 0
            ? current
            : current + System.Math.Log2(brightnessRatio);
    }

    public bool AutoExposure
    {
        get
        {
            lock (_captureLock)
            {
                if (_capture is null) return false;
                var raw = _capture.Get(VideoCaptureProperties.AutoExposure);
                if (!IsDshow)
                {
                    return System.Math.Abs(raw - V4L2AutoExposureAuto) < 0.5;
                }

                // DSHOW: nur ein exakt einem Modus entsprechender Wert ist aussagekräftig,
                // sonst (typisch -1 = "nicht gemeldet") gilt der zuletzt gesetzte Soll-Zustand.
                if (System.Math.Abs(raw - DshowAutoExposureAuto) < 0.01) return true;
                if (System.Math.Abs(raw - DshowAutoExposureManual) < 0.01) return false;
                return _autoExposureRequested;
            }
        }
        set
        {
            lock (_captureLock)
            {
                _autoExposureRequested = value;
                var manual = IsDshow ? DshowAutoExposureManual : V4L2AutoExposureManual;
                var auto = IsDshow ? DshowAutoExposureAuto : V4L2AutoExposureAuto;
                _capture?.Set(VideoCaptureProperties.AutoExposure, value ? auto : manual);
            }
        }
    }

    public double Exposure
    {
        get
        {
            lock (_captureLock)
            {
                return _capture?.Get(VideoCaptureProperties.Exposure) ?? 0.0;
            }
        }
        set
        {
            lock (_captureLock)
            {
                _capture?.Set(VideoCaptureProperties.Exposure, value);
            }
        }
    }

    public double MinFocus => 0.0;
    public double MaxFocus => 255.0;

    public bool AutoFocus
    {
        get
        {
            lock (_captureLock)
            {
                if (_capture is null) return false;
                var raw = _capture.Get(VideoCaptureProperties.AutoFocus);
                if (!IsDshow)
                {
                    return raw > 0.5;
                }

                // DSHOW: nur 0/1 sind aussagekräftig — die OCAL meldet 2 ("unbekannt"),
                // obwohl sich der Fokus manuell setzen lässt. Dann gilt der Soll-Zustand.
                if (System.Math.Abs(raw - 1.0) < 0.01) return true;
                if (System.Math.Abs(raw) < 0.01) return false;
                return _autoFocusRequested;
            }
        }
        set
        {
            lock (_captureLock)
            {
                _autoFocusRequested = value;
                _capture?.Set(VideoCaptureProperties.AutoFocus, value ? 1.0 : 0.0);
            }
        }
    }

    public double Focus
    {
        get
        {
            lock (_captureLock)
            {
                return _capture?.Get(VideoCaptureProperties.Focus) ?? 0.0;
            }
        }
        set
        {
            lock (_captureLock)
            {
                _capture?.Set(VideoCaptureProperties.Focus, value);
            }
        }
    }
}

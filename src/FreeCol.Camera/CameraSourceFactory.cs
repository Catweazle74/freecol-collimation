namespace FreeCol.Camera;

/// <summary>
/// Zentrale Erzeugung der Live-Kameraquellen. Bündelt die Konstruktion an einer Stelle,
/// statt sie über das ViewModel zu verstreuen — eine neue Quelle bedeutet eine neue
/// Methode hier, nicht ein weiteres <c>new</c> irgendwo im UI.
/// </summary>
/// <remarks>
/// Die Methoden liefern bewusst die konkreten Quelltypen (nicht nur
/// <see cref="ICameraSource"/>), weil die Aufrufer quellen-spezifische Member nutzen
/// (z. B. <c>MinExposure</c>/<c>GainMax</c> bei Alpaca/ASI). Die Konstruktoren legen nur
/// Parameter ab; das tatsächliche Öffnen der Hardware passiert erst in <c>Start()</c>.
/// </remarks>
public sealed class CameraSourceFactory
{
    /// <summary>UVC-/V4L2-Kamera über OpenCV-VideoCapture.</summary>
    public OpenCvVideoCaptureSource CreateUvc(int deviceIndex, int width = 0, int height = 0)
        => new(deviceIndex, width, height);

    /// <summary>ASCOM-Alpaca-/INDIGO-Netzwerkkamera.</summary>
    public AlpacaCameraSource CreateAlpaca(string host, int port, int deviceNumber = 0, bool https = false)
        => new(host, port, deviceNumber, https);

    /// <summary>Native ZWO-ASI-Kamera (Direkt-USB).</summary>
    public AsiCameraSource CreateAsi(int cameraIndex = 0)
        => new(cameraIndex);

    /// <summary>ASCOM-Alpaca-/INDIGO-Fokuser (kein <see cref="ICameraSource"/>, aber
    /// dieselbe Erzeugungs-Bündelung).</summary>
    public AlpacaFocuserClient CreateAlpacaFocuser(string host, int port, int deviceNumber = 0, bool https = false)
        => new(host, port, deviceNumber, https);
}

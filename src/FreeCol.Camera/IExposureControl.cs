namespace FreeCol.Camera;

/// <summary>
/// Capability-Interface für Quellen, deren Belichtung gesteuert werden kann.
/// Implementierungen entscheiden Einheit und Range — V4L2 z.B. liefert die
/// absolute Exposure in 100-µs-Schritten, andere Backends nutzen andere Einheiten.
/// </summary>
public interface IExposureControl
{
    /// <summary>true ⇒ Kamera regelt automatisch, false ⇒ manueller Wert via <see cref="Exposure"/>.</summary>
    bool AutoExposure { get; set; }

    /// <summary>Aktueller manueller Belichtungswert in backend-spezifischer Einheit.</summary>
    double Exposure { get; set; }

    /// <summary>Untere Grenze für <see cref="Exposure"/> (für UI-Slider).</summary>
    double MinExposure { get; }

    /// <summary>Obere Grenze für <see cref="Exposure"/>.</summary>
    double MaxExposure { get; }

    /// <summary>
    /// Übersetzt einen gewünschten Helligkeitsfaktor (&gt;1 = heller) in den neuen
    /// Belichtungswert — die Rechenvorschrift hängt an der Skala des Backends und ist
    /// deshalb hier und nicht in der Regelung angesiedelt. Standard ist die LINEARE
    /// Skala (Sekunden bzw. 100-µs-Einheiten): doppelte Belichtung = doppelter Wert.
    /// Backends mit logarithmischer Skala überschreiben das (siehe
    /// <see cref="OpenCvVideoCaptureSource"/> unter DirectShow).
    /// </summary>
    double ScaleExposure(double current, double brightnessRatio) => current * brightnessRatio;
}

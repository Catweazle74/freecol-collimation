using OpenCvSharp;

namespace FreeCol.Camera;

/// <summary>
/// Abstraktion über eine Bildquelle (UVC-Kamera, Datei, Test-Fake).
/// Implementierungen besitzen ihre nativen Ressourcen und müssen via
/// <see cref="IDisposable.Dispose"/> freigegeben werden.
/// </summary>
public interface ICameraSource : IDisposable
{
    bool IsRunning { get; }

    /// <summary>Öffnet die Quelle. Mehrfacher Aufruf ist erlaubt und ein No-op.</summary>
    void Start();

    /// <summary>Schließt die Quelle. Mehrfacher Aufruf ist erlaubt und ein No-op.</summary>
    void Stop();

    /// <summary>
    /// Liefert das nächste Frame als neue <see cref="Mat"/>. Der Aufrufer übernimmt
    /// das Disposing. Gibt <c>null</c> zurück, wenn die Quelle nicht läuft oder
    /// kein Frame verfügbar ist.
    /// </summary>
    Mat? GrabFrame();
}

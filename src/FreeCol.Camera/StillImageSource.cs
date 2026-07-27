using OpenCvSharp;

namespace FreeCol.Camera;

/// <summary>
/// Lädt ein Standbild vom Pfad und liefert es bei jedem <see cref="GrabFrame"/>-Aufruf
/// als unabhängige Kopie. Praktisch für UI- und Algorithmus-Tests ohne Hardware.
/// </summary>
public sealed class StillImageSource : ICameraSource
{
    private readonly string _path;
    private readonly ImreadModes _mode;
    private Mat? _cached;

    public StillImageSource(string path, ImreadModes mode = ImreadModes.Color)
    {
        _path = path;
        _mode = mode;
    }

    public bool IsRunning => _cached is not null;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        var image = Cv2.ImRead(_path, _mode);
        if (image.Empty())
        {
            image.Dispose();
            throw new InvalidOperationException(
                $"Bild konnte nicht geladen werden: {_path}");
        }

        _cached = image;
    }

    public void Stop()
    {
        _cached?.Dispose();
        _cached = null;
    }

    public Mat? GrabFrame() => _cached?.Clone();

    public void Dispose() => Stop();
}

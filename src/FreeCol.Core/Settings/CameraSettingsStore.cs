using System;
using System.IO;
using System.Text.Json;
using FreeCol.Core.Calibration;

namespace FreeCol.Core.Settings;

/// <summary>
/// Lädt und speichert <see cref="CameraSettings"/> pro Kamera in
/// <c>$XDG_CONFIG_HOME/FreeCol/settings-&lt;key&gt;.json</c>.
/// Schlüssel-Sanitisierung wird mit <see cref="CalibrationStore.Sanitize"/>
/// geteilt, damit Setting- und Kalibrier-Dateien für dieselbe Kamera den
/// gleichen Slug verwenden.
/// </summary>
public sealed class CameraSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _baseDirectory;

    public CameraSettingsStore() : this(CalibrationStore.GetDefaultDirectory()) { }

    public CameraSettingsStore(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
    }

    public string GetPathFor(string cameraKey)
        => Path.Combine(_baseDirectory, $"settings-{CalibrationStore.Sanitize(cameraKey)}.json");

    public CameraSettings? Load(string cameraKey)
    {
        var path = GetPathFor(cameraKey);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CameraSettings>(json, JsonOptions);
    }

    public void Save(string cameraKey, CameraSettings settings)
    {
        Directory.CreateDirectory(_baseDirectory);
        var path = GetPathFor(cameraKey);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}

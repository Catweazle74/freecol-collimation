using System.IO;
using System.Text.Json;
using FreeCol.Core.Calibration;

namespace FreeCol.Core.Screws;

/// <summary>
/// Lädt und speichert <see cref="ScrewSet"/> pro Kamera in
/// <c>$XDG_CONFIG_HOME/FreeCol/screws-&lt;slug&gt;.json</c>. Slug-Sanitisierung
/// wird mit <see cref="CalibrationStore.Sanitize"/> geteilt.
/// </summary>
public sealed class ScrewStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _baseDirectory;

    public ScrewStore() : this(CalibrationStore.GetDefaultDirectory()) { }

    public ScrewStore(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
    }

    public string GetPathFor(string cameraKey)
        => Path.Combine(_baseDirectory, $"screws-{CalibrationStore.Sanitize(cameraKey)}.json");

    public ScrewSet Load(string cameraKey)
    {
        var path = GetPathFor(cameraKey);
        if (!File.Exists(path))
        {
            return ScrewSet.Default;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ScrewSet>(json, JsonOptions) ?? ScrewSet.Default;
    }

    public void Save(string cameraKey, ScrewSet set)
    {
        Directory.CreateDirectory(_baseDirectory);
        var path = GetPathFor(cameraKey);
        File.WriteAllText(path, JsonSerializer.Serialize(set, JsonOptions));
    }
}

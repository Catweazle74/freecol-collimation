using System.IO;
using System.Text.Json;
using FreeCol.Core.Calibration;

namespace FreeCol.Ui;

public sealed class WindowStateStore
{
    private const string FileName = "window-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _baseDirectory;

    public WindowStateStore() : this(CalibrationStore.GetDefaultDirectory()) { }

    public WindowStateStore(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
    }

    public string Path => System.IO.Path.Combine(_baseDirectory, FileName);

    public PersistedWindowState? Load()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        var json = File.ReadAllText(Path);
        return JsonSerializer.Deserialize<PersistedWindowState>(json, JsonOptions);
    }

    public void Save(PersistedWindowState state)
    {
        Directory.CreateDirectory(_baseDirectory);
        File.WriteAllText(Path, JsonSerializer.Serialize(state, JsonOptions));
    }
}

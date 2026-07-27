using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FreeCol.Core.Calibration;

namespace FreeCol.Core.Markings;

/// <summary>
/// Lädt und speichert <see cref="MarkingSet"/> pro Kamera in
/// <c>$XDG_CONFIG_HOME/FreeCol/markings-&lt;slug&gt;.json</c>.
/// Slug-Sanitisierung wird mit <see cref="CalibrationStore.Sanitize"/> geteilt.
/// </summary>
public sealed class MarkingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _baseDirectory;

    public MarkingStore() : this(CalibrationStore.GetDefaultDirectory()) { }

    public MarkingStore(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
    }

    public string GetPathFor(string cameraKey)
        => Path.Combine(_baseDirectory, $"markings-{CalibrationStore.Sanitize(cameraKey)}.json");

    public MarkingSet Load(string cameraKey)
    {
        var path = GetPathFor(cameraKey);
        if (!File.Exists(path))
        {
            return MarkingSet.Default;
        }

        var json = File.ReadAllText(path);
        // Migration: ältere Versionen serialisierten den OAZ-Rand-Slot unter dem
        // (geometrisch missverständlichen) Schlüssel „Tubus". Wenn ausschließlich
        // der alte Schlüssel vorhanden ist, in „OazRand" umbenennen, bevor wir an
        // den Deserializer geben — ein Save nach dem Load schreibt dann das neue
        // Schema, der Migrations-Pfad braucht im Folgelauf nicht mehr zu greifen.
        var node = JsonNode.Parse(json);
        if (node is JsonObject obj)
        {
            var changed = false;
            if (obj["Tubus"] is { } legacyTubus && obj["OazRand"] is null)
            {
                obj["OazRand"] = legacyTubus.DeepClone();
                obj.Remove("Tubus");
                changed = true;
            }
            // Migration: der Linse-Slot (Phase-3-IST) kam später dazu. Ältere
            // Dateien ohne diesen Schlüssel würden den positionsbasierten Record
            // mit Linse=null deserialisieren → wir spritzen einen Default-Slot ein.
            if (obj["Linse"] is null)
            {
                obj["Linse"] = JsonSerializer.SerializeToNode(
                    Marking.Default(MarkingKind.Linse), JsonOptions);
                changed = true;
            }
            if (changed) json = obj.ToJsonString();
        }
        return JsonSerializer.Deserialize<MarkingSet>(json, JsonOptions) ?? MarkingSet.Default;
    }

    public void Save(string cameraKey, MarkingSet set)
    {
        Directory.CreateDirectory(_baseDirectory);
        var path = GetPathFor(cameraKey);
        File.WriteAllText(path, JsonSerializer.Serialize(set, JsonOptions));
    }
}

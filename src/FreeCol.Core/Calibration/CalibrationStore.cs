using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;

namespace FreeCol.Core.Calibration;

/// <summary>
/// Persistiert und lädt <see cref="CalibrationResult"/> pro Kamera. Standard-
/// Verzeichnis ist <c>$XDG_CONFIG_HOME/FreeCol/</c> (auf Linux), bzw. die
/// jeweilige Plattform-AppData-Entsprechung. Pro Kamera-Key wird eine eigene
/// Datei <c>calibration-&lt;key&gt;.json</c> abgelegt.
/// </summary>
public sealed class CalibrationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _baseDirectory;

    public CalibrationStore() : this(GetDefaultDirectory()) { }

    public CalibrationStore(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
    }

    public string BaseDirectory => _baseDirectory;

    public string GetPathFor(string cameraKey)
        => Path.Combine(_baseDirectory, $"calibration-{Sanitize(cameraKey)}.json");

    public CalibrationResult? Load(string cameraKey)
    {
        var path = GetPathFor(cameraKey);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<CalibrationDto>(json, JsonOptions);
        return dto?.ToResult();
    }

    public void Save(string cameraKey, CalibrationResult result)
    {
        Directory.CreateDirectory(_baseDirectory);
        var path = GetPathFor(cameraKey);
        var dto = CalibrationDto.FromResult(result);
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
    }

    public static string GetDefaultDirectory()
    {
        var configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(configDir))
        {
            configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }
        return Path.Combine(configDir, "FreeCol");
    }

    /// <summary>
    /// Macht aus einem freien Kameranamen (z.B. "HD Pro Webcam C920") einen
    /// dateinamen-tauglichen Slug: ungültige Zeichen und Leerzeichen → '_',
    /// alles in Kleinbuchstaben.
    /// </summary>
    public static string Sanitize(string cameraKey)
    {
        if (string.IsNullOrWhiteSpace(cameraKey))
        {
            return "default";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = cameraKey.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c == ' ' || c == ':' || Array.IndexOf(invalid, c) >= 0)
            {
                chars[i] = '_';
            }
        }
        return new string(chars).ToLowerInvariant();
    }

    // OpenCvSharp's Point2f serialisiert nicht out-of-the-box, deshalb ein eigenes DTO.
    private sealed record CalibrationDto(
        double OpticalCenterX,
        double OpticalCenterY,
        double FitRadius,
        double RmsResidual,
        int SampleCount,
        bool OrientationConfirmed,
        DateTimeOffset Timestamp,
        // Default 0 = Legacy/unbekannt — ältere Dateien ohne dieses Feld
        // deserialisieren weiterhin klaglos (siehe CalibrationResult).
        int FrameWidth = 0,
        int FrameHeight = 0)
    {
        public CalibrationResult ToResult() => new(
            new Point2f((float)OpticalCenterX, (float)OpticalCenterY),
            FitRadius, RmsResidual, SampleCount, OrientationConfirmed, Timestamp,
            FrameWidth, FrameHeight);

        public static CalibrationDto FromResult(CalibrationResult r) => new(
            r.OpticalCenter.X, r.OpticalCenter.Y,
            r.FitRadius, r.RmsResidual, r.SampleCount, r.OrientationConfirmed, r.Timestamp,
            r.FrameWidth, r.FrameHeight);
    }
}

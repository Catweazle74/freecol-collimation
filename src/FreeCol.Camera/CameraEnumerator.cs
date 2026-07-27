using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;

namespace FreeCol.Camera;

/// <summary>
/// Listet sichtbare Kameras pro Plattform auf: Linux über sysfs
/// (<c>/sys/class/video4linux/</c>), Windows über DirectShow-COM-Interop
/// (<see cref="DirectShowInterop"/>). Andere Plattformen liefern eine leere Liste,
/// bis ihre Enumeration ergänzt wird.
/// </summary>
public static class CameraEnumerator
{
    public static IReadOnlyList<CameraDevice> List()
    {
        if (OperatingSystem.IsLinux())
        {
            return ListLinux();
        }

        if (OperatingSystem.IsWindows())
        {
            return ListWindows();
        }

        return Array.Empty<CameraDevice>();
    }

    private static IReadOnlyList<CameraDevice> ListLinux()
    {
        var result = new List<CameraDevice>();
        var root = new DirectoryInfo("/sys/class/video4linux");
        if (!root.Exists)
        {
            return result;
        }

        foreach (var node in root.EnumerateDirectories("video*"))
        {
            var deviceIndex = ParseVideoIndex(node.Name);
            if (deviceIndex < 0)
            {
                continue;
            }

            // V4L2-Knoten mit Sub-Index 0 sind die eigentlichen Capture-Endpoints;
            // andere Indizes gehören zu Metadaten- oder Hilfs-Streams (z.B. C920
            // legt video1 als Begleit-Device an, das per VideoCapture nicht öffnen ist).
            var subIndexPath = Path.Combine(node.FullName, "index");
            var namePath = Path.Combine(node.FullName, "name");
            if (!File.Exists(subIndexPath) || !File.Exists(namePath))
            {
                continue;
            }

            if (!int.TryParse(File.ReadAllText(subIndexPath).Trim(), out var subIndex) || subIndex != 0)
            {
                continue;
            }

            var name = File.ReadAllText(namePath).Trim();
            var serial = FindSerial(node);
            result.Add(new CameraDevice(deviceIndex, name, serial));
        }

        result.Sort((a, b) => a.Index.CompareTo(b.Index));
        return result;
    }

    private static int ParseVideoIndex(string name)
        => name.StartsWith("video", StringComparison.Ordinal)
           && int.TryParse(name.AsSpan(5), out var i)
            ? i
            : -1;

    // Höchstens so viele Verzeichnis-Ebenen aufwärts vom (aufgelösten) V4L2-Knoten
    // durchsuchen, bevor wir aufgeben — der kanonische Pfad endet auf
    // .../usbX/X-Y/X-Y:1.0/video4linux/videoN, die Seriennummer liegt im
    // USB-Geräteknoten X-Y drei Ebenen darüber.
    private const int MaxSerialSearchLevels = 5;

    /// <summary>
    /// Sucht ausgehend vom V4L2-Knoten <paramref name="videoNode"/> nach einer
    /// <c>serial</c>-Datei in den darüberliegenden sysfs-Verzeichnissen. Wichtig:
    /// Aufgelöst wird der <c>videoN</c>-Knoten SELBST (der Symlink in
    /// <c>/sys/class/video4linux</c>) in den kanonischen <c>/sys/devices</c>-Baum —
    /// NICHT der <c>device</c>-Unterlink: dessen relatives Ziel löst .NET lexikalisch
    /// gegen den unaufgelösten <c>/sys/class</c>-Pfad auf und landet im Nirgendwo
    /// (z.B. <c>/sys/3-2:1.0</c>). Liefert <c>null</c>, wenn keine Seriennummer
    /// gefunden wird oder der Zugriff fehlschlägt — die Kamera bleibt dann
    /// unverändert über den Namen nutzbar.
    /// </summary>
    private static string? FindSerial(DirectoryInfo videoNode)
    {
        try
        {
            var current = videoNode.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo
                          ?? videoNode;

            for (var level = 0; level < MaxSerialSearchLevels && current is not null; level++)
            {
                var serialPath = Path.Combine(current.FullName, "serial");
                if (File.Exists(serialPath))
                {
                    var serial = File.ReadAllText(serialPath).Trim();
                    if (!string.IsNullOrEmpty(serial))
                    {
                        return serial;
                    }
                }
                current = current.Parent;
            }
        }
        catch (Exception)
        {
            // sysfs-Zugriff ist best-effort: fehlende Datei, fehlende Rechte oder
            // ein nicht auflösbarer Symlink dürfen die Geräte-Enumeration nicht
            // zum Absturz bringen — die Kamera bleibt dann ohne Seriennummer nutzbar.
            return null;
        }

        return null;
    }

    /// <summary>
    /// Zählt die DirectShow-Video-Eingabegeräte (Kategorie
    /// <c>VideoInputDeviceCategory</c>) über <see cref="DirectShowInterop"/> auf. Der
    /// Enumerations-Index ENTSPRICHT dem DirectShow-Kamera-Index, den OpenCV mit dem
    /// <c>CAP_DSHOW</c>-Backend anspricht (siehe <see cref="OpenCvVideoCaptureSource"/>).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<CameraDevice> ListWindows()
    {
        var result = new List<CameraDevice>();
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(DirectShowInterop.ClsidSystemDeviceEnum);
            if (enumeratorType is null
                || Activator.CreateInstance(enumeratorType) is not DirectShowInterop.ICreateDevEnum systemDeviceEnum)
            {
                return result;
            }

            var category = DirectShowInterop.CategoryVideoInputDevice;
            var hresult = systemDeviceEnum.CreateClassEnumerator(in category, out var monikerEnum, dwFlags: 0);
            if (hresult != 0 || monikerEnum is null)
            {
                // S_FALSE (1): keine Geräte dieser Kategorie im System — kein Fehler.
                return result;
            }

            var index = 0;
            var monikers = new DirectShowInterop.IMoniker[1];
            while (monikerEnum.Next(1, monikers, out var fetched) == 0 && fetched == 1)
            {
                TryAddWindowsDevice(result, monikers[0], index);
                index++;
            }
        }
        catch (Exception)
        {
            // COM-Enumeration ist best-effort: ein nicht erzeugbarer Enumerator oder
            // eine Treiber-Eigenheit darf die gesamte Geräte-Enumeration nicht zum
            // Absturz bringen — dann bleibt die Liste leer.
            return Array.Empty<CameraDevice>();
        }

        return result;
    }

    /// <summary>
    /// Liest FriendlyName/DevicePath eines einzelnen DirectShow-Monikers und fügt das
    /// Gerät bei Erfolg <paramref name="result"/> hinzu. Scheitert das Auslesen (z.B.
    /// Treiber-Eigenheit), wird NUR dieses eine Gerät übersprungen — analog zum
    /// Linux-Pfad in <see cref="FindSerial"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void TryAddWindowsDevice(List<CameraDevice> result, DirectShowInterop.IMoniker moniker, int index)
    {
        try
        {
            var propertyBagIid = typeof(DirectShowInterop.IPropertyBag).GUID;
            moniker.BindToStorage(pbc: null, pmkToLeft: null, in propertyBagIid, out var propertyBagObj);
            if (propertyBagObj is not DirectShowInterop.IPropertyBag propertyBag)
            {
                return;
            }

            var name = ReadStringProperty(propertyBag, "FriendlyName");
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            var devicePath = ReadStringProperty(propertyBag, "DevicePath");
            result.Add(new CameraDevice(index, name, SerialForDevicePath(devicePath)));
        }
        catch (Exception)
        {
            // Siehe Doku oben: einzelnes Gerät überspringen, Enumeration fortsetzen.
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadStringProperty(DirectShowInterop.IPropertyBag propertyBag, string propertyName)
    {
        object value = string.Empty;
        var hresult = propertyBag.Read(propertyName, ref value, IntPtr.Zero);
        return hresult == 0 ? value as string : null;
    }

    // Windows-Gerätepfade haben die Form
    // "\\?\usb#vid_xxxx&pid_xxxx#SERIAL#{guid}". Das dritte '#'-Segment (Index 2)
    // ist die USB-Seriennummer — SOFERN es kein '&' enthält. Mit '&' handelt es sich
    // um eine Windows-Instanz-ID zusammengesetzter oder serienloser Geräte, aus der
    // keine echte Seriennummer extrahierbar ist.
    private const int SerialSegmentIndex = 2;

    /// <summary>
    /// Extrahiert die USB-Seriennummer aus einem Windows-DevicePath, sofern vorhanden.
    /// Plattformneutral und ohne COM-Abhängigkeit, damit die Logik unabhängig vom
    /// Betriebssystem unit-testbar ist.
    /// </summary>
    /// <param name="devicePath">Windows-Gerätepfad (z.B. aus DirectShow-DevicePath).</param>
    /// <returns>Die Seriennummer, oder <c>null</c>, wenn keine ermittelbar ist.</returns>
    internal static string? SerialFromDevicePath(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath))
        {
            return null;
        }

        var segments = devicePath.Split('#');
        if (segments.Length <= SerialSegmentIndex)
        {
            return null;
        }

        var candidate = segments[SerialSegmentIndex];
        return candidate.Contains('&') ? null : candidate;
    }

    /// <summary>
    /// Ermittelt die Seriennummer zu einem DevicePath — zuerst aus dem Pfad selbst,
    /// und falls dieser nur eine Instanz-ID trägt, über den übergeordneten
    /// USB-Geräteknoten. Letzteres betrifft zusammengesetzte Geräte (<c>&amp;MI_nn</c>,
    /// z.B. die OCAL): deren Kamera-Interface hat keine eigene Seriennummer, sie steht
    /// erst am USB-Geräteknoten darüber.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? SerialForDevicePath(string? devicePath)
    {
        var direct = SerialFromDevicePath(devicePath);
        if (direct is not null)
        {
            return direct;
        }

        var instanceId = InstanceIdFromDevicePath(devicePath);
        return instanceId is null
            ? null
            : SerialFromDeviceId(DeviceInstanceInterop.ParentDeviceId(instanceId));
    }

    /// <summary>
    /// Wandelt einen DevicePath in die zugehörige Geräte-Instanz-ID um:
    /// <c>\\?\usb#vid_a000&amp;pid_b111&amp;mi_00#8&amp;2f1c4d36&amp;0&amp;0000#{guid}\global</c>
    /// wird zu <c>USB\VID_A000&amp;PID_B111&amp;MI_00\8&amp;2F1C4D36&amp;0&amp;0000</c>.
    /// Plattformneutral und ohne P/Invoke, damit unit-testbar.
    /// </summary>
    internal static string? InstanceIdFromDevicePath(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath))
        {
            return null;
        }

        var path = devicePath;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        var segments = path.Split('#');
        if (segments.Length <= SerialSegmentIndex
            || segments[0].Length == 0
            || segments[1].Length == 0
            || segments[SerialSegmentIndex].Length == 0)
        {
            return null;
        }

        // Instanz-IDs führt Windows in Großschreibung.
        return $"{segments[0]}\\{segments[1]}\\{segments[SerialSegmentIndex]}".ToUpperInvariant();
    }

    /// <summary>
    /// Extrahiert die Seriennummer aus einer Geräte-Instanz-ID der Form
    /// <c>USB\VID_A000&amp;PID_B111\20211029</c> — das letzte Segment, sofern es kein
    /// <c>'&amp;'</c> enthält (dann wäre es eine von Windows vergebene Instanz-ID
    /// statt einer echten Seriennummer). Plattformneutral und unit-testbar.
    /// </summary>
    internal static string? SerialFromDeviceId(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        var last = deviceId.Split('\\')[^1];
        return last.Length == 0 || last.Contains('&') ? null : last;
    }
}

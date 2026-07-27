using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ASCOM.Alpaca.Clients;
using ASCOM.Alpaca.Discovery;
using ASCOM.Common;
using ASCOM.Common.Alpaca;
using OpenCvSharp;

namespace FreeCol.Camera;

/// <summary>Im Netzwerk gefundene Alpaca-Kamera (für die Auswahl in der UI).</summary>
public sealed record AlpacaFoundCamera(string Name, string Host, int Port, int DeviceNumber)
{
    public override string ToString() => $"{Name} — {Host}:{Port}/{DeviceNumber}";
}

/// <summary>
/// Bildquelle über das ASCOM-Alpaca-Protokoll (HTTP/REST). Crossplattform und
/// herstellerneutral: spricht jeden Alpaca-Server an (INDIGO-Alpaca-Agent unter
/// Linux/macOS, ASCOM Remote unter Windows, native Alpaca-Geräte). Direkte
/// Host:Port-Verbindung — funktioniert auch lokal (FreeCol auf dem Server selbst,
/// <c>localhost</c>) und wenn UDP-Discovery über Subnetze/Container scheitert.
///
/// Eine Belichtung dauert Sekunden; <see cref="GrabFrame"/> blockiert solange und
/// ist für On-Demand-Aufnahmen gedacht (Sterntest), nicht für eine Highspeed-
/// Live-Schleife. Liefert das rohe Bild als 16-bit-Graustufen-<see cref="Mat"/>
/// (Bayer bleibt erhalten — die Sterntest-Aufbereitung binnt ohnehin).
/// </summary>
public sealed class AlpacaCameraSource : ICameraSource, IExposureControl
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _deviceNumber;
    private readonly bool _https;
    private AlpacaCamera? _camera;
    private bool _running;
    private double _exposureSeconds = 1.0;

    public AlpacaCameraSource(string host, int port, int deviceNumber = 0, bool https = false)
    {
        _host = host;
        _port = port;
        _deviceNumber = deviceNumber;
        _https = https;
    }

    public bool IsRunning => _running;

    private static bool IsLoopback(string host)
        => host.StartsWith("127.", StringComparison.Ordinal)
           || host == "::1"
           || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sucht Alpaca-Kameras im lokalen Netz per UDP-Discovery (Standard-Port 32227).
    /// Liefert je Kamera Host, REST-Port und Geräte-Nummer (z. B. INDIGO meldet
    /// Port 7624). Findet auch lokale Instanzen (localhost).
    /// </summary>
    public static Task<List<AlpacaFoundCamera>> DiscoverCamerasAsync(
        int discoveryPort = 32227, double durationSeconds = 2.0)
        => Task.Run(() =>
        {
            var result = new List<AlpacaFoundCamera>();
            using var disc = new AlpacaDiscovery();
            // numberOfPolls, pollInterval(ms), discoveryPort, discoveryDuration(s),
            // resolveDnsName, useIpV4, useIpV6, serviceType
            disc.StartDiscovery(2, 100, discoveryPort, durationSeconds, false, true, false, ServiceType.Http);
            var waited = 0;
            while (!disc.DiscoveryComplete && waited < (int)(durationSeconds * 1000) + 3000)
            {
                Thread.Sleep(100);
                waited += 100;
            }
            // Dasselbe Gerät wird über mehrere Netz-Interfaces gemeldet (z. B.
            // 127.0.0.1 UND die LAN-IP). Per UniqueId zusammenfassen und dabei die
            // nicht-loopback-Adresse bevorzugen (von überall erreichbar; lokal geht
            // sie ebenso). Fällt zurück auf Name+Port+Gerät, wenn keine UniqueId.
            var byKey = new Dictionary<string, AlpacaFoundCamera>();
            foreach (var d in disc.GetAscomDevices(DeviceTypes.Camera))
            {
                var host = !string.IsNullOrEmpty(d.IpAddress) ? d.IpAddress : d.HostName;
                var key = !string.IsNullOrEmpty(d.UniqueId)
                    ? d.UniqueId
                    : $"{d.AscomDeviceName}|{d.IpPort}|{d.AlpacaDeviceNumber}";
                var cam = new AlpacaFoundCamera(d.AscomDeviceName, host, d.IpPort, d.AlpacaDeviceNumber);
                if (!byKey.TryGetValue(key, out var existing) || (IsLoopback(existing.Host) && !IsLoopback(host)))
                    byKey[key] = cam;
            }
            result.AddRange(byKey.Values);
            return result;
        });

    public void Start()
    {
        if (_running) return;
        var cam = new AlpacaCamera(
            _https ? ServiceType.Https : ServiceType.Http,
            _host, _port, _deviceNumber, strictCasing: false, logger: null);
        try
        {
            // Protokoll-konform verbinden: Platform-7-Geräte nutzen das asynchrone
            // Connect() + Connecting (referenzgezählt → trennt das Gerät NICHT,
            // solange andere Clients es noch nutzen); ältere Geräte das Connected-
            // Flag. ClientExtensions.ConnectAsync wählt anhand der Interface-Version
            // automatisch das Richtige (Connected=true/false ist für Clients
            // deprecated und würde laufende Sitzungen anderer Apps stören).
            cam.ConnectAsync(DeviceTypes.Camera, cam.InterfaceVersion, CancellationToken.None, 500, null)
               .GetAwaiter().GetResult();
            // Schneller Binär-Transfer statt JSON (26 MP wären als JSON riesig).
            try { cam.ImageArrayTransferType = ImageArrayTransferType.BestAvailable; } catch { /* optional */ }
        }
        catch (Exception ex)
        {
            try { cam.Dispose(); } catch { /* ignore */ }
            throw new InvalidOperationException(
                $"Alpaca-Kamera nicht verbunden ({_host}:{_port}, Gerät {_deviceNumber}): {ex.Message}", ex);
        }
        _camera = cam;
        _running = true;
    }

    public void Stop()
    {
        if (_camera is { } c)
        {
            // Protokoll-konform trennen (referenzgezählt: zählt nur den eigenen
            // Client herunter, das Gerät bleibt für andere verbunden).
            try
            {
                c.DisconnectAsync(DeviceTypes.Camera, c.InterfaceVersion, CancellationToken.None, 500, null)
                 .GetAwaiter().GetResult();
            }
            catch { /* best effort */ }
            try { c.Dispose(); } catch { /* ignore */ }
            _camera = null;
        }
        _running = false;
    }

    public Mat? GrabFrame()
    {
        var cam = _camera;
        if (cam is null || !_running) return null;
        try
        {
            cam.StartExposure(_exposureSeconds, true); // Light-Frame
            var maxWaitMs = (int)(_exposureSeconds * 1000) + 60_000; // Belichtung + Download-Reserve
            var waited = 0;
            while (!cam.ImageReady)
            {
                Thread.Sleep(100);
                waited += 100;
                if (waited > maxWaitMs) return null;
            }
            return ToGray16(cam.ImageArray);
        }
        catch
        {
            return null;
        }
    }

    // ASCOM ImageArray: int[,] (mono/Bayer, [NumX,NumY]) oder int[,,] (Farbebenen).
    // → CV_16UC1-Mat (rows=Höhe, cols=Breite); Werte auf ushort geklemmt.
    private static Mat? ToGray16(object? imageArray)
    {
        if (imageArray is int[,] a2)
        {
            int w = a2.GetLength(0), h = a2.GetLength(1);
            var values = new short[(long)w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    values[(long)y * w + x] = ToU16(a2[x, y]);
            return BuildMat(h, w, values);
        }
        if (imageArray is int[,,] a3)
        {
            int w = a3.GetLength(0), h = a3.GetLength(1), planes = a3.GetLength(2);
            var values = new short[(long)w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    long sum = 0;
                    for (int p = 0; p < planes; p++) sum += a3[x, y, p];
                    values[(long)y * w + x] = ToU16((int)(sum / Math.Max(1, planes)));
                }
            return BuildMat(h, w, values);
        }
        return null;
    }

    private static short ToU16(int v) => unchecked((short)(v < 0 ? 0 : v > 65535 ? 65535 : v));

    private static Mat BuildMat(int rows, int cols, short[] values)
    {
        var mat = new Mat(rows, cols, MatType.CV_16UC1);
        Marshal.Copy(values, 0, mat.Data, values.Length);
        return mat;
    }

    // --- IExposureControl (Belichtung in Sekunden) ---------------------------
    public bool AutoExposure { get => false; set { /* Alpaca: keine Auto-Belichtung */ } }

    public double Exposure
    {
        get => _exposureSeconds;
        set => _exposureSeconds = Math.Clamp(value, MinExposure, MaxExposure);
    }

    public double MinExposure { get { try { return _camera?.ExposureMin ?? 0.0001; } catch { return 0.0001; } } }
    public double MaxExposure { get { try { return _camera?.ExposureMax ?? 3600.0; } catch { return 3600.0; } } }

    // --- Gain (nicht Teil von IExposureControl) ------------------------------
    public double Gain
    {
        get { try { return _camera?.Gain ?? 0; } catch { return 0; } }
        set { try { if (_camera is { } c) c.Gain = (short)value; } catch { /* manche Kameras: Gain-Liste statt Wert */ } }
    }
    public double GainMin { get { try { return _camera?.GainMin ?? 0; } catch { return 0; } } }
    public double GainMax { get { try { return _camera?.GainMax ?? 0; } catch { return 0; } } }

    public void Dispose() => Stop();
}

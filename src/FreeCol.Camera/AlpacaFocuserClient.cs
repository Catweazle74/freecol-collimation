using System;
using System.Threading;
using ASCOM.Alpaca.Clients;
using ASCOM.Common;
using ASCOM.Common.Alpaca;

namespace FreeCol.Camera;

/// <summary>
/// Fokuser-Steuerung über das ASCOM-Alpaca-Protokoll (gleicher Server/Port wie die
/// Alpaca-Kamera, eigener Gerätetyp <c>focuser</c>). Für den Sterntest: Fokus ändern,
/// ohne zu einer Fremdsoftware wechseln zu müssen (defokussieren für den Donut,
/// zurück zum Fokus für die Kontrolle).
///
/// Alpaca kennt absolute Fokuser (Move = Zielposition in Schritten) und relative
/// (Move = Delta). <see cref="MoveRelative"/> abstrahiert das: bei absoluten Geräten
/// wird auf [0, MaxStep] geklemmt ab der aktuellen Position gefahren.
/// Alle Aufrufe machen HTTP-Roundtrips → nicht auf dem UI-Thread aufrufen.
/// </summary>
public sealed class AlpacaFocuserClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _deviceNumber;
    private readonly bool _https;
    private AlpacaFocuser? _focuser;

    public AlpacaFocuserClient(string host, int port, int deviceNumber = 0, bool https = false)
    {
        _host = host;
        _port = port;
        _deviceNumber = deviceNumber;
        _https = https;
    }

    public bool IsConnected => _focuser is not null;

    public void Start()
    {
        if (_focuser is not null) return;
        var f = new AlpacaFocuser(
            _https ? ServiceType.Https : ServiceType.Http,
            _host, _port, _deviceNumber, strictCasing: false, logger: null);
        try
        {
            // Referenzgezählt verbinden wie bei der Kamera (Platform 7: Connect(),
            // ältere Geräte: Connected-Flag) — stört keine parallelen Clients.
            f.ConnectAsync(DeviceTypes.Focuser, f.InterfaceVersion, CancellationToken.None, 500, null)
             .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            try { f.Dispose(); } catch { /* ignore */ }
            throw new InvalidOperationException(
                $"Alpaca-Fokuser nicht verbunden ({_host}:{_port}, Gerät {_deviceNumber}): {ex.Message}", ex);
        }
        _focuser = f;
    }

    public void Stop()
    {
        if (_focuser is { } f)
        {
            try
            {
                f.DisconnectAsync(DeviceTypes.Focuser, f.InterfaceVersion, CancellationToken.None, 500, null)
                 .GetAwaiter().GetResult();
            }
            catch { /* best effort */ }
            try { f.Dispose(); } catch { /* ignore */ }
            _focuser = null;
        }
    }

    /// <summary>Aktuelle Position in Schritten (nur bei absoluten Fokusern aussagekräftig).</summary>
    public int Position { get { try { return _focuser?.Position ?? 0; } catch { return 0; } } }

    /// <summary>Fährt das Gerät gerade?</summary>
    public bool IsMoving { get { try { return _focuser?.IsMoving ?? false; } catch { return false; } } }

    /// <summary>Absoluter Fokuser (Move = Zielposition) oder relativer (Move = Delta)?</summary>
    public bool IsAbsolute { get { try { return _focuser?.Absolute ?? false; } catch { return false; } } }

    /// <summary>Maximale Position in Schritten (absolute Fokuser); 0 wenn unbekannt.</summary>
    public int MaxStep { get { try { return _focuser?.MaxStep ?? 0; } catch { return 0; } } }

    /// <summary>Sensor-Temperatur in °C, wenn der Fokuser eine liefert.</summary>
    public double? Temperature { get { try { return _focuser?.Temperature; } catch { return null; } } }

    /// <summary>Fährt eine absolute Zielposition an (nur absolute Fokuser).</summary>
    public void MoveTo(int targetPosition)
    {
        if (_focuser is not { } f) return;
        var max = MaxStep;
        f.Move(max > 0 ? Math.Clamp(targetPosition, 0, max) : targetPosition);
    }

    /// <summary>Fährt um <paramref name="delta"/> Schritte (negativ = rein/Richtung Tubus).</summary>
    public void MoveRelative(int delta)
    {
        if (_focuser is not { } f) return;
        if (IsAbsolute) MoveTo(Position + delta);
        else f.Move(delta);
    }

    /// <summary>Stoppt eine laufende Bewegung sofort.</summary>
    public void Halt()
    {
        try { _focuser?.Halt(); } catch { /* manche Geräte: nicht unterstützt */ }
    }

    public void Dispose() => Stop();
}

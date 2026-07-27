using FreeCol.Camera;
using FreeCol.Core.Imaging;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace FreeCol.Core.Tests.Imaging;

// Live-Probe gegen einen laufenden Alpaca-Server (INDIGO mit Alpaca-Agent auf
// localhost:7624, Device 0). Kein Pass/Fail: überspringt, wenn kein Server läuft.
// Verbindet, belichtet 1 s, lädt das ImageArray, baut eine Mat und schreibt eine
// normalisierte Vorschau nach /tmp/alpaca-frame.png.
public class AlpacaProbe
{
    private readonly ITestOutputHelper _out;
    public AlpacaProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void ConnectAndGrab()
    {
        using var src = new AlpacaCameraSource("localhost", 7624, deviceNumber: 0);
        try { src.Start(); }
        catch (System.Exception ex) { _out.WriteLine($"kein Alpaca-Server: {ex.Message}"); return; }

        _out.WriteLine($"verbunden. Exposure {src.MinExposure}..{src.MaxExposure} s, Gain {src.GainMin}..{src.GainMax}");
        src.Exposure = 1.0;
        using var frame = src.GrabFrame();
        if (frame is null) { _out.WriteLine("kein Frame erhalten"); src.Stop(); return; }
        _out.WriteLine($"frame {frame.Width}x{frame.Height} type={frame.Type()}");
        using var disp = StarFramePrep.ToDisplayGray8(frame);
        Cv2.ImWrite("/tmp/alpaca-frame.png", disp);
        _out.WriteLine("Vorschau: /tmp/alpaca-frame.png");
        src.Stop();
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover()
    {
        var found = await AlpacaCameraSource.DiscoverCamerasAsync();
        _out.WriteLine($"{found.Count} Alpaca-Kamera(s) gefunden:");
        foreach (var c in found) _out.WriteLine("  " + c);
    }
}

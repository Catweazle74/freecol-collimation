using FreeCol.Camera;
using FreeCol.Core.Imaging;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace FreeCol.Core.Tests.Imaging;

// Live-Probe gegen eine direkt am USB angeschlossene ZWO-ASI-Kamera (natives SDK).
// Kein Pass/Fail: überspringt, wenn keine Kamera/SDK da ist. Enumeriert, belichtet
// kurz und schreibt eine normalisierte Vorschau nach /tmp/asi-frame.png.
public class AsiProbe
{
    private readonly ITestOutputHelper _out;
    public AsiProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void EnumerateAndGrab()
    {
        System.Collections.Generic.List<AsiFoundCamera> cams;
        try { cams = AsiCameraSource.DiscoverCameras(); }
        catch (System.Exception ex) { _out.WriteLine($"ASI-SDK nicht ladbar: {ex.Message}"); return; }

        _out.WriteLine($"{cams.Count} ASI-Kamera(s):");
        foreach (var c in cams) _out.WriteLine($"  [{c.Index}] id={c.Id} {c.Name} {c.Width}x{c.Height}");
        if (cams.Count == 0) return;

        using var src = new AsiCameraSource(0);
        try { src.Start(); }
        catch (System.Exception ex) { _out.WriteLine($"Start fehlgeschlagen: {ex.Message}"); return; }

        src.Exposure = 0.05; // 50 ms (Tageslicht/Innenraum)
        src.Gain = 100;
        using var frame = src.GrabFrame();
        if (frame is null) { _out.WriteLine("kein Frame"); src.Stop(); return; }
        _out.WriteLine($"frame {frame.Width}x{frame.Height} type={frame.Type()}");
        using var disp = StarFramePrep.ToDisplayGray8(frame);
        Cv2.ImWrite("/tmp/asi-frame.png", disp);
        _out.WriteLine("Vorschau: /tmp/asi-frame.png");
        src.Stop();
    }
}

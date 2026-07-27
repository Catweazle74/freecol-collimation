using System;
using System.IO;
using System.Text;
using System.Text.Json;
using FreeCol.Core.Imaging;
using OpenCvSharp;
using Xunit;

namespace FreeCol.Core.Tests.Imaging;

// Mess-Werkzeug (kein Pass/Fail): lässt jeden Auto-Markierungs-Detektor auf seinem
// eigenen, scharfgestellten Snapshot laufen (im Live-Betrieb fokussiert die Pipeline
// per Markierung — eine Single-Frame-Fixture wäre damit nicht repräsentativ) und
// vergleicht das Ergebnis mit den geklickten Soll-Werten aus
// tests/.../TestData/automark-reference.json. Schreibt die px-Abweichungen nach
// /tmp/automark-diag.txt und zeichnet Detektion (rot/gelb/cyan/magenta) gegen Soll
// (grün) auf das jeweilige Frame nach /tmp/automark-diag.<kind>.png.
// Detektor-Hints (Tubus für HSR/Sek, HSR für Marker) kommen aus dem GT — so misst
// jeder Detektor isoliert seine eigene Genauigkeit, ohne dass ein Fehler aus dem
// Vorgänger den Folge-Wert vergiftet.
public class AutoMarkingDiagnostics
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "TestData");
    private static string GtPath => Path.Combine(DataDir, "automark-reference.json");

    // Ausgabeverzeichnis der Diagnose-Artefakte: unter Linux unverändert /tmp,
    // unter Windows das Temp-Verzeichnis des Nutzers (dort gibt es kein C:\tmp).
    private static string OutDir => OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp";
    private static string Out(string file) => Path.Combine(OutDir, file);

    private readonly record struct Gt(string Snapshot, double X, double Y, double R);

    private static Gt ReadGt(JsonElement root, string key)
    {
        var m = root.GetProperty(key);
        return new Gt(
            m.GetProperty("Snapshot").GetString() ?? throw new InvalidOperationException($"{key}: Snapshot fehlt"),
            m.GetProperty("CenterX").GetDouble(),
            m.GetProperty("CenterY").GetDouble(),
            m.GetProperty("RadiusX").GetDouble());
    }

    private static string Dev(double dx, double dy, double detR, double gtR)
    {
        var d = Math.Sqrt(dx * dx + dy * dy);
        var rPct = gtR > 0 ? (detR - gtR) / gtR * 100 : 0;
        return $"Δcenter={d:0.0}px (dx={dx:+0.0;-0.0}, dy={dy:+0.0;-0.0})  Δr={detR - gtR:+0.0;-0.0}px ({rPct:+0.0;-0.0}%)";
    }

    private static Mat LoadGray(Gt gt)
    {
        var path = Path.Combine(DataDir, gt.Snapshot);
        if (!File.Exists(path)) throw new FileNotFoundException($"Snapshot fehlt: {path}");
        return Cv2.ImRead(path, ImreadModes.Grayscale);
    }

    [Fact]
    public void Measure()
    {
        if (!File.Exists(GtPath)) return;

        using var doc = JsonDocument.Parse(File.ReadAllText(GtPath));
        var root = doc.RootElement;
        var gtOaz = ReadGt(root, "OazRand");
        var gtHsr = ReadGt(root, "HauptspiegelReflex");
        var gtSek = ReadGt(root, "Sekundaer");
        var gtMar = ReadGt(root, "Marker");

        var log = new StringBuilder();
        var green = new Scalar(0, 255, 0);

        // OAZ-Rand auf seinem eigenen Snapshot.
        using (var gray = LoadGray(gtOaz))
        using (var draw = Cv2.ImRead(Path.Combine(DataDir, gtOaz.Snapshot), ImreadModes.Color))
        {
            Cv2.Circle(draw, new Point((int)gtOaz.X, (int)gtOaz.Y), (int)gtOaz.R, green, 1);
            var r = new OazRandDetector().Detect(gray);
            if (r is null) log.AppendLine("OAZ    NULL");
            else
            {
                Cv2.Circle(draw, new Point((int)r.Center.X, (int)r.Center.Y), (int)r.Radius, new Scalar(0, 0, 255), 2);
                log.AppendLine($"OAZ    {Dev(r.Center.X - gtOaz.X, r.Center.Y - gtOaz.Y, r.Radius, gtOaz.R)}");
            }
            Cv2.ImWrite(Out("automark-diag.oazrand.png"), draw);
        }

        // HSR auf seinem Snapshot, Hint = GT-OAZ-Rand (isoliert).
        using (var gray = LoadGray(gtHsr))
        using (var draw = Cv2.ImRead(Path.Combine(DataDir, gtHsr.Snapshot), ImreadModes.Color))
        {
            Cv2.Circle(draw, new Point((int)gtHsr.X, (int)gtHsr.Y), (int)gtHsr.R, green, 1);
            var oazHint = new OazRandResult(new Point2f((float)gtOaz.X, (float)gtOaz.Y), gtOaz.R);
            var r = new HauptspiegelReflexDetector().Detect(gray, oazHint);
            if (r is null) log.AppendLine("HSR    NULL");
            else
            {
                Cv2.Circle(draw, new Point((int)r.Center.X, (int)r.Center.Y), (int)r.Radius, new Scalar(0, 220, 255), 2);
                log.AppendLine($"HSR    {Dev(r.Center.X - gtHsr.X, r.Center.Y - gtHsr.Y, r.Radius, gtHsr.R)}");
            }
            Cv2.ImWrite(Out("automark-diag.hsr.png"), draw);
        }

        // Sekundär auf seinem Snapshot, Hints = GT-OAZ + GT-HSR (isoliert).
        using (var gray = LoadGray(gtSek))
        using (var draw = Cv2.ImRead(Path.Combine(DataDir, gtSek.Snapshot), ImreadModes.Color))
        {
            Cv2.Circle(draw, new Point((int)gtSek.X, (int)gtSek.Y), (int)gtSek.R, green, 1);
            var oazHint = new OazRandResult(new Point2f((float)gtOaz.X, (float)gtOaz.Y), gtOaz.R);
            var hsrHint = new HauptspiegelReflexResult(new Point2f((float)gtHsr.X, (float)gtHsr.Y), gtHsr.R);
            var r = new SekundaerSilhouetteDetector().Detect(gray, oazHint, hsrHint);
            if (r is null) log.AppendLine("Sekund NULL");
            else
            {
                Cv2.Ellipse(draw, new RotatedRect(
                    new Point2f(r.Center.X, r.Center.Y),
                    new Size2f((float)(r.RadiusX * 2), (float)(r.RadiusY * 2)),
                    (float)r.AngleDeg), new Scalar(255, 220, 0), 2);
                log.AppendLine($"Sekund {Dev(r.Center.X - gtSek.X, r.Center.Y - gtSek.Y, Math.Max(r.RadiusX, r.RadiusY), gtSek.R)}");
            }
            Cv2.ImWrite(Out("automark-diag.sekundaer.png"), draw);
        }

        // Marker auf seinem Snapshot, Hint = GT-HSR-Zentrum (isoliert).
        using (var gray = LoadGray(gtMar))
        using (var draw = Cv2.ImRead(Path.Combine(DataDir, gtMar.Snapshot), ImreadModes.Color))
        {
            Cv2.Circle(draw, new Point((int)gtMar.X, (int)gtMar.Y), (int)Math.Max(2, gtMar.R), green, 1);

            // FS-Reflexion (Außengrenze für Marker+Linse) — Hint = GT-HSR.
            var hsrHint = new HauptspiegelReflexResult(new Point2f((float)gtHsr.X, (float)gtHsr.Y), gtHsr.R);
            var fs = new FangspiegelReflexDetector().Detect(gray, hsrHint);
            if (fs is null) log.AppendLine("FS     NULL");
            else
            {
                Cv2.Circle(draw, new Point((int)fs.Center.X, (int)fs.Center.Y), (int)fs.Radius, new Scalar(0, 165, 255), 2);
                log.AppendLine($"FS     center=({fs.Center.X:0},{fs.Center.Y:0}) r={fs.Radius:0.0}  (Marker-Δzentrum={Math.Sqrt(Math.Pow(fs.Center.X - gtMar.X, 2) + Math.Pow(fs.Center.Y - gtMar.Y, 2)):0.0}px)");
            }

            var r = new MarkerRingDetector().Detect(gray, gtHsr.X, gtHsr.Y);
            if (r is null) log.AppendLine("Marker NULL");
            else
            {
                Cv2.Circle(draw, new Point((int)r.Center.X, (int)r.Center.Y), (int)r.Radius, new Scalar(255, 0, 255), 2);
                var dx = r.Center.X - gtMar.X;
                var dy = r.Center.Y - gtMar.Y;
                var d = Math.Sqrt(dx * dx + dy * dy);
                log.AppendLine($"Marker Δcenter={d:0.0}px (dx={dx:+0.0;-0.0}, dy={dy:+0.0;-0.0})  Δr={r.Radius - gtMar.R:+0.0;-0.0}px");
            }
            Cv2.ImWrite(Out("automark-diag.marker.png"), draw);

            // Vergrößerter Ausschnitt um das HSR-Zentrum für die Sichtprüfung.
            var win = 110;
            var roi = new Rect((int)gtHsr.X - win, (int)gtHsr.Y - win, 2 * win, 2 * win)
                      & new Rect(0, 0, draw.Width, draw.Height);
            using var crop = new Mat(draw, roi);
            using var zoom = new Mat();
            Cv2.Resize(crop, zoom, new Size(512, 512), 0, 0, InterpolationFlags.Nearest);
            Cv2.ImWrite(Out("automark-diag.marker-zoom.png"), zoom);
        }

        File.WriteAllText(Out("automark-diag.txt"), log.ToString());
    }
}

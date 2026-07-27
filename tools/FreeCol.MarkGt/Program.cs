using System;
using System.IO;
using System.Text;
using System.Text.Json;
using OpenCvSharp;

// GT-Klick-Tool für die Auto-Marking-Fixture: zeigt pro Markierungs-Snapshot
// das Bild im Vollformat, der Nutzer klickt 3 Punkte auf dem sichtbaren Rand
// der Markierung, wir fitten einen Kreis durch die Punkte und schreiben am
// Ende eine automark-reference.json im neuen Schema (pro Markierung:
// Snapshot-Datei + Center/Radius). Eine 8-fach gezoomte Lupe folgt dem
// Cursor (oben rechts), damit auch der kleine Marker präzise getroffen wird.
//
// Aufruf: dotnet run --project tools/FreeCol.MarkGt [-- testDataDir]
// Default-testDataDir: tests/FreeCol.Core.Tests/TestData

var dataDir = args.Length > 0 ? args[0] : Path.Combine("tests", "FreeCol.Core.Tests", "TestData");
if (!Directory.Exists(dataDir))
{
    Console.Error.WriteLine($"TestData-Verzeichnis nicht gefunden: {dataDir}");
    return 1;
}

var targets = new[]
{
    new Target("OazRand", "oazrand.png"),
    new Target("HauptspiegelReflex", "hsr.png"),
    new Target("Sekundaer", "sekundaer.png"),
    new Target("Marker", "marker.png"),
};

var results = new List<(string Key, Target T, double Cx, double Cy, double R)>();
foreach (var t in targets)
{
    var path = Path.Combine(dataDir, t.File);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Snapshot fehlt: {path} — übersprungen.");
        continue;
    }
    var (cx, cy, r) = PickCircle(path, t);
    results.Add((t.Key, t, cx, cy, r));
    Console.WriteLine($"  → {t.Key}: c=({cx:0.0},{cy:0.0}) r={r:0.0}");
}

var outPath = Path.Combine(dataDir, "automark-reference.json");
using var fs = File.Create(outPath);
using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
w.WriteStartObject();
foreach (var (key, t, cx, cy, r) in results)
{
    w.WriteStartObject(key);
    w.WriteString("Snapshot", t.File);
    w.WriteNumber("CenterX", cx);
    w.WriteNumber("CenterY", cy);
    w.WriteNumber("RadiusX", r);
    w.WriteNumber("RadiusY", r);
    w.WriteEndObject();
}
w.WriteEndObject();
w.Flush();
Console.WriteLine($"GT geschrieben: {outPath}");
return 0;

static (double cx, double cy, double r) PickCircle(string path, Target t)
{
    using var img = Cv2.ImRead(path, ImreadModes.Color);
    if (img.Empty()) throw new InvalidOperationException($"Bild leer: {path}");
    var title = $"{t.Key}  —  3× Klick auf den Rand   (Lupe oben rechts)";
    Cv2.NamedWindow(title, WindowFlags.Normal);
    Cv2.ResizeWindow(title, 1280, 720);

    var picks = new List<Point2f>();
    var cursor = new Point(img.Width / 2, img.Height / 2);
    const int zoomFactor = 8;
    const int zoomBoxSrc = 40;  // 40×40 px aus dem Bild → 320×320 px im Inset

    void Redraw()
    {
        using var vis = img.Clone();
        foreach (var p in picks) Cv2.Circle(vis, new Point((int)p.X, (int)p.Y), 4, new Scalar(0, 255, 255), -1);
        if (picks.Count == 3 && TryFit3(picks[0], picks[1], picks[2], out var c, out var r))
        {
            Cv2.Circle(vis, new Point((int)c.X, (int)c.Y), (int)r, new Scalar(0, 255, 0), 2);
            Cv2.DrawMarker(vis, new Point((int)c.X, (int)c.Y), new Scalar(0, 255, 0), MarkerTypes.Cross, 30, 2);
        }
        DrawZoomInset(vis, cursor, zoomBoxSrc, zoomFactor);
        Cv2.PutText(vis, $"{picks.Count}/3 Klicks  —  ENTER=ok, BACKSPACE=undo, ESC=abort",
            new Point(10, 30), HersheyFonts.HersheySimplex, 0.8, new Scalar(255, 255, 255), 2);
        Cv2.ImShow(title, vis);
    }

    void OnMouse(MouseEventTypes ev, int x, int y, MouseEventFlags _, IntPtr __)
    {
        if (ev == MouseEventTypes.MouseMove)
        {
            cursor = new Point(x, y);
            Redraw();
            return;
        }
        if (ev != MouseEventTypes.LButtonDown) return;
        if (picks.Count >= 3) return;
        picks.Add(new Point2f(x, y));
        Redraw();
    }

    Cv2.SetMouseCallback(title, OnMouse);
    Redraw();

    while (true)
    {
        var key = Cv2.WaitKey(0);
        if (key == 27) // ESC
        {
            Cv2.DestroyWindow(title);
            throw new OperationCanceledException("Abgebrochen.");
        }
        if (key == 8 || key == 65288) // Backspace
        {
            if (picks.Count > 0) picks.RemoveAt(picks.Count - 1);
            Redraw();
            continue;
        }
        if (key == 13 || key == 10) // Enter
        {
            if (picks.Count != 3)
            {
                Console.WriteLine($"  noch {3 - picks.Count} Klick(s)…");
                continue;
            }
            break;
        }
    }
    Cv2.DestroyWindow(title);

    if (!TryFit3(picks[0], picks[1], picks[2], out var cf, out var rf))
        throw new InvalidOperationException("Drei Punkte sind kollinear, kein Kreis möglich.");
    return (cf.X, cf.Y, rf);
}

// 8-fach Cursor-Lupe oben rechts. Quellbox 40×40 → Inset 320×320, plus Crosshair
// auf der genauen Cursor-Position, damit der Klick präzise gesetzt werden kann.
static void DrawZoomInset(Mat vis, Point cursor, int srcSize, int factor)
{
    var src = new Rect(cursor.X - srcSize / 2, cursor.Y - srcSize / 2, srcSize, srcSize)
              & new Rect(0, 0, vis.Width, vis.Height);
    if (src.Width < 4 || src.Height < 4) return;
    using var crop = new Mat(vis, src);
    var dstSize = new Size(srcSize * factor, srcSize * factor);
    using var zoom = new Mat();
    Cv2.Resize(crop, zoom, dstSize, 0, 0, InterpolationFlags.Nearest);
    var dst = new Rect(vis.Width - zoom.Width - 10, 10, zoom.Width, zoom.Height)
              & new Rect(0, 0, vis.Width, vis.Height);
    if (dst.Width < 4 || dst.Height < 4) return;
    var roi = new Mat(vis, dst);
    using var clipped = new Mat(zoom, new Rect(0, 0, dst.Width, dst.Height));
    clipped.CopyTo(roi);
    Cv2.Rectangle(vis, dst, new Scalar(255, 255, 255), 2);
    // Crosshair an der Cursor-Position innerhalb des Insets.
    var cxIn = dst.X + (cursor.X - src.X) * factor;
    var cyIn = dst.Y + (cursor.Y - src.Y) * factor;
    Cv2.Line(vis, new Point(cxIn - 12, cyIn), new Point(cxIn + 12, cyIn), new Scalar(0, 255, 255), 1);
    Cv2.Line(vis, new Point(cxIn, cyIn - 12), new Point(cxIn, cyIn + 12), new Scalar(0, 255, 255), 1);
}

// Kreis aus 3 Punkten: Mittelpunkt = Schnittpunkt der Mittelsenkrechten.
static bool TryFit3(Point2f p1, Point2f p2, Point2f p3, out Point2f center, out double radius)
{
    double ax = p1.X, ay = p1.Y, bx = p2.X, by = p2.Y, cx = p3.X, cy = p3.Y;
    double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
    if (Math.Abs(d) < 1e-6) { center = default; radius = 0; return false; }
    double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
    double ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d;
    double uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;
    center = new Point2f((float)ux, (float)uy);
    radius = Math.Sqrt((ux - ax) * (ux - ax) + (uy - ay) * (uy - ay));
    return true;
}

record Target(string Key, string File);

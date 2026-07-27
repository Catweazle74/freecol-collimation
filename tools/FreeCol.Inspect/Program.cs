using System;
using System.IO;
using System.Linq;
using FreeCol.Core.Imaging;
using OpenCvSharp;

var path = args.Length > 0 ? args[0] : FindLatestSnapshot();
if (path is null || !File.Exists(path))
{
    Console.Error.WriteLine($"Kein Bild gefunden: {path ?? "(keins angegeben, keiner in ~/Bilder/FreeCol/)"}");
    return 1;
}

Console.WriteLine($"Bild  : {path}");

using var src = Cv2.ImRead(path);
if (src.Empty())
{
    Console.Error.WriteLine("Bild konnte nicht geladen werden.");
    return 1;
}
Console.WriteLine($"Größe : {src.Width}x{src.Height}, Kanäle: {src.Channels()}");

using var gray = Preprocessor.ToGrayscaleBlurred(src, blurKernel: 5);

var detector = new EllipseDetector
{
    MinContourArea = 100,
    MinAxisRatio = 0.3,
};
var raw = detector.Detect(gray);

var clusterer = new EllipseClusterer
{
    CenterTolerancePixels = 5.0,
    SizeTolerancePercent = 0.10,
};
var clustered = clusterer.Merge(raw);

var analyzer = new CollimationAnalyzer();
var analysis = analyzer.Analyze(clustered);

Console.WriteLine($"Roh-Ellipsen   : {raw.Count} (Detector: MinContourArea={detector.MinContourArea}, MinAxisRatio={detector.MinAxisRatio:F2})");
Console.WriteLine($"Nach Cluster   : {clustered.Count} (Clusterer: CenterTol={clusterer.CenterTolerancePixels}px, SizeTol={clusterer.SizeTolerancePercent:P0})");
Console.WriteLine();
Console.WriteLine("Analyse:");
Console.WriteLine($"  OAZ-Rand: {Describe(analysis.OazRand)}");
Console.WriteLine($"  Marker : {Describe(analysis.Marker)}");
if (analysis.Offset is { } off)
{
    Console.WriteLine($"  Versatz: x={off.X:+0.0;-0.0;0.0}, y={off.Y:+0.0;-0.0;0.0} px, |Δ|={off.Magnitude:F1} px");
}
else
{
    Console.WriteLine("  Versatz: —");
}
Console.WriteLine();
Console.WriteLine($"{"#",3}  {"Center",-16}  {"Size",-16}  {"Winkel",7}  {"Area",10}  {"Ratio",6}");
foreach (var (e, i) in clustered.Take(10).Select((e, i) => (e, i)))
{
    var ratio = Math.Min(e.Size.Width, e.Size.Height) / Math.Max(e.Size.Width, e.Size.Height);
    Console.WriteLine(
        $"{i + 1,3}  ({e.Center.X,6:F1},{e.Center.Y,6:F1})  ({e.Size.Width,6:F1}x{e.Size.Height,6:F1})  {e.AngleDegrees,6:F1}°  {e.ContourArea,10:F0}  {ratio,6:F2}");
}
return 0;

static string Describe(EllipseFit? e) => e is null
    ? "—"
    : $"Center=({e.Center.X:F1},{e.Center.Y:F1})  Size=({e.Size.Width:F1}x{e.Size.Height:F1})  Area={e.ContourArea:F0}";

static string? FindLatestSnapshot()
{
    var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    if (string.IsNullOrEmpty(pictures)) return null;
    var dir = Path.Combine(pictures, "FreeCol");
    if (!Directory.Exists(dir)) return null;
    return Directory.EnumerateFiles(dir, "snapshot-*.png")
        .OrderByDescending(File.GetCreationTime)
        .FirstOrDefault();
}

using System;
using System.IO;
using System.Linq;
using FreeCol.Core.Imaging;
using OpenCvSharp;
using Xunit;

namespace FreeCol.Core.Tests.Imaging;

// Mess-/Sichtwerkzeug (kein Pass/Fail) für den ASI-Sterntest: liest die
// defokussierten Stern-FITS aus TestBilder/Donuts (Fokus-Hub von OAZ ganz
// eingefahren bis ganz ausgefahren), entbayert per 2×2-Binning, normalisiert
// und legt ein Übersichts-Grid + Einzel-PNGs unter /tmp ab, damit man den
// Defokus-Verlauf beurteilen und den besten Analyse-Fokus finden kann.
public class StarTestDiagnostics
{
    private const string DonutsDir =
        "/home/marc/Sourcen/FreeCol/TestBilder/Donuts/2026-06-25/SNAPSHOT/UVIRCut";

    private static string[] Frames() =>
        Directory.Exists(DonutsDir)
            ? Directory.GetFiles(DonutsDir, "*.fits")
                .Where(f => !Path.GetFileName(f).StartsWith("BAD_", StringComparison.Ordinal))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

    private static Mat ToDisplayGray(Mat raw16) => StarFramePrep.ToDisplayGray8(raw16);

    private static string Stamp(string path)
    {
        // Dateiname: 2026-06-25_23-21-43_UVIRCut_...
        var name = Path.GetFileName(path);
        var parts = name.Split('_');
        return parts.Length >= 2 ? parts[1] : name;
    }

    [Fact]
    public void DetectDonuts()
    {
        var frames = Frames();
        if (frames.Length == 0) return;

        var log = new System.Text.StringBuilder();
        log.AppendLine("#   Zeit       Rout   Rin    Obstr  |Offset|  dx     dy");
        var detector = new DonutDetector();

        for (int i = 0; i < frames.Length; i++)
        {
            using var raw = FitsReader.ReadGray16(frames[i]);
            using var gray = ToDisplayGray(raw);
            var d = detector.Detect(gray);

            using var bgr = new Mat();
            Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
            if (d is null)
            {
                log.AppendLine($"{i:00}  {Stamp(frames[i])}   --- nicht erkannt ---");
            }
            else
            {
                Cv2.Circle(bgr, (Point)d.OuterCenter, (int)d.OuterRadius, new Scalar(0, 255, 0), 2);
                Cv2.Circle(bgr, (Point)d.InnerCenter, (int)d.InnerRadius, new Scalar(0, 0, 255), 2);
                Cv2.DrawMarker(bgr, (Point)d.OuterCenter, new Scalar(0, 255, 0), MarkerTypes.Cross, 14, 1);
                Cv2.DrawMarker(bgr, (Point)d.InnerCenter, new Scalar(0, 0, 255), MarkerTypes.Cross, 14, 1);
                Cv2.Line(bgr, (Point)d.OuterCenter, (Point)d.InnerCenter, new Scalar(0, 220, 255), 1);
                log.AppendLine(
                    $"{i:00}  {Stamp(frames[i])}   {d.OuterRadius,4:0}   {d.InnerRadius,4:0}   " +
                    $"{d.Obstruction,4:0.00}   {d.OffsetMagnitude,5:0.0}    {d.Offset.X,5:+0.0;-0.0}  {d.Offset.Y,5:+0.0;-0.0}");
            }
            Cv2.ImWrite($"/tmp/donut-det-{i:00}.png", bgr);
        }

        File.WriteAllText("/tmp/donut-detect.txt", log.ToString());
    }

    [Fact]
    public void BuildOverview()
    {
        var frames = Frames();
        if (frames.Length == 0) return;

        const int cols = 4;
        int rows = (frames.Length + cols - 1) / cols;
        const int cellW = 360, cellH = 250;
        var grid = new Mat(new Size(cols * cellW, rows * cellH), MatType.CV_8UC3, Scalar.All(20));

        for (int i = 0; i < frames.Length; i++)
        {
            using var raw = FitsReader.ReadGray16(frames[i]);
            using var gray = ToDisplayGray(raw);

            // Einzel-PNG voller (gebinnter) Auflösung für Detailansicht.
            Cv2.ImWrite($"/tmp/donut-{i:00}.png", gray);

            using var thumb = new Mat();
            double scale = Math.Min((double)cellW / gray.Width, (double)(cellH - 22) / gray.Height);
            Cv2.Resize(gray, thumb, new Size((int)(gray.Width * scale), (int)(gray.Height * scale)));
            using var thumbBgr = new Mat();
            Cv2.CvtColor(thumb, thumbBgr, ColorConversionCodes.GRAY2BGR);

            int cx = (i % cols) * cellW, cy = (i / cols) * cellH;
            var roi = new Rect(cx + (cellW - thumb.Width) / 2, cy + 22, thumb.Width, thumb.Height)
                      & new Rect(0, 0, grid.Width, grid.Height);
            thumbBgr[new Rect(0, 0, roi.Width, roi.Height)].CopyTo(grid[roi]);
            Cv2.PutText(grid, $"#{i:00}  {Stamp(frames[i])}", new Point(cx + 6, cy + 16),
                HersheyFonts.HersheySimplex, 0.5, new Scalar(80, 220, 80), 1);
        }

        Cv2.ImWrite("/tmp/donuts-grid.png", grid);
    }
}

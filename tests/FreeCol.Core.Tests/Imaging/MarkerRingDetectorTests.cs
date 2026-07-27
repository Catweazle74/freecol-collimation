using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Imaging;

public class MarkerRingDetectorTests
{
    [Fact]
    public void Detect_EmptyImage_ReturnsNull()
    {
        using var img = new Mat();
        Assert.Null(new MarkerRingDetector().Detect(img, 50, 50));
    }

    [Fact]
    public void Detect_RoiTooSmall_ReturnsNull()
    {
        // Winziges Bild → der Such-ROI ist kleiner als 2·MinRadiusPx → kein Versuch.
        using var img = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(0));
        Assert.Null(new MarkerRingDetector().Detect(img, 4, 4));
    }

    private static void DrawRing(Mat img, Point center, int radius)
        => Cv2.Circle(img, center, radius, Scalar.All(230), thickness: 3, lineType: LineTypes.AntiAlias);

    // Default-AccumulatorThreshold (25) ist auf reale, kontrastarme OCAL-Ringe getunt;
    // synthetische Rings liefern weniger Akkumulator-Stimmen. Für die Hough-Pfad-Tests
    // eine permissivere Schwelle — Radiusband, ROI, Nearest-Hint bleiben wie im Default.
    private static MarkerRingDetector TestDetector(int searchRadiusPx = 30)
        => new() { AccumulatorThreshold = 14, SearchRadiusPx = searchRadiusPx };

    [Fact]
    public void Detect_BrightRingNearHint_RecoversCenterAndRadius()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(40));
        DrawRing(img, new Point(100, 100), 12);

        var r = TestDetector().Detect(img, 100, 100);

        Assert.NotNull(r);
        Assert.InRange(r!.Center.X, 93, 107);
        Assert.InRange(r.Center.Y, 93, 107);
        Assert.InRange(r.Radius, 6, 18);
    }

    [Fact]
    public void Detect_PicksRingNearestToHint()
    {
        // Zwei Ringe im Suchfenster; der Hinweis liegt am linken → dieser gewinnt
        // (der echte Marker sitzt nahe am Hinweis, der Linsen-Reflex versetzt daneben).
        using var img = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(40));
        DrawRing(img, new Point(88, 100), 10);
        DrawRing(img, new Point(120, 100), 10);

        var r = TestDetector(searchRadiusPx: 40).Detect(img, 88, 100);

        Assert.NotNull(r);
        // Näher am linken Ring (88) als am rechten (120).
        Assert.True(System.Math.Abs(r!.Center.X - 88) < System.Math.Abs(r.Center.X - 120),
            $"Center.X war {r.Center.X}");
    }

    [Fact]
    public void Detect_RingOutsideRadiusBand_ReturnsNull()
    {
        // Ring mit Radius 26 liegt über MaxRadiusPx (18) → kein Treffer im Radiusband,
        // auch bei permissiver Akkumulator-Schwelle.
        using var img = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(40));
        DrawRing(img, new Point(100, 100), 26);

        Assert.Null(TestDetector(searchRadiusPx: 40).Detect(img, 100, 100));
    }
}

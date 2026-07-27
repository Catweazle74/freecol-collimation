using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Imaging;

public class DonutDetectorTests
{
    // Synthetischer defokussierter Stern: helle Außenscheibe mit dunkler zentraler
    // Obstruktion auf schwarzem Himmel. obstructionCenter erlaubt einen Kollimations-
    // Versatz (Obstruktion gegen die Scheibe verschoben).
    private static Mat MakeDonut(int size, Point outerCenter, int outerR,
                                 Point obstructionCenter, int innerR, byte disk = 200)
    {
        var img = new Mat(size, size, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(img, outerCenter, outerR, Scalar.All(disk), thickness: -1);
        Cv2.Circle(img, obstructionCenter, innerR, Scalar.All(0), thickness: -1);
        return img;
    }

    [Fact]
    public void Detect_CenteredDonut_RecoversGeometry()
    {
        var c = new Point(100, 100);
        using var img = MakeDonut(200, c, outerR: 60, c, innerR: 24);

        var result = new DonutDetector().Detect(img);

        Assert.NotNull(result);
        Assert.InRange(result!.OuterRadius, 55, 65);
        Assert.InRange(result.InnerRadius, 20, 29);
        Assert.InRange(result.Obstruction, 0.30, 0.50);
        Assert.InRange(result.OuterCenter.X, 95, 105);
        Assert.InRange(result.OuterCenter.Y, 95, 105);
        // Perfekt zentriert → Kollimations-Versatz nahe 0.
        Assert.InRange(result.OffsetMagnitude, 0, 6);
    }

    [Fact]
    public void Detect_OffsetObstruction_ReportsCollimationOffset()
    {
        // Realistischer (kleiner) Versatz: Obstruktion 8 px nach +x verschoben →
        // Offset (innen−außen) zeigt nach +x. Größere Versätze sprengen den
        // Median-Radius-Outlier-Filter, der auf nahezu zentrierte Features ausgelegt ist.
        using var img = MakeDonut(200, new Point(100, 100), 60, new Point(108, 100), 24);

        var result = new DonutDetector().Detect(img);

        Assert.NotNull(result);
        Assert.True(result!.Offset.X > 2.5, $"Offset.X war {result.Offset.X}");
        Assert.True(result.OffsetMagnitude > 2.5);
    }

    [Fact]
    public void Detect_EmptyImage_ReturnsNull()
    {
        using var img = new Mat();
        Assert.Null(new DonutDetector().Detect(img));
    }

    [Fact]
    public void Detect_UniformImage_ReturnsNull()
    {
        // Kein Kontrast (peak − sky < 12) → kein Donut.
        using var img = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(50));
        Assert.Null(new DonutDetector().Detect(img));
    }

    [Fact]
    public void Detect_FilledDiskWithoutObstruction_IsRejected()
    {
        // Gefüllte Scheibe ohne zentrale Obstruktion → Obstruktionsgrad ~0,
        // unter MinObstruction ⇒ verworfen (fokusnaher Stern, kein echter Donut).
        using var img = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(img, new Point(100, 100), 40, Scalar.All(200), thickness: -1);

        Assert.Null(new DonutDetector().Detect(img));
    }

    [Fact]
    public void Detect_TooLargeObstruction_IsRejected()
    {
        // Obstruktionsgrad 50/60 ≈ 0.83 > MaxObstruction (0.60) ⇒ verworfen.
        var c = new Point(100, 100);
        using var img = MakeDonut(240, c, outerR: 60, c, innerR: 50);

        Assert.Null(new DonutDetector().Detect(img));
    }

    // Dunkelt eine Bildhälfte des Donuts ab (Faktor < 1 auf die vorhandenen
    // Pixel angewandt) — betrifft nur den hellen Ring, der Himmel bleibt bei 0.
    private static void DarkenHalf(Mat img, Rect half, double factor)
    {
        using var region = new Mat(img, half);
        Cv2.Multiply(region, new Scalar(factor), region);
    }

    [Fact]
    public void Detect_UniformRing_HasNearZeroBrightnessImbalance()
    {
        var c = new Point(100, 100);
        using var img = MakeDonut(200, c, outerR: 60, c, innerR: 24);

        var result = new DonutDetector().Detect(img);

        Assert.NotNull(result);
        // Toleranz 0.05: Rundungs-/Diskretisierungsrauschen des Rasterbilds
        // (Pixel-Rundung je Strahl, kein echtes Helligkeits-Gefälle vorhanden).
        Assert.InRange(result!.BrightnessImbalance, 0, 0.05);
    }

    [Fact]
    public void Detect_DarkSectorLeft_PointsDarkDirectionLeft()
    {
        var c = new Point(100, 100);
        using var img = MakeDonut(200, c, outerR: 60, c, innerR: 24);
        // Linke Bildhälfte (x < 100) auf 60 % abdunkeln.
        DarkenHalf(img, new Rect(0, 0, 100, 200), 0.6);

        var result = new DonutDetector().Detect(img);

        Assert.NotNull(result);
        Assert.True(result!.BrightnessImbalance > 0.05,
            $"BrightnessImbalance war {result.BrightnessImbalance}");
        Assert.True(result.BrightnessDarkDirection.X < 0,
            $"BrightnessDarkDirection war {result.BrightnessDarkDirection}");
    }

    [Fact]
    public void Detect_DarkSectorTop_PointsDarkDirectionUp()
    {
        var c = new Point(100, 100);
        using var img = MakeDonut(200, c, outerR: 60, c, innerR: 24);
        // Obere Bildhälfte (y < 100, "oben" im Pixel-Koordinatensystem) abdunkeln.
        DarkenHalf(img, new Rect(0, 0, 200, 100), 0.6);

        var result = new DonutDetector().Detect(img);

        Assert.NotNull(result);
        Assert.True(result!.BrightnessImbalance > 0.05,
            $"BrightnessImbalance war {result.BrightnessImbalance}");
        // y nach unten ⇒ "oben" ist negatives y.
        Assert.True(result.BrightnessDarkDirection.Y < 0,
            $"BrightnessDarkDirection war {result.BrightnessDarkDirection}");
    }
}

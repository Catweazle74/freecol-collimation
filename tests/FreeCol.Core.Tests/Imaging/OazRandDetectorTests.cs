using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Imaging;

public class OazRandDetectorTests
{
    [Fact]
    public void Detect_EmptyImage_ReturnsNull()
    {
        var detector = new OazRandDetector();
        using var empty = new Mat();
        Assert.Null(detector.Detect(empty));
    }

    [Fact]
    public void Detect_PureBlackImage_ReturnsNull()
    {
        var detector = new OazRandDetector();
        using var img = new Mat(new Size(600, 400), MatType.CV_8UC1, Scalar.All(0));
        Assert.Null(detector.Detect(img));
    }

    [Fact]
    public void Detect_FilledCircle_ReturnsCenterAndRadius()
    {
        var detector = new OazRandDetector();
        using var img = new Mat(new Size(600, 400), MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(img, new Point(300, 200), 120, Scalar.All(255), thickness: -1);

        var result = detector.Detect(img);

        Assert.NotNull(result);
        Assert.InRange(result!.Center.X, 298, 302);
        Assert.InRange(result.Center.Y, 198, 202);
        Assert.InRange(result.Radius, 118, 124);
    }

    [Fact]
    public void Detect_OffCentreCircle_LocatesIt()
    {
        var detector = new OazRandDetector();
        using var img = new Mat(new Size(800, 600), MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(img, new Point(500, 350), 200, Scalar.All(255), thickness: -1);

        var result = detector.Detect(img);

        Assert.NotNull(result);
        Assert.InRange(result!.Center.X, 497, 503);
        Assert.InRange(result.Center.Y, 347, 353);
        Assert.InRange(result.Radius, 198, 204);
    }

    [Fact]
    public void Detect_PrefersLargestRegion()
    {
        // Ein kleines helles Korn neben dem eigentlichen Tubus-Reflex darf
        // den Kreis nicht verfälschen.
        var detector = new OazRandDetector();
        using var img = new Mat(new Size(800, 600), MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(img, new Point(400, 300), 180, Scalar.All(255), thickness: -1);
        Cv2.Circle(img, new Point(50, 50), 8, Scalar.All(255), thickness: -1);

        var result = detector.Detect(img);

        Assert.NotNull(result);
        // GradientAlt schätzt den Radius exakt, das Zentrum aber etwas weicher
        // (ein weit entferntes Störkorn zieht es leicht); ±6 px ist für die grobe
        // Vormarkierung unkritisch.
        Assert.InRange(result!.Center.X, 394, 406);
        Assert.InRange(result.Center.Y, 294, 306);
        Assert.InRange(result.Radius, 177, 183);
    }

    [Fact]
    public void Detect_TinyRegion_BelowMinRadiusFraction_IsRejected()
    {
        var detector = new OazRandDetector();
        using var img = new Mat(new Size(600, 400), MatType.CV_8UC1, Scalar.All(0));
        // Radius 20 < 0.25 * min(600,400) = 100 → unter dem konfigurierten Minimum.
        Cv2.Circle(img, new Point(300, 200), 20, Scalar.All(255), thickness: -1);

        Assert.Null(detector.Detect(img));
    }
}

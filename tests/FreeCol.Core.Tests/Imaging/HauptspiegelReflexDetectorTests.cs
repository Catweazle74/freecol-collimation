using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Imaging;

public class HauptspiegelReflexDetectorTests
{
    [Fact]
    public void Detect_EmptyImage_ReturnsNull()
    {
        var detector = new HauptspiegelReflexDetector();
        using var empty = new Mat();
        Assert.Null(detector.Detect(empty));
    }

    [Fact]
    public void Detect_BrightCircle_LocatesIt()
    {
        var detector = new HauptspiegelReflexDetector();
        using var img = new Mat(new Size(600, 400), MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(img, new Point(300, 200), 60, Scalar.All(255), thickness: -1);

        var result = detector.Detect(img);

        Assert.NotNull(result);
        Assert.InRange(result!.Center.X, 297, 303);
        Assert.InRange(result.Center.Y, 197, 203);
        Assert.InRange(result.Radius, 58, 64);
    }

    [Fact]
    public void Detect_WithTubusHint_PreferssmallerInnerCircle()
    {
        // Tubus-Kreis (radius 200) und kleinerer Reflex (radius 60) im Bild.
        // Mit Hint=Tubus muss der Detector den kleineren wählen.
        var detector = new HauptspiegelReflexDetector();
        using var img = new Mat(new Size(800, 600), MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(img, new Point(400, 300), 200, Scalar.All(180), thickness: 2);
        Cv2.Circle(img, new Point(400, 300), 60, Scalar.All(255), thickness: -1);

        var hint = new OazRandResult(new Point2f(400, 300), 200);
        var result = detector.Detect(img, hint);

        Assert.NotNull(result);
        Assert.InRange(result!.Radius, 56, 70);
    }

    [Fact]
    public void Detect_CircleTooBigForHint_ReturnsNull()
    {
        // Reflex hat 0.7 × Tubus-Radius — größer als die zulässigen 0.5.
        var detector = new HauptspiegelReflexDetector();
        using var img = new Mat(new Size(800, 600), MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(img, new Point(400, 300), 140, Scalar.All(255), thickness: -1);

        var hint = new OazRandResult(new Point2f(400, 300), 200);
        var result = detector.Detect(img, hint);

        Assert.Null(result);
    }
}

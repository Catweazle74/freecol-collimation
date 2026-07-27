using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Imaging;

public class FangspiegelReflexDetectorTests
{
    [Fact]
    public void Detect_EmptyImage_ReturnsNull()
    {
        var detector = new FangspiegelReflexDetector();
        using var empty = new Mat();
        Assert.Null(detector.Detect(empty, new HauptspiegelReflexResult(new Point2f(100, 100), 150)));
    }

    [Fact]
    public void Detect_WithoutHint_ReturnsNull()
    {
        var detector = new FangspiegelReflexDetector();
        using var img = new Mat(new Size(600, 400), MatType.CV_8UC1, Scalar.All(255));
        Assert.Null(detector.Detect(img, null));
    }

    [Fact]
    public void Detect_DarkDiscWithinBrightHsr_FitsCircle()
    {
        // OCAL-ähnlich: heller HSR (255) mit einer dunklen FS-Scheibe (r=40) und
        // einem hellen Marker-Ring im Zentrum, den der Scan überspringen muss.
        var img = new Mat(new Size(600, 400), MatType.CV_8UC1, Scalar.All(255));
        Cv2.Circle(img, new Point(300, 200), 40, Scalar.All(30), thickness: -1);
        Cv2.Circle(img, new Point(300, 200), 9, Scalar.All(255), thickness: 2);

        var detector = new FangspiegelReflexDetector();
        var hsrHint = new HauptspiegelReflexResult(new Point2f(300, 200), 150);

        var result = detector.Detect(img, hsrHint);

        Assert.NotNull(result);
        Assert.InRange(result!.Center.X, 295, 305);
        Assert.InRange(result.Center.Y, 195, 205);
        Assert.InRange(result.Radius, 34, 46);

        img.Dispose();
    }

    [Fact]
    public void Detect_UniformBright_ReturnsNull()
    {
        var detector = new FangspiegelReflexDetector();
        using var img = new Mat(new Size(600, 400), MatType.CV_8UC1, Scalar.All(255));
        Assert.Null(detector.Detect(img, new HauptspiegelReflexResult(new Point2f(300, 200), 150)));
    }
}

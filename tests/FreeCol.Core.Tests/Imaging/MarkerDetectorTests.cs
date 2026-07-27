using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Imaging;

public class MarkerDetectorTests
{
    // Linse ist etwa marker-groß; der Aufrufer leitet den Radiusbereich aus dem
    // Marker-Radius ab. Hier ein fester Bereich [6,10] um die Test-Scheibe r=8.
    private static MarkerResult? DetectNear(
        MarkerDetector d, Mat img, double cx, double cy,
        double rMin = 6, double rMax = 10, double maxOffset = 100)
        => d.Detect(img, cx, cy, rMin, rMax, maxOffset);

    [Fact]
    public void Detect_EmptyImage_ReturnsNull()
    {
        var detector = new MarkerDetector();
        using var empty = new Mat();
        Assert.Null(DetectNear(detector, empty, 100, 100));
    }

    [Fact]
    public void Detect_NoDarkCircle_ReturnsNull()
    {
        var detector = new MarkerDetector();
        using var img = new Mat(new Size(600, 400), MatType.CV_8UC1, Scalar.All(255));
        Assert.Null(DetectNear(detector, img, 300, 200));
    }

    [Fact]
    public void Detect_DarkCircleNearCenter_FindsIt()
    {
        var detector = new MarkerDetector();
        using var img = new Mat(new Size(600, 400), MatType.CV_8UC1, Scalar.All(255));
        // Dunkle Linsen-Scheibe bei (305, 197), Radius 8.
        Cv2.Circle(img, new Point(305, 197), 8, Scalar.All(20), thickness: -1);

        var result = DetectNear(detector, img, 300, 200);

        Assert.NotNull(result);
        Assert.InRange(result!.Center.X, 300, 310);
        Assert.InRange(result.Center.Y, 192, 202);
    }

    [Fact]
    public void Detect_DarkCircleBeyondOffset_ReturnsNull()
    {
        var detector = new MarkerDetector();
        using var img = new Mat(new Size(600, 400), MatType.CV_8UC1, Scalar.All(255));
        // Dunkler Kreis weit außerhalb des Suchzentrums — der Offset-Filter wirft ihn raus.
        Cv2.Circle(img, new Point(500, 300), 8, Scalar.All(20), thickness: -1);

        var result = DetectNear(detector, img, 300, 200, maxOffset: 50);

        Assert.Null(result);
    }

    [Fact]
    public void Detect_OversizedCircle_RejectedByRadiusGate()
    {
        // FS-Reflexion: großer dunkler Kreis am Zentrum, weit außerhalb des
        // Radiusbereichs [6,10] → wird verworfen (genau der gemeldete Bug, bei dem
        // die Linse als die viel größere Fangspiegel-Reflexion erkannt wurde).
        var detector = new MarkerDetector();
        using var img = new Mat(new Size(600, 400), MatType.CV_8UC1, Scalar.All(255));
        Cv2.Circle(img, new Point(300, 200), 40, Scalar.All(20), thickness: -1);

        var result = DetectNear(detector, img, 300, 200, rMin: 6, rMax: 10);

        Assert.Null(result);
    }
}

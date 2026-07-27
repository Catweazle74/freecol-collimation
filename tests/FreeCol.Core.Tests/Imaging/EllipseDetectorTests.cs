using System;
using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Imaging;

public class EllipseDetectorTests
{
    [Fact]
    public void Detect_FindsDrawnCircle_NearExpectedCenterAndSize()
    {
        using var img = new Mat(new Size(200, 200), MatType.CV_8UC1, Scalar.Black);
        Cv2.Circle(img, new Point(100, 100), 50, Scalar.White, thickness: 2);

        var detector = new EllipseDetector { MinContourArea = 50 };
        var ellipses = detector.Detect(img);

        Assert.NotEmpty(ellipses);
        var best = ellipses[0];
        Assert.InRange(best.Center.X, 95, 105);
        Assert.InRange(best.Center.Y, 95, 105);
        // gezeichneter Radius 50 → erwartete Achsenlängen ~100 (Durchmesser)
        Assert.InRange(best.Size.Width, 90, 110);
        Assert.InRange(best.Size.Height, 90, 110);
        // sortiert nach Konturfläche, also sollte die größte zuerst kommen
        Assert.True(best.ContourArea > 0);
    }

    [Fact]
    public void Detect_RejectsMulticannelInput()
    {
        using var bgr = new Mat(new Size(100, 100), MatType.CV_8UC3, Scalar.Black);
        var detector = new EllipseDetector();

        Assert.Throws<ArgumentException>(() => detector.Detect(bgr));
    }

    [Fact]
    public void Detect_IgnoresContoursBelowMinArea()
    {
        using var img = new Mat(new Size(100, 100), MatType.CV_8UC1, Scalar.Black);
        // winzige Punkt-Kontur — sollte am Flächen-Filter scheitern
        Cv2.Circle(img, new Point(20, 20), 2, Scalar.White, thickness: -1);

        var detector = new EllipseDetector { MinContourArea = 200 };
        var ellipses = detector.Detect(img);

        Assert.Empty(ellipses);
    }
}

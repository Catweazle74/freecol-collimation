using System;
using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Imaging;

public class BrightSpotDetectorTests
{
    private static Mat Black(int size = 200) => new(size, size, MatType.CV_8UC1, Scalar.All(0));

    [Fact]
    public void Find_SingleBrightBlob_ReturnsCentroid()
    {
        using var img = Black();
        Cv2.Circle(img, new Point(70, 120), 12, Scalar.All(255), thickness: -1);

        var r = BrightSpotDetector.Find(img);

        Assert.NotNull(r.Position);
        Assert.InRange(r.Position!.Value.X, 67, 73);
        Assert.InRange(r.Position.Value.Y, 117, 123);
        Assert.Equal(1, r.CandidateCount);
    }

    [Fact]
    public void Find_PicksLargestBlob()
    {
        using var img = Black();
        Cv2.Circle(img, new Point(50, 50), 6, Scalar.All(255), thickness: -1);    // klein
        Cv2.Circle(img, new Point(150, 150), 18, Scalar.All(255), thickness: -1); // groß

        var r = BrightSpotDetector.Find(img);

        Assert.NotNull(r.Position);
        Assert.InRange(r.Position!.Value.X, 145, 155);
        Assert.InRange(r.Position.Value.Y, 145, 155);
        Assert.Equal(2, r.CandidateCount);
    }

    [Fact]
    public void Find_BlobAboveMaxArea_Excluded()
    {
        using var img = Black();
        Cv2.Circle(img, new Point(100, 100), 20, Scalar.All(255), thickness: -1); // Fläche ~1256

        var r = BrightSpotDetector.Find(img, maxArea: 100);

        Assert.Null(r.Position);
        Assert.Equal(0, r.CandidateCount);
    }

    [Fact]
    public void Find_InsideEllipse_ExcludesOutsideBlobs()
    {
        using var img = Black();
        Cv2.Circle(img, new Point(100, 100), 10, Scalar.All(255), thickness: -1); // innen
        Cv2.Circle(img, new Point(170, 100), 10, Scalar.All(255), thickness: -1); // außen

        // Ellipse Ø60 (Halbachse 30) um (100,100): der Blob bei x=170 liegt außerhalb.
        var ellipse = new EllipseFit(new Point2f(100, 100), new Size2f(60, 60), 0, 0);
        var r = BrightSpotDetector.Find(img, insideEllipse: ellipse);

        Assert.NotNull(r.Position);
        Assert.InRange(r.Position!.Value.X, 95, 105);
        Assert.Equal(1, r.CandidateCount); // nur der innere Blob zählt
    }

    [Fact]
    public void Find_NonSingleChannel_Throws()
    {
        using var color = new Mat(50, 50, MatType.CV_8UC3, Scalar.All(0));
        Assert.Throws<ArgumentException>(() => BrightSpotDetector.Find(color));
    }

    [Fact]
    public void FindDarkInBright_DarkSpotInBrightDisc_ReturnsCentroid()
    {
        using var img = Black();
        Cv2.Circle(img, new Point(100, 100), 40, Scalar.All(220), thickness: -1); // heller Disc
        Cv2.Circle(img, new Point(100, 100), 8, Scalar.All(0), thickness: -1);     // dunkler Marker

        var r = BrightSpotDetector.FindDarkInBright(img);

        Assert.NotNull(r.Position);
        Assert.InRange(r.Position!.Value.X, 94, 106);
        Assert.InRange(r.Position.Value.Y, 94, 106);
    }

    [Fact]
    public void FindDarkInBright_NonSingleChannel_Throws()
    {
        using var color = new Mat(50, 50, MatType.CV_8UC3, Scalar.All(0));
        Assert.Throws<ArgumentException>(() => BrightSpotDetector.FindDarkInBright(color));
    }
}

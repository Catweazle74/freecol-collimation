using System;
using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Imaging;

public class SekundaerSilhouetteDetectorTests
{
    [Fact]
    public void Detect_EmptyImage_ReturnsNull()
    {
        var detector = new SekundaerSilhouetteDetector();
        using var empty = new Mat();
        Assert.Null(detector.Detect(empty));
    }

    [Fact]
    public void Detect_WithoutHints_ReturnsNull()
    {
        var detector = new SekundaerSilhouetteDetector();
        using var img = new Mat(new Size(600, 400), MatType.CV_8UC1, Scalar.All(128));
        Assert.Null(detector.Detect(img));
    }

    [Fact]
    public void Detect_DarkAnnulusBetweenHsrAndTubus_FitsCircle()
    {
        // Konstruiere ein OCAL-ähnliches Bild:
        //   außen (außerhalb Tubus): dunkel (0)
        //   Tubus-Inneres: hell (255)
        //   Sekundär-Annulus zwischen 60 und 130 px: dunkel
        //   HSR innerhalb 40 px: hell
        var img = new Mat(new Size(600, 400), MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(img, new Point(300, 200), 180, Scalar.All(255), thickness: -1);
        Cv2.Circle(img, new Point(300, 200), 130, Scalar.All(20), thickness: -1);
        Cv2.Circle(img, new Point(300, 200), 40, Scalar.All(255), thickness: -1);

        var detector = new SekundaerSilhouetteDetector();
        var outerHint = new OazRandResult(new Point2f(300, 200), 180);
        var innerHint = new HauptspiegelReflexResult(new Point2f(300, 200), 40);

        var result = detector.Detect(img, outerHint, innerHint);

        Assert.NotNull(result);
        Assert.InRange(result!.Center.X, 295, 305);
        Assert.InRange(result.Center.Y, 195, 205);
        var avgRadius = (result.RadiusX + result.RadiusY) / 2.0;
        // Sekundär-Radius im Bild ist 130, Gauß-Glättung verschiebt die Kante
        // um wenige Pixel — eine ±10 px-Spanne ist tolerant.
        Assert.InRange(avgRadius, 120, 140);

        img.Dispose();
    }

    [Fact]
    public void Detect_NoVisibleTransition_ReturnsNull()
    {
        // Komplett gleichmäßiges Bild ohne klare Dunkel→Hell-Kante.
        var detector = new SekundaerSilhouetteDetector();
        using var img = new Mat(new Size(600, 400), MatType.CV_8UC1, Scalar.All(128));
        var outerHint = new OazRandResult(new Point2f(300, 200), 180);
        var innerHint = new HauptspiegelReflexResult(new Point2f(300, 200), 40);

        Assert.Null(detector.Detect(img, outerHint, innerHint));
    }
}

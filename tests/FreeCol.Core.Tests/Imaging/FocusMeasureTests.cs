using FreeCol.Core.Imaging;
using OpenCvSharp;
using Xunit;

namespace FreeCol.Core.Tests.Imaging;

public class FocusMeasureTests
{
    private static Mat SharpEdges()
    {
        // Schachbrett: viele harte Kanten → hohe Laplacian-Varianz.
        var m = new Mat(100, 100, MatType.CV_8UC1, Scalar.Black);
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
                if (((x / 10) + (y / 10)) % 2 == 0)
                    m.Set(y, x, (byte)255);
        return m;
    }

    [Fact]
    public void Sharp_Has_Higher_Variance_Than_Blurred()
    {
        using var sharp = SharpEdges();
        using var blurred = new Mat();
        Cv2.GaussianBlur(sharp, blurred, new Size(9, 9), 0);

        var roi = new Rect(0, 0, 100, 100);
        var sharpScore = FocusMeasure.LaplacianVariance(sharp, roi);
        var blurScore = FocusMeasure.LaplacianVariance(blurred, roi);

        Assert.True(sharpScore > blurScore,
            $"sharp={sharpScore} sollte > blur={blurScore} sein");
    }

    [Fact]
    public void Roi_Is_Clamped_To_Image_Bounds()
    {
        using var img = SharpEdges();
        // ROI ragt über den Rand hinaus — darf nicht werfen, liefert >0.
        var score = FocusMeasure.LaplacianVariance(img, new Rect(-20, -20, 80, 80));
        Assert.True(score > 0);
    }

    [Fact]
    public void Degenerate_Roi_Returns_Zero()
    {
        using var img = SharpEdges();
        Assert.Equal(0, FocusMeasure.LaplacianVariance(img, new Rect(50, 50, 1, 1)));
    }

    [Fact]
    public void Box_Variance_Is_Local_To_The_Focus_Point()
    {
        // Scharfe Kante nur links; ein ROI rechts (glatte Fläche) ist ~0, der
        // ROI auf der Kante deutlich höher — der ROI misst lokal am Fokus-Punkt.
        using var img = new Mat(200, 200, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(img, new Rect(0, 0, 50, 200), Scalar.White, thickness: -1);

        var onEdge = FocusMeasure.Variance(img, new FocusRoi(50, 100, 20));
        var onFlat = FocusMeasure.Variance(img, new FocusRoi(150, 100, 20));

        Assert.True(onEdge > onFlat + 10, $"edge={onEdge} flat={onFlat}");
    }
}

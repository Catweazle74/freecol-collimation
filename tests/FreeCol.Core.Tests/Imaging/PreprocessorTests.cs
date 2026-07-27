using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Imaging;

public class PreprocessorTests
{
    [Fact]
    public void ToGrayscaleBlurred_From3ChannelInput_ReturnsSingleChannelSameSize()
    {
        using var bgr = new Mat(new Size(64, 64), MatType.CV_8UC3, new Scalar(100, 150, 200));

        using var result = Preprocessor.ToGrayscaleBlurred(bgr, blurKernel: 5);

        Assert.Equal(1, result.Channels());
        Assert.Equal(bgr.Size(), result.Size());
    }

    [Fact]
    public void ToGrayscaleBlurred_AcceptsSingleChannelAndPassesThrough()
    {
        using var gray = new Mat(new Size(32, 32), MatType.CV_8UC1, new Scalar(128));

        using var result = Preprocessor.ToGrayscaleBlurred(gray, blurKernel: 0);

        Assert.Equal(1, result.Channels());
        Assert.Equal(gray.Size(), result.Size());
    }
}

using System;
using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Imaging;

public class EllipseClustererTests
{
    private static EllipseFit Make(float x, float y, float w, float h, double area, float angle = 0f)
        => new(new Point2f(x, y), new Size2f(w, h), angle, area);

    [Fact]
    public void Merge_TwoNearlyIdenticalEllipses_BecomeOne()
    {
        var input = new[]
        {
            Make(100, 100, 50, 50, 1000),
            Make(101, 99, 51, 49, 950),
        };

        var result = new EllipseClusterer().Merge(input);

        Assert.Single(result);
        Assert.Equal(1000, result[0].ContourArea);
    }

    [Fact]
    public void Merge_FarApartEllipses_KeepBoth()
    {
        var input = new[]
        {
            Make(100, 100, 50, 50, 1000),
            Make(300, 300, 50, 50, 800),
        };

        var result = new EllipseClusterer().Merge(input);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Merge_SameCenterDifferentSize_KeepBoth()
    {
        var input = new[]
        {
            Make(100, 100, 50, 50, 1000),
            Make(100, 100, 200, 200, 5000),
        };

        var result = new EllipseClusterer().Merge(input);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Merge_ThreeTransitivelyClose_AllInOneCluster()
    {
        // A↔B 4px, B↔C 4px (transitiv via Union-Find), A↔C 8px (direkt nicht ähnlich)
        var input = new[]
        {
            Make(100, 100, 50, 50, 1000),
            Make(104, 100, 50, 50, 900),
            Make(108, 100, 50, 50, 800),
        };

        var result = new EllipseClusterer { CenterTolerancePixels = 5.0 }.Merge(input);

        Assert.Single(result);
        Assert.Equal(1000, result[0].ContourArea);
    }

    [Fact]
    public void Merge_EmptyInput_ReturnsEmpty()
    {
        var result = new EllipseClusterer().Merge(Array.Empty<EllipseFit>());
        Assert.Empty(result);
    }
}

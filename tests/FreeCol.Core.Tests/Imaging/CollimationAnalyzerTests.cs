using System;
using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Imaging;

public class CollimationAnalyzerTests
{
    private static EllipseFit Make(float cx, float cy, float w, float h, double area)
        => new(new Point2f(cx, cy), new Size2f(w, h), 0f, area);

    [Fact]
    public void Analyze_NoEllipses_AllNull()
    {
        var result = new CollimationAnalyzer().Analyze(Array.Empty<EllipseFit>());
        Assert.Null(result.OazRand);
        Assert.Null(result.Marker);
        Assert.Null(result.Offset);
    }

    [Fact]
    public void Analyze_SingleEllipse_OnlyTubus()
    {
        var result = new CollimationAnalyzer().Analyze(new[] { Make(100, 100, 200, 200, 30000) });
        Assert.NotNull(result.OazRand);
        Assert.Null(result.Marker);
        Assert.Null(result.Offset);
    }

    [Fact]
    public void Analyze_LargeWithSmallInside_TubusAndMarker()
    {
        var input = new[]
        {
            Make(100, 100, 200, 200, 30000),  // Tubus
            Make(110, 105, 20, 20, 300),      // kleiner Marker innerhalb
        };

        var result = new CollimationAnalyzer().Analyze(input);

        Assert.NotNull(result.OazRand);
        Assert.NotNull(result.Marker);
        Assert.NotNull(result.Offset);
        Assert.Equal(10, result.Offset!.X);
        Assert.Equal(5, result.Offset.Y);
        Assert.Equal(Math.Sqrt(125), result.Offset.Magnitude, precision: 5);
    }

    [Fact]
    public void Analyze_SmallOutsideTubus_NoMarker()
    {
        var input = new[]
        {
            Make(100, 100, 50, 50, 30000),    // Tubus, radius 25
            Make(200, 200, 10, 10, 300),      // weit außerhalb
        };

        var result = new CollimationAnalyzer().Analyze(input);

        Assert.NotNull(result.OazRand);
        Assert.Null(result.Marker);
    }

    [Fact]
    public void Analyze_TwoLargeSimilar_NoMarkerClassified()
    {
        var input = new[]
        {
            Make(100, 100, 200, 200, 30000),
            Make(105, 105, 180, 180, 25000),  // 83 % der Tubus-Fläche → kein Marker
        };

        var result = new CollimationAnalyzer().Analyze(input);

        Assert.NotNull(result.OazRand);
        Assert.Null(result.Marker);
    }

    [Fact]
    public void Analyze_MultipleSmallInside_PicksClosestToTubusCenter()
    {
        var input = new[]
        {
            Make(100, 100, 200, 200, 30000),  // Tubus
            Make(150, 100, 10, 10, 100),      // weiter weg (50 px)
            Make(110, 100, 10, 10, 100),      // näher (10 px)
        };

        var result = new CollimationAnalyzer().Analyze(input);

        Assert.NotNull(result.Marker);
        Assert.Equal(110, result.Marker!.Center.X);
    }
}

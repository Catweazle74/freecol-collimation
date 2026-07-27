using FreeCol.Core.Screws;

namespace FreeCol.Core.Tests.Screws;

public class ScrewCalibrationMathTests
{
    [Fact]
    public void EffectPerTurn_QuarterTurn_ScalesByFour()
    {
        var (dx, dy) = ScrewCalibrationMath.EffectPerTurn(3.0, -2.0, 0.25, clockwise: true);
        Assert.Equal(12.0, dx, 6);
        Assert.Equal(-8.0, dy, 6);
    }

    [Fact]
    public void EffectPerTurn_FullTurn_KeepsMeasuredDelta()
    {
        var (dx, dy) = ScrewCalibrationMath.EffectPerTurn(5.0, 4.0, 1.0, clockwise: true);
        Assert.Equal(5.0, dx, 6);
        Assert.Equal(4.0, dy, 6);
    }

    [Fact]
    public void EffectPerTurn_CounterClockwise_NegatesVector()
    {
        var (dx, dy) = ScrewCalibrationMath.EffectPerTurn(5.0, -4.0, 0.5, clockwise: false);
        Assert.Equal(-10.0, dx, 6);
        Assert.Equal(8.0, dy, 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.25)]
    public void EffectPerTurn_NonPositiveTurns_Throws(double turns)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScrewCalibrationMath.EffectPerTurn(1.0, 1.0, turns, clockwise: true));
    }

    [Theory]
    [InlineData("0,25", 0.25)]
    [InlineData("0.5", 0.5)]
    [InlineData("1", 1.0)]
    [InlineData(" 1,5 ", 1.5)]
    [InlineData("1/4", 0.25)]
    [InlineData("3/4", 0.75)]
    public void ParseTurns_ValidInput_Parses(string text, double expected)
    {
        Assert.Equal(expected, ScrewCalibrationMath.ParseTurns(text)!.Value, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-0,5")]
    [InlineData("11")]
    [InlineData("1/0")]
    public void ParseTurns_InvalidInput_ReturnsNull(string? text)
    {
        Assert.Null(ScrewCalibrationMath.ParseTurns(text));
    }

    [Theory]
    [InlineData(0.0, 0.0, 2.0, false)] // kein Versatz -> nicht bewegt
    [InlineData(1.0, 1.0, 2.0, false)] // ~1,41 px, knapp unter der Standard-Schwelle
    [InlineData(3.0, 0.0, 2.0, true)] // klar über der Schwelle
    [InlineData(0.0, 2.0, 2.0, true)] // exakt auf der Schwelle: IST-Verhalten ist >= (Grenzwert zählt als "genug bewegt")
    [InlineData(-3.0, 0.0, 2.0, true)] // negativer Versatz zählt genauso, da über Pythagoras nur der Betrag einfließt
    [InlineData(1.0, 0.0, 0.5, true)] // eigener (kleinerer) Schwellenwert: gleicher Versatz reicht jetzt aus
    [InlineData(1.0, 0.0, 2.0, false)] // gleicher Versatz, aber mit Standard-Schwelle nicht erreicht
    public void HasMovedEnough_GivenDeltaAndThreshold_ReturnsExpected(
        double dx, double dy, double thresholdPx, bool expected)
    {
        Assert.Equal(expected, ScrewCalibrationMath.HasMovedEnough(dx, dy, thresholdPx));
    }

    [Theory]
    [InlineData(3.0, 0.0, true)] // Standard-Schwelle (2.0 px) ohne expliziten Parameter überschritten
    [InlineData(1.0, 1.0, false)] // Standard-Schwelle ohne expliziten Parameter unterschritten
    public void HasMovedEnough_WithoutExplicitThreshold_UsesDefaultOfTwoPixels(
        double dx, double dy, bool expected)
    {
        Assert.Equal(expected, ScrewCalibrationMath.HasMovedEnough(dx, dy));
    }
}

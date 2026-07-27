using FreeCol.Core.Imaging;
using FreeCol.Core.Startest;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Startest;

public class CollimationPairTests
{
    // Baut ein DonutResult mit gegebenem Außen-/Innenoffset und -radius;
    // InnerRadius/BrightnessDarkDirection/BrightnessImbalance sind für die
    // Paar-Auswertung irrelevant und bleiben neutral.
    private static DonutResult Donut(Point2f offset, double outerRadius) =>
        new(OuterCenter: new Point2f(0, 0), OuterRadius: outerRadius,
            InnerCenter: offset, InnerRadius: outerRadius * 0.3,
            BrightnessDarkDirection: new Point2f(0, 0), BrightnessImbalance: 0);

    [Fact]
    public void Evaluate_PerfectMirroring_ErrorNearZeroSystematicIsOffset()
    {
        // u_A = (0.05, 0.02), u_B = -u_A bei gleichem Radius (100 px).
        var a = Donut(new Point2f(5, 2), 100);
        var b = Donut(new Point2f(-5, -2), 100);

        var result = CollimationPair.Evaluate(a, firstFocusOffsetSteps: -160, b, secondFocusOffsetSteps: 160);

        Assert.True(result.IsEvaluable);
        Assert.Null(result.Reason);
        Assert.Equal(0.0, result.ErrorPercent, precision: 3);
        Assert.Equal(5.3852, result.SystematicPercent, precision: 3);
    }

    [Fact]
    public void Evaluate_PureCollimationError_SystematicNearZero()
    {
        // u_A = u_B = (0.03, -0.01) bei gleichem Radius (100 px).
        var a = Donut(new Point2f(3, -1), 100);
        var b = Donut(new Point2f(3, -1), 100);

        var result = CollimationPair.Evaluate(a, firstFocusOffsetSteps: -160, b, secondFocusOffsetSteps: 160);

        Assert.True(result.IsEvaluable);
        Assert.Equal(3.1623, result.ErrorPercent, precision: 3);
        Assert.Equal(0.0, result.SystematicPercent, precision: 3);
    }

    [Fact]
    public void Evaluate_MixedCase_MatchesHandComputedExpectation()
    {
        // u_A = (0.04, 0.03), u_B = (0.02, -0.01), gleicher Radius (100 px).
        // error = (0.03, 0.01), systematic = (0.01, 0.02) — von Hand gerechnet.
        var a = Donut(new Point2f(4, 3), 100);
        var b = Donut(new Point2f(2, -1), 100);

        var result = CollimationPair.Evaluate(a, firstFocusOffsetSteps: -160, b, secondFocusOffsetSteps: 200);

        Assert.True(result.IsEvaluable);
        Assert.Equal(0.03, result.ErrorVector.X, precision: 4);
        Assert.Equal(0.01, result.ErrorVector.Y, precision: 4);
        Assert.Equal(0.01, result.SystematicVector.X, precision: 4);
        Assert.Equal(0.02, result.SystematicVector.Y, precision: 4);
        Assert.Equal(3.1623, result.ErrorPercent, precision: 3);
        Assert.Equal(2.2361, result.SystematicPercent, precision: 3);
        // ErrorPixels = ErrorVector * Referenzradius (Mittel = 100 px).
        Assert.Equal(3.0, result.ErrorPixels.X, precision: 3);
        Assert.Equal(1.0, result.ErrorPixels.Y, precision: 3);
    }

    [Theory]
    [InlineData(-160, -200)] // beide intra (gleiches Vorzeichen)
    [InlineData(160, 200)]   // beide extra (gleiches Vorzeichen)
    [InlineData(0, 160)]     // eine Aufnahme im Fokus
    [InlineData(-160, 0)]    // eine Aufnahme im Fokus
    public void Evaluate_SameFocusSide_ReturnsNotEvaluable(int firstSteps, int secondSteps)
    {
        var a = Donut(new Point2f(5, 2), 100);
        var b = Donut(new Point2f(-5, -2), 100);

        var result = CollimationPair.Evaluate(a, firstSteps, b, secondSteps);

        Assert.False(result.IsEvaluable);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public void Evaluate_UnequalRadii_SetsWarningFlag()
    {
        // Realer Datensatz: 92 px vs. 68 px Scheibchenradius (35 % Abweichung).
        var a = Donut(new Point2f(5, 2), 92);
        var b = Donut(new Point2f(-5, -2), 68);

        var result = CollimationPair.Evaluate(a, firstFocusOffsetSteps: -160, b, secondFocusOffsetSteps: 160);

        Assert.True(result.IsEvaluable);
        Assert.True(result.UnequalDefocusWarning);
        Assert.True(result.RadiusRatio > 1.25);
    }

    [Fact]
    public void Evaluate_SimilarRadii_NoWarningFlag()
    {
        // 92 px vs. 83 px (~11 % Abweichung) — unterhalb der 25 %-Schwelle.
        var a = Donut(new Point2f(5, 2), 92);
        var b = Donut(new Point2f(-5, -2), 83);

        var result = CollimationPair.Evaluate(a, firstFocusOffsetSteps: -160, b, secondFocusOffsetSteps: 160);

        Assert.True(result.IsEvaluable);
        Assert.False(result.UnequalDefocusWarning);
    }
}

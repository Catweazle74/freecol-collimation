using FreeCol.Core.Focuser;

namespace FreeCol.Core.Tests.Focuser;

public class FocusPairModelTests
{
    [Fact]
    public void IntraFocusPosition_SubtractsDefocusFromCenter()
        => Assert.Equal(12340, FocusPairModel.IntraFocusPosition(12500, 160));

    [Fact]
    public void ExtraFocusPosition_AddsDefocusToCenter()
        => Assert.Equal(12660, FocusPairModel.ExtraFocusPosition(12500, 160));

    [Theory]
    [InlineData(0, 10000, true)]     // untere Grenze
    [InlineData(10000, 10000, true)] // obere Grenze
    [InlineData(-1, 10000, false)]   // unterhalb 0
    [InlineData(10001, 10000, false)] // oberhalb MaxStep
    public void IsWithinRange_ChecksAgainstMaxStep(
        int position, int maxStep, bool expected)
        => Assert.Equal(expected, FocusPairModel.IsWithinRange(position, maxStep));

    [Fact]
    public void IsWithinRange_UnknownMaxStep_OnlyRejectsNegative()
    {
        Assert.True(FocusPairModel.IsWithinRange(999_999, maxStep: 0));
        Assert.False(FocusPairModel.IsWithinRange(-5, maxStep: 0));
    }
}

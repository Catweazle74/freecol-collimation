using FreeCol.Core.Screws;

namespace FreeCol.Core.Tests.Screws;

public class ScrewTravelTrackerTests
{
    private static KeyValuePair<string, double>[] Turns(params (string Name, double Turns)[] items)
        => items.Select(i => new KeyValuePair<string, double>(i.Name, i.Turns)).ToArray();

    [Fact]
    public void CumulativeFor_UnknownScrew_IsZero()
    {
        var t = new ScrewTravelTracker();
        Assert.Equal(0, t.CumulativeFor("A"));
    }

    [Fact]
    public void Accumulate_SumsAcrossIterations()
    {
        var t = new ScrewTravelTracker();
        t.Accumulate(Turns(("A", -1.0), ("B", 0.5)));
        t.Accumulate(Turns(("A", -1.5)));

        Assert.Equal(-2.5, t.CumulativeFor("A"), precision: 6);
        Assert.Equal(0.5, t.CumulativeFor("B"), precision: 6);
    }

    [Fact]
    public void Clear_ResetsAccumulatedTurns()
    {
        var t = new ScrewTravelTracker();
        t.Accumulate(Turns(("A", -5.0)));
        t.Clear();
        Assert.Equal(0, t.CumulativeFor("A"));
        Assert.Null(t.FindRunout(new[] { "A" }, warnTurns: 3.0));
    }

    [Fact]
    public void FindRunout_BelowThreshold_ReturnsNull()
    {
        var t = new ScrewTravelTracker();
        t.Accumulate(Turns(("A", -2.9)));
        Assert.Null(t.FindRunout(new[] { "A" }, warnTurns: 3.0));
    }

    [Fact]
    public void FindRunout_AtThreshold_IsReported()
    {
        var t = new ScrewTravelTracker();
        t.Accumulate(Turns(("A", -3.0)));

        var runout = t.FindRunout(new[] { "A" }, warnTurns: 3.0);

        Assert.NotNull(runout);
        Assert.Equal("A", runout!.Value.Name);
        Assert.Equal(-3.0, runout.Value.Cumulative, precision: 6);
    }

    [Fact]
    public void FindRunout_PositiveTurns_NeverTrigger()
    {
        // Hineindrehen (CW, positiv) kann keinen Runout auslösen.
        var t = new ScrewTravelTracker();
        t.Accumulate(Turns(("A", 5.0)));
        Assert.Null(t.FindRunout(new[] { "A" }, warnTurns: 3.0));
    }

    [Fact]
    public void FindRunout_ReturnsFirstInGivenOrder()
    {
        // Beide über der Schranke → die in der übergebenen Reihenfolge zuerst
        // genannte Schraube wird gemeldet (entspricht der Phasen-Schraubenfolge).
        var t = new ScrewTravelTracker();
        t.Accumulate(Turns(("A", -4.0), ("B", -5.0)));

        var first = t.FindRunout(new[] { "B", "A" }, warnTurns: 3.0);
        Assert.Equal("B", first!.Value.Name);

        var second = t.FindRunout(new[] { "A", "B" }, warnTurns: 3.0);
        Assert.Equal("A", second!.Value.Name);
    }
}

using FreeCol.Core.Screws;

namespace FreeCol.Core.Tests.Screws;

public class ScrewSolverTests
{
    [Fact]
    public void ComputeTurns_EmptySet_ReturnsEmpty()
    {
        var turns = ScrewSolver.ComputeTurns(new Screw[0], 10, 0);
        Assert.Empty(turns);
    }

    [Fact]
    public void ComputeTurns_TwoOrthogonalScrews_SolvesExactly()
    {
        var screws = new[]
        {
            new Screw("X-only", 1, EffectDx: 10, EffectDy: 0, IsCalibrated: true),
            new Screw("Y-only", 1, EffectDx: 0, EffectDy: 10, IsCalibrated: true),
        };

        var turns = ScrewSolver.ComputeTurns(screws, 30, 20);

        Assert.Equal(2, turns.Length);
        Assert.Equal(3.0, turns[0], precision: 6);
        Assert.Equal(2.0, turns[1], precision: 6);
    }

    [Fact]
    public void ComputeTurns_ThreeScrews120Degrees_MinimumNorm()
    {
        // Drei Schrauben, deren Wirkungs-Vektoren 120° auseinander liegen.
        // Erwartung: Lösung verteilt sich gleichmäßig auf alle drei.
        var s120 = System.Math.Sin(2 * System.Math.PI / 3);
        var c120 = System.Math.Cos(2 * System.Math.PI / 3);
        var screws = new[]
        {
            new Screw("A", 2, EffectDx: 10, EffectDy: 0, IsCalibrated: true),
            new Screw("B", 2, EffectDx: 10 * c120, EffectDy: 10 * s120, IsCalibrated: true),
            new Screw("C", 2, EffectDx: 10 * c120, EffectDy: -10 * s120, IsCalibrated: true),
        };

        var turns = ScrewSolver.ComputeTurns(screws, 5, 0);

        // Σ t·E = (5, 0) muss gelten.
        double sumDx = 0, sumDy = 0;
        for (var i = 0; i < screws.Length; i++)
        {
            sumDx += turns[i] * screws[i].EffectDx;
            sumDy += turns[i] * screws[i].EffectDy;
        }
        Assert.Equal(5.0, sumDx, precision: 6);
        Assert.Equal(0.0, sumDy, precision: 6);
    }

    [Fact]
    public void ComputeTurns_CollinearScrews_ReturnsZero()
    {
        // Alle Schrauben wirken in dieselbe Richtung → System degeneriert.
        var screws = new[]
        {
            new Screw("A", 1, 10, 0, true),
            new Screw("B", 1, 5, 0, true),
        };

        var turns = ScrewSolver.ComputeTurns(screws, 0, 7);

        Assert.All(turns, t => Assert.Equal(0.0, t));
    }
}

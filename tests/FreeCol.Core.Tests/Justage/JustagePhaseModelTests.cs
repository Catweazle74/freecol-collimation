using FreeCol.Core.Justage;
using FreeCol.Core.Markings;

namespace FreeCol.Core.Tests.Justage;

public class JustagePhaseModelTests
{
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public void FirstMirrorPhase_SkipsSpiderPhase_WhenSpiderFixed(bool phase1Enabled, int expected)
        => Assert.Equal(expected, JustagePhaseModel.FirstMirrorPhase(phase1Enabled));

    [Theory]
    // Spinnen-Phase aktiv: 0→1, 1→2, 2→3, 3→4
    [InlineData(0, true, 1)]
    [InlineData(1, true, 2)]
    [InlineData(2, true, 3)]
    [InlineData(3, true, 4)]
    // Spinnen-Phase aus: Kipp-Phasen rücken nach vorn (2→2, 3→3)
    [InlineData(0, false, 1)]
    [InlineData(2, false, 2)]
    [InlineData(3, false, 3)]
    // Unbekannte Phase → 0
    [InlineData(7, true, 0)]
    public void DisplayNumber_RenumbersWhenSpiderPhaseHidden(int phase, bool phase1Enabled, int expected)
        => Assert.Equal(expected, JustagePhaseModel.DisplayNumber(phase, phase1Enabled));

    [Theory]
    [InlineData(1, MarkingKind.Sekundaer)]
    [InlineData(2, MarkingKind.HauptspiegelReflex)]
    [InlineData(3, MarkingKind.Linse)]
    [InlineData(0, MarkingKind.Sekundaer)] // Default/Orientierung
    [InlineData(9, MarkingKind.Sekundaer)] // Unbekannt → Default
    public void MovingKind_MapsPhaseToMovingMarking(int phase, MarkingKind expected)
        => Assert.Equal(expected, JustagePhaseModel.MovingKind(phase));

    [Theory]
    [InlineData(MarkingKind.OazRand, 0)]
    [InlineData(MarkingKind.Sekundaer, 1)]
    [InlineData(MarkingKind.HauptspiegelReflex, 2)]
    [InlineData(MarkingKind.Marker, 2)]
    [InlineData(MarkingKind.Linse, 2)]
    public void FocusDepthRank_OrdersFeaturesByDepth(MarkingKind kind, int expected)
        => Assert.Equal(expected, JustagePhaseModel.FocusDepthRank(kind));

    [Fact]
    public void FocusDepthRank_FarPlaneFeatures_ShareSameRank()
    {
        // HSR, Marker und Linse liegen auf der Hauptspiegel-Ebene → kein Rang-Unterschied.
        var hsr = JustagePhaseModel.FocusDepthRank(MarkingKind.HauptspiegelReflex);
        var marker = JustagePhaseModel.FocusDepthRank(MarkingKind.Marker);
        var linse = JustagePhaseModel.FocusDepthRank(MarkingKind.Linse);
        Assert.Equal(hsr, marker);
        Assert.Equal(hsr, linse);
    }

    [Theory]
    [InlineData(MarkingKind.OazRand, new[] { 1 })]
    [InlineData(MarkingKind.Sekundaer, new[] { 1, 2 })]
    [InlineData(MarkingKind.HauptspiegelReflex, new[] { 2 })]
    [InlineData(MarkingKind.Marker, new[] { 3 })]
    [InlineData(MarkingKind.Linse, new[] { 3 })]
    public void PhasesUsing_MapsMarkingToItsMeasurementPhases(MarkingKind kind, int[] expected)
        => Assert.Equal(expected, JustagePhaseModel.PhasesUsing(kind));
}

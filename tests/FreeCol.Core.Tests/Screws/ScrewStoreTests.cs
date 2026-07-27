using System;
using System.IO;
using System.Linq;
using FreeCol.Core.Screws;

namespace FreeCol.Core.Tests.Screws;

public class ScrewStoreTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(),
        $"freecol-screws-{Guid.NewGuid():N}");

    [Fact]
    public void Load_MissingFile_ReturnsDefaultSet()
    {
        var dir = TempDir();
        var store = new ScrewStore(dir);
        try
        {
            var set = store.Load("any");
            Assert.NotEmpty(set.Screws);
            Assert.All(set.Screws, s => Assert.False(s.IsCalibrated));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Default_HasScrewsForAllThreePhases()
    {
        var set = ScrewSet.Default;
        Assert.NotEmpty(set.ForPhase(1));
        Assert.NotEmpty(set.ForPhase(2));
        Assert.NotEmpty(set.ForPhase(3));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var dir = TempDir();
        try
        {
            var store = new ScrewStore(dir);
            var input = ScrewSet.Default
                .Replace(new Screw("Fangspiegel 1", 2, EffectDx: 12.5, EffectDy: -3.2, IsCalibrated: true));

            store.Save("OCAL", input);
            var loaded = store.Load("OCAL");

            var calibrated = loaded.Screws.Single(s => s.Name == "Fangspiegel 1" && s.Phase == 2);
            Assert.True(calibrated.IsCalibrated);
            Assert.Equal(12.5, calibrated.EffectDx, precision: 6);
            Assert.Equal(-3.2, calibrated.EffectDy, precision: 6);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SpiderAdjustable_RoundTrips_AndPreservedByReplace()
    {
        var dir = TempDir();
        try
        {
            var store = new ScrewStore(dir);
            var input = ScrewSet.Default with { SpiderAdjustable = false };
            // Replace darf das Flag nicht verlieren.
            input = input.Replace(new Screw("Hauptspiegel 1", 3, 1, 1, true));
            Assert.False(input.SpiderAdjustable);

            store.Save("OCAL", input);
            Assert.False(store.Load("OCAL").SpiderAdjustable);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void OazAngleDeg_RoundTrips_AndPreservedByReplace()
    {
        var dir = TempDir();
        try
        {
            var store = new ScrewStore(dir);
            var input = (ScrewSet.Default with { OazAngleDeg = 90 })
                .Replace(new Screw("Hauptspiegel 1", 3, 1, 1, true));
            Assert.Equal(90, input.OazAngleDeg);

            store.Save("OCAL", input);
            Assert.Equal(90, store.Load("OCAL").OazAngleDeg);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SpiderAngleDeg_RoundTrips()
    {
        var dir = TempDir();
        try
        {
            var store = new ScrewStore(dir);
            store.Save("OCAL", ScrewSet.Default with { SpiderAngleDeg = 45 });
            Assert.Equal(45, store.Load("OCAL").SpiderAngleDeg);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }



    [Fact]
    public void Replace_KeepsOtherScrewsIntact()
    {
        var set = ScrewSet.Default;
        var updated = set.Replace(new Screw("Hauptspiegel 1", 3, 5, 5, true));
        Assert.Equal(set.Screws.Count, updated.Screws.Count);
        Assert.True(updated.Screws.Single(s => s.Name == "Hauptspiegel 1").IsCalibrated);
        Assert.False(updated.Screws.Single(s => s.Name == "Hauptspiegel 2").IsCalibrated);
    }

    [Fact]
    public void CalibratedAt_RoundTrips()
    {
        var dir = TempDir();
        try
        {
            var store = new ScrewStore(dir);
            var timestamp = new DateTimeOffset(2026, 7, 12, 10, 30, 0, TimeSpan.Zero);
            var input = ScrewSet.Default.Replace(new Screw(
                "Fangspiegel 1", 2, EffectDx: 12.5, EffectDy: -3.2,
                IsCalibrated: true, CalibratedAt: timestamp));

            store.Save("OCAL", input);
            var loaded = store.Load("OCAL");

            var calibrated = loaded.Screws.Single(s => s.Name == "Fangspiegel 1" && s.Phase == 2);
            Assert.Equal(timestamp, calibrated.CalibratedAt);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

}

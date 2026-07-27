using System;
using System.IO;
using FreeCol.Core.Markings;

namespace FreeCol.Core.Tests.Markings;

public class MarkingStoreTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(),
        $"freecol-markings-{Guid.NewGuid():N}");

    [Fact]
    public void Load_MissingFile_ReturnsDefaultSet()
    {
        var dir = TempDir();
        var store = new MarkingStore(dir);
        try
        {
            var set = store.Load("any");
            Assert.False(set.OazRand.IsPlaced);
            Assert.False(set.HauptspiegelReflex.IsPlaced);
            Assert.False(set.Sekundaer.IsPlaced);
            Assert.False(set.Marker.IsPlaced);
            Assert.True(set.OazRand.IsAutoEnabled);
            Assert.True(set.OazRand.IsVisible);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllSlots()
    {
        var dir = TempDir();
        try
        {
            var store = new MarkingStore(dir);
            var input = MarkingSet.Default
                .With(new Marking(MarkingKind.OazRand, true, 320, 240, 200, 200, 0, true, true))
                .With(new Marking(MarkingKind.HauptspiegelReflex, true, 322, 241, 150, 150, 0, false, true))
                .With(new Marking(MarkingKind.Sekundaer, true, 321, 240, 60, 55, 12.5, true, false))
                .With(new Marking(MarkingKind.Marker, true, 320, 239, 4, 4, 0, true, true));

            store.Save("OCAL", input);
            var loaded = store.Load("OCAL");

            Assert.Equal(input, loaded);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_KeepsMarkingsSeparatePerCamera()
    {
        var dir = TempDir();
        try
        {
            var store = new MarkingStore(dir);
            var a = MarkingSet.Default
                .With(new Marking(MarkingKind.OazRand, true, 100, 100, 50, 50, 0, true, true));
            var b = MarkingSet.Default
                .With(new Marking(MarkingKind.OazRand, true, 500, 500, 200, 200, 0, true, true));

            store.Save("cam-A", a);
            store.Save("cam-B", b);

            Assert.Equal(100, store.Load("cam-A").OazRand.CenterX);
            Assert.Equal(500, store.Load("cam-B").OazRand.CenterX);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Eccentricity_OfCircle_IsZero()
    {
        var circle = new Marking(MarkingKind.OazRand, true, 0, 0, 100, 100, 0, true, true);
        Assert.Equal(0.0, circle.Eccentricity, precision: 6);
    }

    [Fact]
    public void Eccentricity_OfEllipse_MatchesFormula()
    {
        var ellipse = new Marking(MarkingKind.Sekundaer, true, 0, 0, 100, 60, 0, true, true);
        // e = √(1 − (60/100)²) = √(1 − 0.36) = √0.64 = 0.8
        Assert.Equal(0.8, ellipse.Eccentricity, precision: 6);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsFrameSize()
    {
        var dir = TempDir();
        try
        {
            var store = new MarkingStore(dir);
            var input = MarkingSet.Default with { FrameWidth = 1920, FrameHeight = 1080 };

            store.Save("OCAL", input);
            var loaded = store.Load("OCAL");

            Assert.Equal(1920, loaded.FrameWidth);
            Assert.Equal(1080, loaded.FrameHeight);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

}

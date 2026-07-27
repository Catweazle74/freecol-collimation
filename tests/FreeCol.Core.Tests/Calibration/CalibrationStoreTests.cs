using System;
using System.IO;
using FreeCol.Core.Calibration;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Calibration;

public class CalibrationStoreTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(),
        $"freecol-calib-{Guid.NewGuid():N}");

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var dir = TempDir();
        var store = new CalibrationStore(dir);
        try
        {
            Assert.Null(store.Load("any-camera"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var dir = TempDir();
        try
        {
            var store = new CalibrationStore(dir);
            var input = new CalibrationResult(
                OpticalCenter: new Point2f(318.5f, 239.7f),
                FitRadius: 12.3,
                RmsResidual: 0.42,
                SampleCount: 4,
                OrientationConfirmed: true,
                Timestamp: DateTimeOffset.UtcNow);

            store.Save("HD Pro Webcam C920", input);
            var loaded = store.Load("HD Pro Webcam C920");

            Assert.NotNull(loaded);
            Assert.Equal(input.OpticalCenter.X, loaded!.OpticalCenter.X, precision: 3);
            Assert.Equal(input.OpticalCenter.Y, loaded.OpticalCenter.Y, precision: 3);
            Assert.Equal(input.FitRadius, loaded.FitRadius, precision: 6);
            Assert.Equal(input.RmsResidual, loaded.RmsResidual, precision: 6);
            Assert.Equal(input.SampleCount, loaded.SampleCount);
            Assert.Equal(input.OrientationConfirmed, loaded.OrientationConfirmed);
            Assert.Equal(input.Timestamp, loaded.Timestamp);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_IsolatesPerCameraKey()
    {
        var dir = TempDir();
        try
        {
            var store = new CalibrationStore(dir);
            var a = new CalibrationResult(new Point2f(100, 200), 10, 0.1, 5, true, DateTimeOffset.UtcNow);
            var b = new CalibrationResult(new Point2f(300, 400), 20, 0.2, 6, true, DateTimeOffset.UtcNow);

            store.Save("camera-A", a);
            store.Save("camera-B", b);

            var loadedA = store.Load("camera-A");
            var loadedB = store.Load("camera-B");

            Assert.NotNull(loadedA);
            Assert.NotNull(loadedB);
            Assert.Equal(100f, loadedA!.OpticalCenter.X);
            Assert.Equal(300f, loadedB!.OpticalCenter.X);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("HD Pro Webcam C920", "hd_pro_webcam_c920")]
    [InlineData("oca calibration: oca calibratio", "oca_calibration__oca_calibratio")]
    [InlineData("", "default")]
    public void Sanitize_ReplacesProblematicChars(string input, string expected)
    {
        Assert.Equal(expected, CalibrationStore.Sanitize(input));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsFrameSize()
    {
        var dir = TempDir();
        try
        {
            var store = new CalibrationStore(dir);
            var input = new CalibrationResult(
                OpticalCenter: new Point2f(318.5f, 239.7f),
                FitRadius: 12.3,
                RmsResidual: 0.42,
                SampleCount: 4,
                OrientationConfirmed: true,
                Timestamp: DateTimeOffset.UtcNow,
                FrameWidth: 1920,
                FrameHeight: 1080);

            store.Save("HD Pro Webcam C920", input);
            var loaded = store.Load("HD Pro Webcam C920");

            Assert.NotNull(loaded);
            Assert.Equal(1920, loaded!.FrameWidth);
            Assert.Equal(1080, loaded.FrameHeight);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

}

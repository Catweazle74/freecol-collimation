using System;
using System.IO;
using FreeCol.Core.Settings;

namespace FreeCol.Core.Tests.Settings;

public class CameraSettingsStoreTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(),
        $"freecol-settings-{Guid.NewGuid():N}");

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var dir = TempDir();
        var store = new CameraSettingsStore(dir);
        try
        {
            Assert.Null(store.Load("any"));
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
            var store = new CameraSettingsStore(dir);
            var input = new CameraSettings(
                IsAutoExposure: false,
                Exposure: 312.5,
                IsAutoFocus: true,
                Focus: 47.0);

            store.Save("OCAL", input);
            var loaded = store.Load("OCAL");

            Assert.NotNull(loaded);
            Assert.Equal(input.IsAutoExposure, loaded!.IsAutoExposure);
            Assert.Equal(input.Exposure, loaded.Exposure, precision: 6);
            Assert.Equal(input.IsAutoFocus, loaded.IsAutoFocus);
            Assert.Equal(input.Focus, loaded.Focus, precision: 6);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_KeepsSettingsSeparatePerCamera()
    {
        var dir = TempDir();
        try
        {
            var store = new CameraSettingsStore(dir);
            var a = new CameraSettings(true, 100, true, 10);
            var b = new CameraSettings(false, 800, false, 200);

            store.Save("cam-A", a);
            store.Save("cam-B", b);

            var la = store.Load("cam-A");
            var lb = store.Load("cam-B");

            Assert.NotNull(la);
            Assert.NotNull(lb);
            Assert.Equal(100, la!.Exposure);
            Assert.Equal(800, lb!.Exposure);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}

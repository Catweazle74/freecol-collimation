using FreeCol.Camera;

namespace FreeCol.Core.Tests.Camera;

public class CameraSourceFactoryTests
{
    // Die Factory zentralisiert nur die Konstruktion; die Konstruktoren öffnen keine
    // Hardware (das passiert erst in Start()), daher sind diese Tests hardwarefrei.
    private readonly CameraSourceFactory _factory = new();

    [Fact]
    public void CreateUvc_ReturnsIdleUvcSource()
    {
        using var source = _factory.CreateUvc(deviceIndex: 0, width: 640, height: 480);

        Assert.IsType<OpenCvVideoCaptureSource>(source);
        Assert.False(source.IsRunning); // ohne Start() nicht laufend
    }

    [Fact]
    public void CreateAlpaca_ReturnsIdleAlpacaSource()
    {
        using var source = _factory.CreateAlpaca("localhost", 11111, deviceNumber: 0);

        Assert.IsType<AlpacaCameraSource>(source);
        Assert.False(source.IsRunning);
    }

    [Fact]
    public void CreateAsi_ReturnsIdleAsiSource()
    {
        using var source = _factory.CreateAsi(cameraIndex: 0);

        Assert.IsType<AsiCameraSource>(source);
        Assert.False(source.IsRunning);
    }
}

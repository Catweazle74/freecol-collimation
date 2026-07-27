using FreeCol.Camera;

namespace FreeCol.Core.Tests.Camera;

/// <summary>
/// Prüft die Übersetzung eines Helligkeitsfaktors in einen Belichtungswert. Die
/// lineare Standard-Rechnung (Sekunden bzw. 100-µs-Einheiten) gilt für ASI/Alpaca/V4L2,
/// die logarithmische für DirectShow — dort ist der Wert log2(Belichtungszeit) und
/// der Wertebereich negativ, sodass Multiplizieren die Regelrichtung umkehren würde.
/// </summary>
public class ExposureScalingTests
{
    /// <summary>Minimale Attrappe, die nur die Standard-Implementierung des Interface nutzt.</summary>
    private sealed class LinearControl : IExposureControl
    {
        public bool AutoExposure { get; set; }
        public double Exposure { get; set; }
        public double MinExposure => 1.0;
        public double MaxExposure => 10000.0;
    }

    [Theory]
    [InlineData(100.0, 2.0, 200.0)]
    [InlineData(100.0, 0.5, 50.0)]
    [InlineData(1.0, 1.0, 1.0)]
    public void ScaleExposure_LineareSkala_RechnetMultiplikativ(
        double current, double ratio, double expected)
    {
        IExposureControl control = new LinearControl();

        Assert.Equal(expected, control.ScaleExposure(current, ratio), 6);
    }

    /// <summary>
    /// Kernpunkt für DirectShow: doppelte Helligkeit = EINE Stufe mehr. Der Test läuft
    /// plattformunabhängig nur dann gegen die log2-Variante, wenn er auf Windows
    /// ausgeführt wird — unter Linux prüft er, dass dort weiterhin linear gerechnet wird.
    /// </summary>
    [Theory]
    [InlineData(-4.0, 2.0)]
    [InlineData(-4.0, 0.5)]
    [InlineData(0.0, 2.0)]
    public void ScaleExposure_OpenCvQuelle_FolgtDerSkalaDerPlattform(double current, double ratio)
    {
        IExposureControl control = new OpenCvVideoCaptureSource(deviceIndex: 0);

        var next = control.ScaleExposure(current, ratio);

        if (OperatingSystem.IsWindows())
        {
            // log2: heller ⇒ Wert steigt, dunkler ⇒ Wert fällt — auch im negativen Bereich.
            Assert.Equal(current + Math.Log2(ratio), next, 6);
            Assert.Equal(ratio > 1.0, next > current);
        }
        else
        {
            Assert.Equal(current * ratio, next, 6);
        }
    }

    /// <summary>Ein unbrauchbarer Faktor darf den Wert nicht ins Bodenlose ziehen.</summary>
    [Fact]
    public void ScaleExposure_OpenCvQuelle_LaesstWertBeiFaktorNullUnveraendert()
    {
        IExposureControl control = new OpenCvVideoCaptureSource(deviceIndex: 0);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(-4.0, control.ScaleExposure(-4.0, 0.0), 6);
        }
    }

    /// <summary>
    /// Ohne geöffnetes Gerät gelten die V4L2-Grenzen als Vorbelegung; die
    /// DirectShow-Grenzen ermittelt erst <c>Start()</c> am Gerät.
    /// </summary>
    [Fact]
    public void ExposureGrenzen_VorDemOeffnen_SindAufsteigend()
    {
        var source = new OpenCvVideoCaptureSource(deviceIndex: 0);

        Assert.True(source.MinExposure < source.MaxExposure);
    }
}

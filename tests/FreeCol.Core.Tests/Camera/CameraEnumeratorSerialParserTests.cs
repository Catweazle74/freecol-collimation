using FreeCol.Camera;

namespace FreeCol.Core.Tests.Camera;

/// <summary>
/// Prüft den plattformneutralen Parser <see cref="CameraEnumerator.SerialFromDevicePath"/>,
/// der aus einem Windows-DevicePath die USB-Seriennummer extrahiert. Bewusst ohne
/// COM/DirectShow-Abhängigkeit testbar, da reine String-Logik.
/// </summary>
public class CameraEnumeratorSerialParserTests
{
    [Theory]
    [InlineData(
        @"\\?\usb#vid_a000&pid_b111#20211029#{65e8773d-8f56-11d0-a3b9-00a0c9223196}",
        "20211029")]
    [InlineData(
        @"\\?\USB#VID_A000&PID_B111#20211029#{65E8773D-8F56-11D0-A3B9-00A0C9223196}",
        "20211029")] // Groß-/Kleinschreibung im restlichen Pfad ist ohne Belang.
    [InlineData(
        @"\\?\usb#vid_0bda&pid_5829&mi_00#7&2d3d1f4b&0&0000#{65e8773d-8f56-11d0-a3b9-00a0c9223196}",
        null)] // '&' im dritten Segment: Windows-Instanz-ID statt Seriennummer.
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(@"\\?\usb#vid_a000&pid_b111", null)] // zu wenige '#'-Segmente
    public void SerialFromDevicePath_ExtractsThirdSegmentUnlessInstanceId(
        string? devicePath, string? expectedSerial)
    {
        var serial = CameraEnumerator.SerialFromDevicePath(devicePath);

        Assert.Equal(expectedSerial, serial);
    }

    [Theory]
    [InlineData(
        @"\\?\usb#vid_a000&pid_b111&mi_00#8&2f1c4d36&0&0000#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global",
        @"USB\VID_A000&PID_B111&MI_00\8&2F1C4D36&0&0000")] // OCAL: zusammengesetztes Gerät.
    [InlineData(
        @"\\?\usb#vid_a000&pid_b111#20211029#{65e8773d-8f56-11d0-a3b9-00a0c9223196}",
        @"USB\VID_A000&PID_B111\20211029")]
    [InlineData(
        @"usb#vid_a000&pid_b111#20211029#{65e8773d-8f56-11d0-a3b9-00a0c9223196}",
        @"USB\VID_A000&PID_B111\20211029")] // Präfix \\?\ ist optional.
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(@"\\?\usb#vid_a000&pid_b111", null)] // zu wenige '#'-Segmente
    [InlineData(@"\\?\usb##20211029#{guid}", null)]  // leeres Segment
    public void InstanceIdFromDevicePath_BuildsUppercaseInstanceId(
        string? devicePath, string? expectedInstanceId)
    {
        var instanceId = CameraEnumerator.InstanceIdFromDevicePath(devicePath);

        Assert.Equal(expectedInstanceId, instanceId);
    }

    [Theory]
    [InlineData(@"USB\VID_A000&PID_B111\20211029", "20211029")] // USB-Parent der OCAL.
    [InlineData(@"USB\VID_A000&PID_B111&MI_00\8&2F1C4D36&0&0000", null)] // Instanz-ID, keine Seriennummer.
    [InlineData(@"20211029", "20211029")] // ohne Pfad-Trenner: alles ist das letzte Segment.
    [InlineData(@"USB\VID_A000&PID_B111\", null)] // leeres letztes Segment
    [InlineData(null, null)]
    [InlineData("", null)]
    public void SerialFromDeviceId_TakesLastSegmentUnlessInstanceId(
        string? deviceId, string? expectedSerial)
    {
        var serial = CameraEnumerator.SerialFromDeviceId(deviceId);

        Assert.Equal(expectedSerial, serial);
    }
}

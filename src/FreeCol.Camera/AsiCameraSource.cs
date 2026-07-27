using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using OpenCvSharp;

namespace FreeCol.Camera;

/// <summary>Am USB gefundene ASI-Kamera (für die Auswahl in der UI).</summary>
public sealed record AsiFoundCamera(int Index, int Id, string Name, int Width, int Height)
{
    public override string ToString() => $"{Name} ({Width}×{Height})";
}

/// <summary>
/// Bildquelle über das native ZWO-ASI-SDK (USB direkt, kein Server). On-Demand-
/// Einzelbelichtung (Sterntest), liefert RAW16-Frame als 16-bit-Graustufen-Mat.
/// Siehe <see cref="AsiNative"/> zum plattformabhängigen C-<c>long</c> (CLong).
/// </summary>
public sealed class AsiCameraSource : ICameraSource, IExposureControl
{
    private readonly int _cameraIndex;
    private int _cameraId = -1;
    private int _width, _height;
    private bool _running;
    private byte[]? _buffer;
    private double _exposureSeconds = 1.0;

    public AsiCameraSource(int cameraIndex = 0)
    {
        _cameraIndex = cameraIndex;
        AsiNative.EnsureLoaded();
    }

    public static List<AsiFoundCamera> DiscoverCameras()
    {
        AsiNative.EnsureLoaded();
        var list = new List<AsiFoundCamera>();
        int n = AsiNative.ASIGetNumOfConnectedCameras();
        for (int i = 0; i < n; i++)
        {
            if (AsiNative.ASIGetCameraProperty(out var info, i) == AsiNative.AsiError.Success)
                list.Add(new AsiFoundCamera(i, info.CameraID, info.GetName(), (int)info.MaxWidth.Value, (int)info.MaxHeight.Value));
        }
        return list;
    }

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;
        AsiNative.EnsureLoaded();
        int n = AsiNative.ASIGetNumOfConnectedCameras();
        if (_cameraIndex >= n)
            throw new InvalidOperationException($"Keine ASI-Kamera an Index {_cameraIndex} (gefunden: {n}).");

        Check(AsiNative.ASIGetCameraProperty(out var info, _cameraIndex), "GetCameraProperty");
        _cameraId = info.CameraID;
        _width = (int)info.MaxWidth.Value;
        _height = (int)info.MaxHeight.Value;
        Check(AsiNative.ASIOpenCamera(_cameraId), "OpenCamera");
        Check(AsiNative.ASIInitCamera(_cameraId), "InitCamera");
        Check(AsiNative.ASISetROIFormat(_cameraId, _width, _height, 1, AsiNative.AsiImgType.Raw16), "SetROIFormat");
        _buffer = new byte[(long)_width * _height * 2];
        _running = true;
    }

    public void Stop()
    {
        if (_cameraId >= 0)
        {
            try { AsiNative.ASIStopExposure(_cameraId); } catch { /* ignore */ }
            try { AsiNative.ASICloseCamera(_cameraId); } catch { /* ignore */ }
            _cameraId = -1;
        }
        _running = false;
        _buffer = null;
    }

    public Mat? GrabFrame()
    {
        if (!_running || _cameraId < 0 || _buffer is not { } buffer) return null;
        try
        {
            AsiNative.ASISetControlValue(_cameraId, AsiNative.AsiControlType.Exposure,
                new CLong((int)(_exposureSeconds * 1_000_000)), AsiNative.AsiBool.False);
            AsiNative.ASISetControlValue(_cameraId, AsiNative.AsiControlType.Gain,
                new CLong((int)Gain), AsiNative.AsiBool.False);

            if (AsiNative.ASIStartExposure(_cameraId, AsiNative.AsiBool.False) != AsiNative.AsiError.Success)
                return null;

            var maxWaitMs = (int)(_exposureSeconds * 1000) + 10_000;
            var waited = 0;
            while (true)
            {
                AsiNative.ASIGetExpStatus(_cameraId, out var status);
                if (status == AsiNative.AsiExposureStatus.Success) break;
                if (status == AsiNative.AsiExposureStatus.Failed) return null;
                Thread.Sleep(20);
                waited += 20;
                if (waited > maxWaitMs) { AsiNative.ASIStopExposure(_cameraId); return null; }
            }

            if (AsiNative.ASIGetDataAfterExp(_cameraId, buffer, new CLong(buffer.Length)) != AsiNative.AsiError.Success)
                return null;

            var mat = new Mat(_height, _width, MatType.CV_16UC1);
            Marshal.Copy(buffer, 0, mat.Data, buffer.Length);
            return mat;
        }
        catch
        {
            return null;
        }
    }

    // --- IExposureControl (Sekunden) -----------------------------------------
    public bool AutoExposure { get => false; set { /* manuell */ } }
    public double Exposure { get => _exposureSeconds; set => _exposureSeconds = Math.Clamp(value, MinExposure, MaxExposure); }
    public double MinExposure => 0.000032; // 32 µs (typ. ASI-Minimum)
    public double MaxExposure => 2000.0;

    // --- Gain (nicht Teil von IExposureControl) ------------------------------
    public double Gain { get; set; } = 100;
    public double GainMin => 0;
    public double GainMax => 600;

    public void Dispose() => Stop();

    private static void Check(AsiNative.AsiError e, string op)
    {
        if (e != AsiNative.AsiError.Success)
            throw new InvalidOperationException($"ASI {op} fehlgeschlagen: {e}");
    }
}

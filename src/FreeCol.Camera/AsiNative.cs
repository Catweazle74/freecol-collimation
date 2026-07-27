using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FreeCol.Camera;

/// <summary>
/// P/Invoke-Bindings für das native ZWO-ASI-SDK (<c>libASICamera2</c>). Direkter
/// USB-Zugriff ohne Server — der Null-Setup-Schnellpfad für ZWO-Kameras, wenn
/// FreeCol auf der Maschine mit der angesteckten Kamera läuft.
///
/// C-<c>long</c> via <see cref="CLong"/> — Linux/macOS 8 Byte, Windows 4 Byte.
/// Alle Struct-Felder und P/Invoke-Parameter, die im Original-Header
/// (ASICamera2.h) als <c>long</c> deklariert sind (MaxWidth/MaxHeight in
/// <see cref="AsiCameraInfo"/>, Control-Werte, Puffergröße), verwenden deshalb
/// <see cref="CLong"/> statt <see cref="long"/>: <see cref="CLong"/> marshallt
/// plattformnativ und liefert so unter Windows (C-<c>long</c> = 4 Byte) die
/// korrekte Byte-Breite. Ohne diese Umstellung wurden MaxWidth/MaxHeight unter
/// Windows aus falsch ausgerichteten Bytes gelesen → sinnlose ROI-Maße →
/// <c>ASI_ERROR_INVALID_SIZE</c> bei <c>ASISetROIFormat</c>. Unter Linux/macOS
/// bleibt das Verhalten unverändert (<see cref="CLong"/> ist dort 8 Byte, wie
/// zuvor <see cref="long"/>). Der crossplattform-Universalweg ist Alpaca;
/// dieser Pfad ergänzt ihn für direkte ZWO-Nutzung.
/// </summary>
internal static class AsiNative
{
    private const string Lib = "ASICamera2";

    static AsiNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(AsiNative).Assembly, Resolve);
    }

    /// <summary>Stellt sicher, dass der statische Konstruktor (Resolver) lief.</summary>
    public static void EnsureLoaded() { }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Lib) return IntPtr.Zero;
        foreach (var candidate in Candidates())
        {
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        }
        return IntPtr.Zero; // Standard-Suche übernimmt
    }

    private static System.Collections.Generic.IEnumerable<string> Candidates()
    {
        // 1) Per Umgebungsvariable überschreibbar.
        var env = Environment.GetEnvironmentVariable("FREECOL_ASI_LIB");
        if (!string.IsNullOrEmpty(env)) yield return env;

        if (OperatingSystem.IsWindows())
        {
            // Neben der FreeCol.exe (dokumentierter Ablageort, BETA-WINDOWS.md) …
            yield return Path.Combine(AppContext.BaseDirectory, "ASICamera2.dll");
            // … sowie eine installierte ASI-Studio-Instanz (Standardpfade).
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(pf, "ASIStudio", "ASICamera2.dll");
            yield return Path.Combine(pf, "ASIStudio", "lib", "ASICamera2.dll");
            yield return Path.Combine(pf, "ZWO Design", "ASIStudio", "ASICamera2.dll");
            yield return Lib; // Standardsuche (App-Verzeichnis, PATH)
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            // Neben dem Binary — dort legt scripts/setup-asi-macos.sh das Paar
            // libASICamera2.dylib + libusb-1.0.0.dylib ab (im .app also
            // Contents/MacOS/). Die dylib von ZWO referenziert libusb über einen
            // absoluten Homebrew-Pfad; das Script biegt das auf @loader_path um,
            // weshalb beide Dateien zusammen im selben Verzeichnis liegen müssen.
            yield return Path.Combine(AppContext.BaseDirectory, "libASICamera2.dylib");

            // Von Hand installiertes SDK.
            yield return "/usr/local/lib/libASICamera2.dylib";
            yield return "/opt/homebrew/lib/libASICamera2.dylib";
            yield return "libASICamera2.dylib";
            yield return Lib; // Standardsuche (DYLD_*-Pfade)

            // Bewusst NICHT durchsucht: /Applications/ASIStudio.app. Die dort
            // mitgelieferte libASICamera2.dylib ist i386/x86_64 (ASI Studio läuft
            // unter Rosetta); FreeCol ist osx-arm64 und kann sie grundsätzlich
            // nicht laden. arm64 gibt es erst im separaten ZWO-Camera-SDK
            // (lib/mac_arm64, ab V1.41 verifiziert).
            yield break;
        }

        // Linux: ASIStudio-Standardinstallation (Marcs Rechner) …
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, "ASIStudio", "lib", "libASICamera2.so");

        // … dann übliche System-/lokale Pfade + bloßer Name (Standardsuche).
        yield return "/usr/local/lib/libASICamera2.so";
        yield return "/usr/lib/libASICamera2.so";
        yield return "libASICamera2.so";
        yield return Lib;
    }

    // --- Enums (alle int) ----------------------------------------------------
    public enum AsiBool { False = 0, True = 1 }

    public enum AsiImgType { Raw8 = 0, Rgb24 = 1, Raw16 = 2, Y8 = 3, End = -1 }

    public enum AsiControlType
    {
        Gain = 0,
        Exposure = 1, // Mikrosekunden
        Gamma = 2,
        WbR = 3,
        WbB = 4,
        Offset = 5,
        BandwidthOverload = 6,
        Overclock = 7,
        Temperature = 8,
        Flip = 9,
        HighSpeedMode = 14,
    }

    public enum AsiExposureStatus { Idle = 0, Working = 1, Success = 2, Failed = 3 }

    public enum AsiError
    {
        Success = 0, InvalidIndex, InvalidId, InvalidControlType, CameraClosed, CameraRemoved,
        InvalidPath, InvalidFileFormat, InvalidSize, InvalidImgType, OutOfBoundary, Timeout,
        InvalidSequence, BufferTooSmall, VideoModeActive, ExposureInProgress, GeneralError,
        InvalidMode, End,
    }

    // ASI_CAMERA_INFO — Reihenfolge/Typen exakt wie ASICamera2.h; MaxHeight/
    // MaxWidth sind dort C-long → CLong (plattformnative Breite, s. Kopfkommentar).
    [StructLayout(LayoutKind.Sequential)]
    public struct AsiCameraInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] Name;
        public int CameraID;
        public CLong MaxHeight;
        public CLong MaxWidth;
        public AsiBool IsColorCam;
        public int BayerPattern;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public int[] SupportedBins;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] SupportedVideoFormat;
        public double PixelSize;
        public AsiBool MechanicalShutter;
        public AsiBool ST4Port;
        public AsiBool IsCoolerCam;
        public AsiBool IsUSB3Host;
        public AsiBool IsUSB3Camera;
        public float ElecPerADU;
        public int BitDepth;
        public AsiBool IsTriggerCam;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Unused;

        public readonly string GetName() =>
            Name is null ? "" : System.Text.Encoding.ASCII.GetString(Name).TrimEnd('\0').Trim();
    }

    [DllImport(Lib)] public static extern int ASIGetNumOfConnectedCameras();
    [DllImport(Lib)] public static extern AsiError ASIGetCameraProperty(out AsiCameraInfo info, int cameraIndex);
    [DllImport(Lib)] public static extern AsiError ASIOpenCamera(int cameraId);
    [DllImport(Lib)] public static extern AsiError ASIInitCamera(int cameraId);
    [DllImport(Lib)] public static extern AsiError ASICloseCamera(int cameraId);
    [DllImport(Lib)] public static extern AsiError ASISetControlValue(int cameraId, AsiControlType type, CLong value, AsiBool auto);
    [DllImport(Lib)] public static extern AsiError ASIGetControlValue(int cameraId, AsiControlType type, out CLong value, out AsiBool auto);
    [DllImport(Lib)] public static extern AsiError ASISetROIFormat(int cameraId, int width, int height, int bin, AsiImgType imgType);
    [DllImport(Lib)] public static extern AsiError ASIGetROIFormat(int cameraId, out int width, out int height, out int bin, out AsiImgType imgType);
    [DllImport(Lib)] public static extern AsiError ASIStartExposure(int cameraId, AsiBool isDark);
    [DllImport(Lib)] public static extern AsiError ASIStopExposure(int cameraId);
    [DllImport(Lib)] public static extern AsiError ASIGetExpStatus(int cameraId, out AsiExposureStatus status);
    [DllImport(Lib)] public static extern AsiError ASIGetDataAfterExp(int cameraId, byte[] buffer, CLong bufferSize);
}

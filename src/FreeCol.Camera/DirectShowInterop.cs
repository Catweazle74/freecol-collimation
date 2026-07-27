using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FreeCol.Camera;

/// <summary>
/// Minimale COM-Interop-Deklarationen für DirectShow, um die Video-Eingabe-Geräte
/// (Kategorie <c>VideoInputDeviceCategory</c>) unter Windows aufzuzählen — analog
/// zur sysfs-Enumeration auf Linux. Bewusst auf das Nötigste beschränkt (keine
/// zusätzliche NuGet-Abhängigkeit, kein vollständiger DirectShow-Wrapper).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DirectShowInterop
{
    /// <summary>CLSID des System Device Enumerator.</summary>
    public static readonly Guid ClsidSystemDeviceEnum =
        new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");

    /// <summary>Kategorie-GUID für Video-Eingabegeräte (Kameras).</summary>
    public static readonly Guid CategoryVideoInputDevice =
        new("860BB310-5D01-11d0-BD3B-00A0C911CE86");

    [ComImport]
    [Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(
            in Guid pType,
            out IEnumMoniker? ppEnumMoniker,
            int dwFlags);
    }

    [ComImport]
    [Guid("00000102-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumMoniker
    {
        // [Out] + ArraySubType=Interface sind BEIDE zwingend: ohne [Out] marshalt die
        // Runtime das Array nur hinein und schreibt die von COM gelieferten Moniker
        // nicht zurück — rgelt[0] bliebe null. Ohne ArraySubType würden die Elemente
        // als IDispatch statt IUnknown gemarshalt, was DirectShow-Moniker nicht
        // unterstützen.
        [PreserveSig]
        int Next(
            int celt,
            [Out][MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Interface, SizeConst = 1)] IMoniker[] rgelt,
            out int pceltFetched);

        [PreserveSig]
        int Skip(int celt);

        void Reset();

        void Clone(out IEnumMoniker ppenum);
    }

    [ComImport]
    [Guid("0000000C-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IBindCtx;

    [ComImport]
    [Guid("00000109-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMoniker
    {
        // IPersist
        void GetClassID(out Guid pClassID);

        // IPersistStream-Teil ausgelassen — hier nicht benötigt (nur Auszug der vtable-
        // relevanten Signaturen für die tatsächlich genutzten Methoden weiter unten).
        [PreserveSig]
        int IsDirty();

        void Load(object pstm);

        void Save(object pstm, [MarshalAs(UnmanagedType.Bool)] bool fClearDirty);

        void GetSizeMax(out long pcbSize);

        // IMoniker
        void BindToObject(
            IBindCtx? pbc,
            IMoniker? pmkToLeft,
            in Guid riidResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppvResult);

        void BindToStorage(
            IBindCtx? pbc,
            IMoniker? pmkToLeft,
            in Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppvObj);

        void Reduce(
            IBindCtx? pbc,
            int dwReduceHowFar,
            ref IMoniker? ppmkToLeft,
            out IMoniker ppmkReduced);

        void ComposeWith(IMoniker pmkRight, [MarshalAs(UnmanagedType.Bool)] bool fOnlyIfNotGeneric, out IMoniker ppmkComposite);

        void Enum([MarshalAs(UnmanagedType.Bool)] bool fForward, out IEnumMoniker ppenumMoniker);

        [PreserveSig]
        int IsEqual(IMoniker pmkOtherMoniker);

        void Hash(out int pdwHash);

        [PreserveSig]
        int IsRunning(IBindCtx? pbc, IMoniker? pmkToLeft, IMoniker? pmkNewlyRunning);

        [PreserveSig]
        int GetTimeOfLastChange(IBindCtx? pbc, IMoniker? pmkToLeft, out long pFileTime);

        void Inverse(out IMoniker ppmk);

        void CommonPrefixWith(IMoniker pmkOther, out IMoniker ppmkPrefix);

        void RelativePathTo(IMoniker pmkOther, out IMoniker ppmkRelPath);

        void GetDisplayName(IBindCtx pbc, IMoniker? pmkToLeft, [MarshalAs(UnmanagedType.LPWStr)] out string ppszDisplayName);

        void ParseDisplayName(
            IBindCtx pbc,
            IMoniker? pmkToLeft,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out int pchEaten,
            out IMoniker ppmkOut);

        [PreserveSig]
        int IsSystemMoniker(out int pdwMksys);
    }

    // VARIANT-Parameter werden über den eingebauten object<->VARIANT-Marshaler
    // des Runtime-Interop ("ref object") übergeben — das übliche, bewährte
    // Muster für IPropertyBag in DirectShow-Interop-Code (kein manuelles
    // VARIANT-Layout nötig).
    [ComImport]
    [Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyBag
    {
        [PreserveSig]
        int Read(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
            ref object pVar,
            IntPtr pErrorLog);

        [PreserveSig]
        int Write(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
            ref object pVar);
    }
}

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace FreeCol.Camera;

/// <summary>
/// Minimale P/Invoke-Deklarationen für den Configuration Manager (cfgmgr32), um zu
/// einer Geräte-Instanz-ID die des übergeordneten Geräteknotens zu ermitteln. Nötig
/// für zusammengesetzte USB-Geräte: deren Kamera-Interface (<c>&amp;MI_00</c>) trägt
/// keine Seriennummer, sie steht erst am USB-Geräteknoten eine Ebene darüber
/// (siehe <see cref="CameraEnumerator"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DeviceInstanceInterop
{
    private const int CrSuccess = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint dnDevInst, StringBuilder buffer, uint bufferLen, uint ulFlags);

    /// <summary>
    /// Liefert die Geräte-Instanz-ID des übergeordneten Geräteknotens, oder
    /// <c>null</c>, wenn der Knoten nicht auffindbar ist oder keinen Elternknoten hat.
    /// Fehlschläge sind best-effort und werfen nicht — die Kamera bleibt dann ohne
    /// Seriennummer nutzbar.
    /// </summary>
    public static string? ParentDeviceId(string instanceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out var devInst, instanceId, 0) != CrSuccess
                || CM_Get_Parent(out var parent, devInst, 0) != CrSuccess
                || CM_Get_Device_ID_Size(out var length, parent, 0) != CrSuccess)
            {
                return null;
            }

            // CM_Get_Device_ID_Size liefert die Länge OHNE abschließende NUL.
            var buffer = new StringBuilder((int)length + 1);
            if (CM_Get_Device_IDW(parent, buffer, length + 1, 0) != CrSuccess)
            {
                return null;
            }

            var id = buffer.ToString();
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }
}

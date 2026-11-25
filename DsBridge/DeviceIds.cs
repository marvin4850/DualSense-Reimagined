using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace DsBridge;

/// <summary>Windows Container ID: one GUID shared by every interface of one physical device (the pad's HID interface and
/// its audio endpoint). Read-only queries (cfgmgr32 / the endpoint property store); null when Windows gives none.</summary>
public static class DeviceIds
{
    // DEVPKEY_Device_ContainerId / PKEY_Device_ContainerId = {8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c}, 2
    static readonly Guid ContainerFmt = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c");

    [StructLayout(LayoutKind.Sequential)] struct DevPropKey { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type, byte[]? buffer, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_PropertyW(string iface, ref DevPropKey key, out uint type, byte[]? buffer, ref uint size, uint flags);

    // DEVPKEY_Device_InstanceId = {78c34fc8-104a-4aca-9ea4-524d52996e57}, 256 (on a device interface: its device)
    static readonly DevPropKey InstanceIdKey = new() { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };

    /// <summary>Container of the device behind a HID interface path (\\?\hid#vid_054c&amp;pid_0ce6...#{...}).</summary>
    public static Guid? ContainerOfHid(string interfacePath)
    {
        try
        {
            var key = InstanceIdKey; uint size = 0;
            CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, null, ref size, 0);
            if (size == 0) return null;
            var buf = new byte[size];
            if (CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, buf, ref size, 0) != 0) return null;
            var inst = System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
            if (CM_Locate_DevNodeW(out var dn, inst, 0) != 0) return null;
            var ck = new DevPropKey { fmtid = ContainerFmt, pid = 2 }; uint cs = 16; var g = new byte[16];
            if (CM_Get_DevNode_PropertyW(dn, ref ck, out _, g, ref cs, 0) != 0 || cs != 16) return null;
            var id = new Guid(g);
            return id == Guid.Empty ? null : id;
        }
        catch { return null; }
    }

    /// <summary>Container of an audio endpoint (its property store), null when absent.</summary>
    public static Guid? ContainerOf(MMDevice d)
    {
        try
        {
            var store = d.Properties;
            for (int i = 0; i < store.Count; i++)
            {
                var k = store.Get(i);
                if (k.formatId != ContainerFmt || k.propertyId != 2) continue;
                var v = store.GetValue(i).Value;
                return v switch { Guid g => g, string s when Guid.TryParse(s, out var g2) => g2, _ => null };
            }
        }
        catch { }
        return null;
    }
}

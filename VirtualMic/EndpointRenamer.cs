using System.Runtime.InteropServices;

namespace VRCGuiter.VirtualMic;

/// <summary>オーディオエンドポイントの表示名を書き換える（管理者権限が必要）。</summary>
public static class EndpointRenamer
{
    private static readonly Guid DeviceFmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private const int PidDeviceDesc = 2;      // PKEY_Device_DeviceDesc（サウンド設定で変えられる名前）
    private const int PidFriendlyName = 14;   // PKEY_Device_FriendlyName（アプリに見える完全な名前）
    private const int StgmReadWrite = 2;
    private const ushort VtLpwstr = 31;

    public static void Rename(string endpointId, string shortName, string fullName)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        Check(enumerator.GetDevice(endpointId, out IMMDevice device));
        Check(device.OpenPropertyStore(StgmReadWrite, out IPropertyStore store));
        try
        {
            SetString(store, new PropertyKey { fmtid = DeviceFmtid, pid = PidDeviceDesc }, shortName);
            SetString(store, new PropertyKey { fmtid = DeviceFmtid, pid = PidFriendlyName }, fullName);
            Check(store.Commit());
        }
        finally
        {
            Marshal.ReleaseComObject(store);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static void SetString(IPropertyStore store, PropertyKey key, string value)
    {
        var pv = new PropVariant { vt = VtLpwstr, p = Marshal.StringToCoTaskMemUni(value) };
        try { Check(store.SetValue(ref key, ref pv)); }
        finally { Marshal.FreeCoTaskMem(pv.p); }
    }

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        int GetId(out IntPtr id);
        int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out int count);
        int GetAt(int index, out PropertyKey key);
        int GetValue(ref PropertyKey key, out PropVariant value);
        int SetValue(ref PropertyKey key, ref PropVariant value);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
    }
}

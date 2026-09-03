// DeviceSwitcher.cs - default capture device switching while talking.
// IME voice input (WeType etc.) and Windows voice typing record from the system
// default microphone, so on voice-key press we switch the default capture device
// to the cable's capture side ("CABLE Output") and restore it on release.
// Uses the well-known undocumented IPolicyConfig COM interface (vtable slot 10
// placeholder methods, then SetDefaultEndpoint) - same technique as
// AudioSwitcher/SoundSwitch/RemoteMapper.
// 中文：默认麦克风切换 —— 说话期间切到虚拟声卡录音端，结束后自动恢复
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

sealed class DeviceSwitcher {
    enum ERole : uint { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
    enum EDataFlow { eRender = 0, eCapture = 1 }
    [Flags] enum EDeviceState : uint { ACTIVE = 1, DISABLED = 2, NOTPRESENT = 4, UNPLUGGED = 8 }

    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator {
        void EnumAudioEndpoints(EDataFlow f, EDeviceState s, out IMMDeviceCollection c);
        void GetDefaultAudioEndpoint(EDataFlow f, ERole r, out IMMDevice d);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice d);
        void R1(IntPtr p); void R2(IntPtr p);   // Register/UnregisterEndpointNotificationCallback
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection { void GetCount(out uint n); void Item(uint i, out IMMDevice d); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice {
        int Activate();
        void OpenPropertyStore(uint stgm, out IPropertyStore ps);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out EDeviceState st);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore {
        void GetCount(out uint n);
        void GetAt(uint i, out PROPERTYKEY k);
        void GetValue([In] ref PROPERTYKEY k, IntPtr pv);
        int SetValue();
        void Commit();
    }
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] internal class CPolicyConfigClient { }
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig {
        int M0(); int M1(); int M2(); int M3(); int M4(); int M5(); int M6(); int M7(); int M8(); int M9();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole r);
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(IntPtr pv);
    static readonly Guid PKEY_Device_FriendlyName = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
    static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

    string targetId;           // cable capture endpoint id
    string savedDefault;       // endpoint id to restore

    public sealed class EndpointInfo {
        public string Id; public string Name;
    }

    public static List<EndpointInfo> ListCaptureEndpoints() {
        var result = new List<EndpointInfo>();
        RunSta(delegate {
            var e = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator));
            IMMDeviceCollection c;
            e.EnumAudioEndpoints(EDataFlow.eCapture, EDeviceState.ACTIVE, out c);
            uint n; c.GetCount(out n);
            for (uint i = 0; i < n; i++) {
                IMMDevice d; c.Item(i, out d);
                string id; d.GetId(out id);
                IPropertyStore ps; d.OpenPropertyStore(0, out ps);
                result.Add(new EndpointInfo { Id = id, Name = ReadName(ps) });
            }
        });
        return result;
    }

    public static string CurrentDefaultCaptureName() {
        string name = null;
        RunSta(delegate {
            var e = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator));
            IMMDevice d; e.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out d);
            IPropertyStore ps; d.OpenPropertyStore(0, out ps);
            name = ReadName(ps);
        });
        return name;
    }

    static string ReadName(IPropertyStore ps) {
        var key = new PROPERTYKEY { fmtid = PKEY_Device_FriendlyName, pid = 2 };
        IntPtr pv = Marshal.AllocCoTaskMem(40);
        for (int i = 0; i < 40; i++) Marshal.WriteByte(pv, i, 0);
        try {
            ps.GetValue(ref key, pv);
            short vt = Marshal.ReadInt16(pv);
            if (vt == 31) return Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8));
            return null;
        } finally { PropVariantClear(pv); Marshal.FreeCoTaskMem(pv); }
    }

    static void RunSta(Action a) {
        Exception err = null;
        var t = new Thread((ThreadStart)delegate { try { a(); } catch (Exception e) { err = e; } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        if (err != null) throw err;
    }

    /// Find the capture endpoint whose friendly name contains nameContains. Call at startup.
    public bool FindTarget(string nameContains) {
        try {
            foreach (var ep in ListCaptureEndpoints()) {
                if (ep.Name != null && ep.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0) {
                    targetId = ep.Id;
                    Log.Info("[DEV] capture target: " + ep.Name);
                    return true;
                }
            }
        } catch (Exception ex) { Log.Error("[DEV] enumerate failed: " + ex.Message); }
        targetId = null;
        return false;
    }

    public bool TargetFound { get { return targetId != null; } }

    /// Switch default capture to the target (call on voice-key press, before hotkey).
    public void SwitchToTarget() {
        if (targetId == null) return;
        try {
            RunSta(delegate {
                var e = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator));
                IMMDevice d; e.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out d);
                string id; d.GetId(out id);
                savedDefault = id;
                SetDefault(targetId);
            });
            Log.Info("[DEV] default capture -> cable");
        } catch (Exception ex) { Log.Error("[DEV] switch failed: " + ex.Message); }
    }

    /// Restore the previous default (call on voice-key release, after hotkey).
    public void Restore() {
        if (savedDefault == null) return;
        string prev = savedDefault;
        savedDefault = null;
        try {
            RunSta(delegate { SetDefault(prev); });
            Log.Info("[DEV] default capture restored");
        } catch (Exception ex) { Log.Error("[DEV] restore failed: " + ex.Message); }
    }

    static void SetDefault(string id) {
        var p = (IPolicyConfig)new CPolicyConfigClient();
        p.SetDefaultEndpoint(id, ERole.eConsole);
        p.SetDefaultEndpoint(id, ERole.eMultimedia);
        p.SetDefaultEndpoint(id, ERole.eCommunications);
    }
}

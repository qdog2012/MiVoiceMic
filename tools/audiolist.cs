// audiolist.cs - compare audio device visibility: write results to audiolist.out.txt
using System;
using System.IO;
using System.Runtime.InteropServices;

class AudioList {
    enum EDataFlow { eRender = 0, eCapture = 1 }
    enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
    [Flags] enum DSTATE : uint { ACTIVE = 1, DISABLED = 2, NOTPRESENT = 4, UNPLUGGED = 8, MASK_ALL = 15 }

    [StructLayout(LayoutKind.Sequential)] struct PK { public Guid fmtid; public uint pid; }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnum {
        void EnumAudioEndpoints(EDataFlow f, DSTATE s, out IColl c);
        void GetDefaultAudioEndpoint(EDataFlow f, ERole r, out IDev d);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IColl { void GetCount(out uint n); void Item(uint i, out IDev d); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDev {
        int Activate();
        void OpenPropertyStore(uint stgm, out IPropStore ps);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropStore {
        void GetCount(out uint n);
        void GetAt(uint i, out PK k);
        void GetValue([In] ref PK k, IntPtr pv);
    }
    [DllImport("ole32.dll")] static extern int PropVariantClear(IntPtr pv);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern int waveOutGetDevCaps(uint id, ref WOC pwoc, int cb);
    [DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WOC { public ushort a, b; public uint c; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name; }

    static StreamWriter w;
    static readonly Guid CLSID_ENUM = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid PKEY_NAME = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");

    static string ReadName(IPropStore ps) {
        var key = new PK { fmtid = PKEY_NAME, pid = 2 };
        IntPtr pv = Marshal.AllocCoTaskMem(40);
        for (int i = 0; i < 40; i++) Marshal.WriteByte(pv, i, 0);
        try {
            ps.GetValue(ref key, pv);
            if (Marshal.ReadInt16(pv) == 31) return Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8));
            return "?";
        } finally { PropVariantClear(pv); Marshal.FreeCoTaskMem(pv); }
    }

    static void Main() {
        using (w = new StreamWriter(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audiolist.out.txt"))) {
            w.WriteLine("user: " + Environment.UserName + " session " + System.Diagnostics.Process.GetCurrentProcess().SessionId);
            w.WriteLine("waveOut devices: " + waveOutGetNumDevs());
            uint n = waveOutGetNumDevs();
            for (uint i = 0; i < n; i++) {
                var c = new WOC();
                waveOutGetDevCaps(i, ref c, Marshal.SizeOf(c));
                w.WriteLine("  waveOut[" + i + "] = " + c.name);
            }
            try {
                var e = (IEnum)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ENUM));
                IColl c2; e.EnumAudioEndpoints(EDataFlow.eCapture, DSTATE.ACTIVE, out c2);
                uint cnt; c2.GetCount(out cnt);
                w.WriteLine("capture endpoints (ACTIVE): " + cnt);
                for (uint i = 0; i < cnt; i++) {
                    IDev d; c2.Item(i, out d);
                    string id; d.GetId(out id);
                    IPropStore ps; d.OpenPropertyStore(0, out ps);
                    w.WriteLine("  [" + i + "] " + ReadName(ps));
                }
                IDev def; e.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out def);
                IPropStore dps; def.OpenPropertyStore(0, out dps);
                w.WriteLine("default capture: " + ReadName(dps));
            } catch (Exception ex) {
                w.WriteLine("MMDevice enum FAILED: " + ex.Message + " hr=0x" + Marshal.GetHRForException(ex).ToString("X8"));
            }
        }
    }
}

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class RI {
    [StructLayout(LayoutKind.Sequential)] public struct DEVLIST { public IntPtr hDevice; public uint dwType; }
    [DllImport("user32.dll")] public static extern uint GetRawInputDeviceList([Out] DEVLIST[] list, ref uint count, uint size);
    [DllImport("user32.dll")] public static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint cmd, StringBuilder data, ref uint size);
    public static void Run() {
        uint count = 0, size = (uint)Marshal.SizeOf(typeof(DEVLIST));
        uint r = GetRawInputDeviceList(null, ref count, size);
        Console.WriteLine("probe r=" + r + " count=" + count + " size=" + size);
        var list = new DEVLIST[count];
        uint r2 = GetRawInputDeviceList(list, ref count, size);
        Console.WriteLine("fill r=" + r2);
        int kb = 0;
        foreach (var d in list) {
            if (d.dwType != 1) continue;
            kb++;
            uint sz = 0;
            uint ra = GetRawInputDeviceInfo(d.hDevice, 0x20000003, null, ref sz);
            Console.WriteLine("kb#" + kb + " hDevice=0x" + d.hDevice.ToInt64().ToString("X") + " firstRet=" + ra + " sz=" + sz);
            if (sz > 0 && sz < 1024) {
                var sb = new StringBuilder((int)sz);
                uint rb = GetRawInputDeviceInfo(d.hDevice, 0x20000003, sb, ref sz);
                Console.WriteLine("   secondRet=" + rb + " name=" + sb.ToString());
            }
        }
        Console.WriteLine("keyboards=" + kb);
    }
}
'@
[RI]::Run()

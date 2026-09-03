// injecttest.cs - quick SendInput verification (writes result file)
using System;
using System.IO;
using System.Runtime.InteropServices;

class InjectTest {
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk; public ushort scan; public uint flags; public uint time; public UIntPtr extra; }

    static void Main() {
        using (var w = new StreamWriter(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "injecttest.out.txt"))) {
            IntPtr fg = GetForegroundWindow();
            uint pid; GetWindowThreadProcessId(fg, out pid);
            string fgName = "?";
            try { fgName = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
            w.WriteLine("foreground: " + fgName);
            var i = new INPUT[2];
            i[0] = new INPUT { type = 1, keyboard = new KEYBDINPUT { vk = 0x74, scan = 0x74, flags = 0 } };
            i[1] = new INPUT { type = 1, keyboard = new KEYBDINPUT { vk = 0x74, scan = 0x74, flags = 2 } };
            uint r = SendInput(2, i, 40);
            w.WriteLine("SendInput(F5 down+up) -> " + r + "/2 err=" + Marshal.GetLastWin32Error());
        }
    }
}

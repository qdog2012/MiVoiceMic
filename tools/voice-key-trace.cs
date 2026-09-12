// Read-only diagnostic: voice keys and modifiers only; never records typed text.
using System;
using System.Runtime.InteropServices;
using System.Threading;

static class VoiceKeyTrace {
    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct Key { public uint vk, scan, flags, time; public IntPtr extra; }
    static readonly int[] observed = {0x74,0x83,0xA0,0xA1,0xA2,0xA3,0xA4,0xA5,0x5B,0x5C};
    static readonly HookProc callback = OnKey;
    static string State() {
        string result = "";
        foreach (int vk in observed) if ((GetAsyncKeyState(vk) & 0x8000) != 0) result += vk.ToString("X2") + " ";
        return result.Length == 0 ? "none" : result.Trim();
    }
    static IntPtr OnKey(int code, IntPtr w, IntPtr l) {
        if (code >= 0) {
            Key k = (Key)Marshal.PtrToStructure(l, typeof(Key));
            if (Array.IndexOf(observed, (int)k.vk) >= 0)
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " msg=" + w.ToInt64().ToString("X4") + " vk=" + k.vk.ToString("X2") + " scan=" + k.scan.ToString("X2") + " flags=" + k.flags.ToString("X2") + " pre-state=" + State());
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }
    static int Main(string[] args) {
        if (args.Length > 1) {
            var output = new System.IO.StreamWriter(args[1], false);
            output.AutoFlush = true;
            Console.SetOut(output);
        }
        uint thread = GetCurrentThreadId();
        IntPtr hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) { Console.WriteLine("Hook failed: " + Marshal.GetLastWin32Error()); return 1; }
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 90;
        using (var timer = new Timer(delegate { PostThreadMessage(thread, 0x0012, IntPtr.Zero, IntPtr.Zero); }, null, seconds * 1000, Timeout.Infinite)) {
            Console.WriteLine("Voice-key trace ready: " + DateTime.Now.ToString("HH:mm:ss.fff"));
            MSG message;
            while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0) { }
        }
        UnhookWindowsHookEx(hook);
        return 0;
    }
}

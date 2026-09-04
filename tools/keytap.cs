// keytap.cs - tap the given virtual key via SendInput (layout must match the
// 40-byte x64 INPUT; see injecttest.cs). Usage: keytap <vkHex> [vkHex ...]
using System;
using System.Runtime.InteropServices;

class KeyTap {
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk; public ushort scan; public uint flags; public uint time; public UIntPtr extra; }

    static int Main(string[] args) {
        if (args.Length == 0) { Console.WriteLine("usage: keytap <vkHex> [vkHex ...]"); return 2; }
        var keys = new ushort[args.Length];
        for (int i = 0; i < args.Length; i++) keys[i] = Convert.ToUInt16(args[i], 16);
        var inputs = new INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++) {
            inputs[i] = new INPUT { type = 1, keyboard = new KEYBDINPUT { vk = keys[i], scan = keys[i], flags = 0 } };
            inputs[inputs.Length - 1 - i] = new INPUT { type = 1, keyboard = new KEYBDINPUT { vk = keys[i], scan = keys[i], flags = 2 } };
        }
        uint r = SendInput((uint)inputs.Length, inputs, 40);
        Console.WriteLine("SendInput -> " + r + "/" + inputs.Length + " err=" + Marshal.GetLastWin32Error());
        return r == inputs.Length ? 0 : 1;
    }
}

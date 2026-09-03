// HotkeyInjector.cs - SendInput-based combo hold/release/tap for the IME voice
// hotkey (WeType default: Ctrl+Win), plus key-name parsing.
//
// Two proven subtleties (from RemoteMapper NOTES.md):
//  1. Right Alt is an EXTENDED key - must set KEYEVENTF_EXTENDEDKEY or the IME
//     sees Left Alt and triggers its own behaviour.
//  2. While the remote's voice key is held, the remote spams HID F5. Our
//     low-level keyboard hook marshals ALL system input through the hook thread,
//     and that traffic disturbs injection timing. So we suspend the hook while
//     injecting and re-install it afterwards from the pump thread.
// 中文：SendInput 组合键注入 —— 输入法语音热键的按住/点按控制
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

static class VkNames {
    static readonly Dictionary<string, ushort> map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase) {
        { "LCTRL", 0xA2 }, { "RCTRL", 0xA3 }, { "LALT", 0xA4 }, { "RALT", 0xA5 },
        { "LSHIFT", 0xA0 }, { "RSHIFT", 0xA1 }, { "LWIN", 0x5B }, { "RWIN", 0x5C },
        { "CTRL", 0xA2 }, { "ALT", 0xA4 }, { "SHIFT", 0xA0 }, { "WIN", 0x5B },
        { "COMMA", 0xBC }, { "PERIOD", 0xBE }, { "SPACE", 0x20 }, { "ENTER", 0x0D },
        { "TAB", 0x09 }, { "ESC", 0x1B }, { "BACKSPACE", 0x08 }, { "DELETE", 0x2E },
        { "F1", 0x70 }, { "F2", 0x71 }, { "F3", 0x72 }, { "F4", 0x73 }, { "F5", 0x74 },
        { "F6", 0x75 }, { "F7", 0x76 }, { "F8", 0x77 }, { "F9", 0x78 },
        { "UP", 0x26 }, { "DOWN", 0x28 }, { "LEFT", 0x25 }, { "RIGHT", 0x27 },
    };

    public static bool TryParse(string name, out ushort vk) {
        if (string.IsNullOrEmpty(name)) { vk = 0; return false; }
        if (map.TryGetValue(name.Trim(), out vk)) return true;
        if (name.Length == 1) {
            char c = name[0];
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z')) { vk = (ushort)c; return true; }
        }
        return false;
    }

    public static ushort[] ParseList(IList<string> names) {
        var list = new List<ushort>();
        if (names != null)
            foreach (string n in names) {
                ushort vk;
                if (TryParse(n, out vk) && !list.Contains(vk)) list.Add(vk);
                else if (!string.IsNullOrEmpty(n)) Log.Warn("[CFG] unknown key name: " + n);
            }
        if (list.Count == 0) { list.Add(0xA2); list.Add(0x5B); }   // Ctrl+Win fallback (WeType default)
        return list.ToArray();
    }
}

sealed class HotkeyInjector {
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    const int INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_SCANCODE = 0x0008;
    const uint MAPVK_VK_TO_VSC = 0;

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort vk; public ushort scan; public uint flags; public uint time; public UIntPtr extra; }

    readonly ushort[] combo;      // e.g. { RAlt, Comma } - modifiers first, trigger last
    readonly bool tapMode;        // true: tap combo on press and again on release

    public HotkeyInjector(IList<string> keyNames, string mode) {
        combo = VkNames.ParseList(keyNames);
        tapMode = mode != null && mode.Equals("tap", StringComparison.OrdinalIgnoreCase);
    }

    public string Describe() {
        var parts = new List<string>();
        foreach (ushort vk in combo) parts.Add("0x" + vk.ToString("X2"));
        return string.Join("+", parts.ToArray()) + (tapMode ? " (tap x2)" : " (hold)");
    }

    static bool IsExtended(ushort vk) {
        return vk == 0x21 || vk == 0x22 || vk == 0x23 || vk == 0x24 ||
               vk == 0x25 || vk == 0x26 || vk == 0x27 || vk == 0x28 ||
               vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C ||
               vk == 0x5D || vk == 0xA3 || vk == 0xA5;
    }

    static INPUT Make(ushort vk, bool down) {
        uint flags = KEYEVENTF_SCANCODE;
        if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= KEYEVENTF_KEYUP;
        return new INPUT {
            type = INPUT_KEYBOARD,
            keyboard = new KEYBDINPUT { vk = vk, scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC), flags = flags }
        };
    }

    static bool PressAll(ushort[] keys) {
        var inputs = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++) inputs[i] = Make(keys[i], true);
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))) == inputs.Length;
    }
    static bool ReleaseAll(ushort[] keys) {
        var inputs = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++) inputs[i] = Make(keys[keys.Length - 1 - i], false);
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))) == inputs.Length;
    }
    static bool TapOnce(ushort[] keys) {
        var inputs = new INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++) inputs[i] = Make(keys[i], true);
        for (int i = 0; i < keys.Length; i++) inputs[keys.Length + i] = Make(keys[keys.Length - 1 - i], false);
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))) == (uint)inputs.Length;
    }

    static void ForcedRelease(ushort vk) {
        uint flags = KEYEVENTF_KEYUP | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        keybd_event((byte)vk, (byte)MapVirtualKey(vk, MAPVK_VK_TO_VSC), flags, UIntPtr.Zero);
    }

    /// Voice key pressed: default-mic already switched by caller.
    public void OnVoiceDown() {
        InputRouter.Suspend();
        try {
            foreach (ushort vk in combo) ForcedRelease(vk);   // clean slate
            System.Threading.Thread.Sleep(50);
            if (tapMode) {
                if (!TapOnce(combo)) Log.Warn("[KEY] tap down failed");
            } else {
                if (!PressAll(combo)) Log.Warn("[KEY] press failed");
            }
        } finally { InputRouter.Resume(); }
    }

    /// Voice key released.
    public void OnVoiceUp() {
        InputRouter.Suspend();
        try {
            if (tapMode) {
                if (!TapOnce(combo)) Log.Warn("[KEY] tap up failed");
            } else {
                if (!ReleaseAll(combo)) Log.Warn("[KEY] release failed");
            }
        } finally { InputRouter.Resume(); }
    }

    /// Safety net: release held keys (on exit / link loss).
    public void ForceRelease() {
        try {
            InputRouter.Suspend();
            foreach (ushort vk in combo) ForcedRelease(vk);
            InputRouter.Resume();
        } catch (Exception ex) { Log.Warn("[KEY] force release: " + ex.Message); }
    }
}

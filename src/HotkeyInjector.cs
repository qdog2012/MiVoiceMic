// HotkeyInjector.cs - SendInput-based combo hold/release/tap for the IME voice
// hotkey (WeType default: Ctrl+Win), plus key-name parsing.
//
// Injection details:
//  1. Right Alt is an EXTENDED key - must set KEYEVENTF_EXTENDEDKEY or the IME
//     sees Left Alt and triggers its own behaviour.
//  2. The remote sends HID F5 (F20 with MiRemoteHidFilter) while held. Keep the
//     input hook installed throughout injection. Move it ahead of newer hooks
//     and release stale voice-key state before the combo; simply swallowing an
//     event does not hide it from hooks earlier in the chain.
// 中文：SendInput 组合键注入 —— 输入法语音热键的按住/点按控制
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        vk = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (map.TryGetValue(name.Trim(), out vk)) return true;
        return KeyMapNames.TryParse(name, out vk) && IsSupported(vk);
    }

    public static bool IsSupported(ushort vk) {
        return vk > 0 && vk < 0xFF && !KeyMapNames.Name(vk).StartsWith("0x", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParseList(IList<string> names, out ushort[] keys) {
        keys = new ushort[0];
        if (names == null || names.Count == 0) return false;
        var list = new List<ushort>();
        foreach (string n in names) {
            ushort vk;
            if (!TryParse(n, out vk)) return false;
            if (!list.Contains(vk)) list.Add(vk);
        }
        keys = list.ToArray();
        return keys.Length > 0;
    }

    public static ushort[] ParseList(IList<string> names) {
        if (names == null || names.Count == 0) return new ushort[] { 0xA2, 0x5B };
        ushort[] keys;
        if (TryParseList(names, out keys)) return keys;
        Log.Warn("[CFG] 无效语音组合键，已停止注入（请重新设置完整快捷键）");
        return new ushort[0];
    }
}

sealed class HotkeyInjector : IVoiceHotkey {
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
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
        if (combo.Length == 0) return "已禁用（无效快捷键）";
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
        return SendChecked(inputs, "按下", keys);
    }
    static bool ReleaseAll(ushort[] keys) {
        var inputs = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++) inputs[i] = Make(keys[keys.Length - 1 - i], false);
        return SendChecked(inputs, "松开", keys);
    }
    static bool TapOnce(ushort[] keys) {
        var inputs = new INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++) inputs[i] = Make(keys[i], true);
        for (int i = 0; i < keys.Length; i++) inputs[keys.Length + i] = Make(keys[keys.Length - 1 - i], false);
        return SendChecked(inputs, "点按", keys);
    }

    static bool SendChecked(INPUT[] inputs, string action, ushort[] keys) {
        string target = "未知";
        try {
            uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            using (var process = Process.GetProcessById((int)pid)) target = process.ProcessName;
        } catch { }
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        int error = Marshal.GetLastWin32Error();
        string detail = "[KEY] " + action + " " + KeyMapNames.FriendlyCombo(keys) +
            ": " + sent + "/" + inputs.Length + "，前台应用=" + target +
            "，累计拦截语音键事件=" + System.Threading.Interlocked.Read(ref InputRouter.SwallowedVoiceKeyCount);
        if (sent == inputs.Length) Log.Info(detail);
        else Log.Warn(detail + "，系统错误=" + error);
        return sent == inputs.Length;
    }

    static void ForcedRelease(ushort vk) {
        uint flags = KEYEVENTF_KEYUP | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        keybd_event((byte)vk, (byte)MapVirtualKey(vk, MAPVK_VK_TO_VSC), flags, UIntPtr.Zero);
    }

    /// Release stale keys before the cancellable settling period.
    public void PrepareVoiceDown() {
        if (combo.Length == 0) return;
        if (InputRouter.RefreshVoiceBlocker()) {
            // The IME may already have observed the first physical voice-key
            // DOWN before our hook. Send only UPs to clear its tracked state;
            // subsequent physical repeats stay blocked by the refreshed hook.
            ushort[] voiceKeys = InputRouter.VoiceKeysToRelease();
            foreach (ushort vk in voiceKeys) ForcedRelease(vk);
            Log.Info("[KEY] 已刷新语音键拦截顺序并清理 " + KeyMapNames.FriendlyCombo(voiceKeys) + " 按下状态");
        }
        foreach (ushort vk in combo) ForcedRelease(vk);   // clean slate
        System.Threading.Thread.Sleep(50);
    }

    /// Caller rechecks session validity after PrepareVoiceDown, before committing.
    public void OnVoiceDown() {
        if (combo.Length == 0) return;
        if (tapMode) {
            if (!TapOnce(combo)) Log.Warn("[KEY] tap down failed");
        } else {
            if (!PressAll(combo)) Log.Warn("[KEY] press failed");
        }
    }

    /// Voice key released.
    public void OnVoiceUp() {
        if (combo.Length == 0) return;
        if (tapMode) {
            if (!TapOnce(combo)) Log.Warn("[KEY] tap up failed");
        } else {
            if (!ReleaseAll(combo)) Log.Warn("[KEY] release failed");
        }
    }

    /// Safety net: release held keys (on exit / link loss).
    public void ForceRelease() {
        if (combo.Length == 0) return;
        try {
            foreach (ushort vk in combo) ForcedRelease(vk);
        } catch (Exception ex) { Log.Warn("[KEY] force release: " + ex.Message); }
    }
}

// RemoteKeys.cs - driverless key remapping for the remote's Windows-visible keys
// (voice=F5, arrows, OK/Enter). Replaces VoiceKeyBlocker as the single
// WH_KEYBOARD_LL owner.
//
// Attribution: the low-level hook cannot see which device a key came from, but
// Raw Input can. A hidden sink window (own thread, RIDEV_INPUTSINK) records
// {vk, down, tick, fromRemote} for every keyboard event; the hook callback
// correlates the event in front of it against that record (short bounded wait)
// and only remotes keys that Raw Input attributes to the remote's HID device
// (matched by VID:PID, default 2717:32B8). Failure to attribute always falls
// back to "physical keyboard" -> pass through.
//
// Gestures: click / long-press per key, actions = combo (tap or hold-through),
// task view, launch app, shell command. Executed on a dedicated worker thread,
// never inside the hook callback.
// 中文：输入路由器 —— Raw Input 设备归因 + WH_KEYBOARD_LL 拦截 + 点按/长按手势引擎
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

static class InputRouter {
    // ---- hook -------------------------------------------------------------
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int px, py; }
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr extra; }

    const int WH_KEYBOARD_LL = 13;
    const uint WM_APP_REHOOK = 0x8000;
    const uint LLKHF_INJECTED = 0x10;
    const ushort VK_F5 = 0x74;

    static readonly HookProc proc = HookCb;
    static volatile IntPtr hhk = IntPtr.Zero;
    static Thread pump;
    static uint pumpTid;
    static volatile bool blockF5;         // swallow remote/physical F5 while linked
    static volatile bool linked;          // BLE link connected
    public static long SwallowedCount;
    static int lastPassLogTick;           // hook thread only: throttle passthrough notes

    // ---- mapping state ----------------------------------------------------
    static readonly object mapGate = new object();
    static volatile bool mappingEnabled;
    static GestureEngine engine = new GestureEngine(new KeyMapEntry[0]);
    static Thread mapWorker;
    static readonly BlockingCollection<GestureEngine.RawEvent> mapQueue =
        new BlockingCollection<GestureEngine.RawEvent>();

    /// UI callback (worker thread): (keyId, description) when a mapped action fires.
    public static event Action<string, string> ActionFired;

    static void FireAction(string id, string desc) {
        var h = ActionFired;
        if (h != null) { try { h(id, desc); } catch { } }
    }

    static IntPtr HookCb(int nCode, IntPtr wParam, IntPtr lParam) {
        try {
            if (nCode >= 0 && linked) {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                if ((k.flags & LLKHF_INJECTED) == 0) {          // never touch injected input
                    bool down = wParam == (IntPtr)0x0100 || wParam == (IntPtr)0x0104;
                    uint vk = k.vkCode;
                    bool f5Case = vk == VK_F5 && blockF5;        // legacy blocker semantics
                    bool mapCase = false;
                    if (mappingEnabled) lock (mapGate) mapCase = engine.HasBinding(vk);
                if (f5Case || mapCase) {
                    bool remote = true;
                    if (mapCase) remote = RawSink.IsFromRemote(vk, down, k.time);
                    if (remote) {
                        Interlocked.Increment(ref SwallowedCount);
                        if (mapCase) mapQueue.TryAdd(new GestureEngine.RawEvent { Vk = (ushort)vk, Down = down, TickMs = NowMs() });
                        return (IntPtr)1;                    // swallowed
                    }
                    if (f5Case) {                            // physical F5 while linked: still swallowed (old behavior)
                        Interlocked.Increment(ref SwallowedCount);
                        return (IntPtr)1;
                    }
                    int t = Environment.TickCount;           // mapped key from another device: throttled note
                    if (t - lastPassLogTick > 2000 || t < lastPassLogTick) {
                        lastPassLogTick = t;
                        Log.Info("[INPUT] 0x" + vk.ToString("X2") + (down ? " 按下" : " 松开") +
                                 " 已映射但未归因遥控器 → 放行（物理键）");
                    }
                }
                }
            }
        } catch { }
        return CallNextHookEx(hhk, nCode, wParam, lParam);
    }

    static long NowMs() { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; }

    /// Unit-test hook: simulate a key event and return the verdict (1 = swallowed).
    internal static IntPtr TestDispatch(uint vk, bool down, bool fromRemote) {
        var k = new KBDLLHOOKSTRUCT { vkCode = vk };
        IntPtr mem = Marshal.AllocHGlobal(Marshal.SizeOf(k));
        try {
            if (fromRemote) RawSink.SeedForTest(vk, down);
            Marshal.StructureToPtr(k, mem, false);
            return HookCb(0, (IntPtr)(down ? 0x0100 : 0x0101), mem);
        } finally { Marshal.FreeHGlobal(mem); }
    }
    internal static IntPtr TestDispatch(uint vk, bool down) { return TestDispatch(vk, down, true); }

    public static void Start() {
        if (pump != null) return;
        pump = new Thread((ThreadStart)delegate {
            pumpTid = GetCurrentThreadId();
            hhk = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            Log.Info("[INPUT] router armed (blockF5 " + (blockF5 ? "on" : "off") +
                     ", mapping " + (mappingEnabled ? "on" : "off") + " while linked)");
            MSG m;
            while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) {
                if (m.message == WM_APP_REHOOK && hhk == IntPtr.Zero && suspendDepth == 0)
                    hhk = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
        }) { IsBackground = true, Name = "inputrouter" };
        pump.Start();
        RawSink.Start();
        if (mapWorker == null) {
            mapWorker = new Thread((ThreadStart)delegate {
                var pending = new List<GestureEngine.OutAction>();
                while (true) {
                    GestureEngine.RawEvent ev;
                    while (mapQueue.TryTake(out ev, 20)) {
                        try {
                            lock (mapGate) engine.Feed(ev.Vk, ev.Down, ev.TickMs, pending);
                            Flush(pending);
                        } catch (Exception ex) { Log.Error("[INPUT] worker: " + ex.Message); }
                    }
                    try {
                        lock (mapGate) engine.Tick(NowMs(), pending);
                        Flush(pending);
                    } catch (Exception ex) { Log.Error("[INPUT] tick: " + ex.Message); }
                }
            }) { IsBackground = true, Name = "keymap" };
            mapWorker.Start();
        }
    }

    public static void Stop() {
        if (hhk != IntPtr.Zero) { UnhookWindowsHookEx(hhk); hhk = IntPtr.Zero; }
        RawSink.Stop();
    }

    public static void SetBlockF5(bool on) { blockF5 = on; }
    public static void SetLinked(bool on) { linked = on; }
    public static void SetMappingEnabled(bool on) { mappingEnabled = on; }

    /// Swap in a new mapping table (config edit / preset load). Safe at any time.
    public static void SetKeyMap(KeyMapConfig map) { SetKeyMap(map, null); }

    public static void SetKeyMap(KeyMapConfig map, string macPrefix) {
        if (map == null) { mappingEnabled = false; return; }
        List<KeyMapEntry> bound = new List<KeyMapEntry>();
        foreach (KeyMapEntry e in map.keys) if (e != null && e.Mapped) bound.Add(e);
        lock (mapGate) {
            engine = new GestureEngine(bound.ToArray());
            mappingEnabled = map.enabled && bound.Count > 0;
            RawSink.SetMatchers(map.matchVidPid, macPrefix);
        }
        Log.Info("[INPUT] keymap: " + bound.Count + " mapped key(s), " + (mappingEnabled ? "enabled" : "disabled"));
    }

    static void Flush(List<GestureEngine.OutAction> actions) {
        for (int i = 0; i < actions.Count; i++) Execute(actions[i]);
        actions.Clear();
    }

    static void Execute(GestureEngine.OutAction a) {
        try {
            KeyMapAction act = a.Action;
            switch (act.Kind) {
                case MapActionKind.Combo: {
                    ushort[] combo;
                    if (!KeyMapNames.TryParseCombo(act.keys, out combo)) return;
                    if (act.tap) KeySender.Tap(combo);
                    else if (a.Phase == GestureEngine.Phase.Down) KeySender.Press(combo);
                    else KeySender.Release(combo);
                    break;
                }
                case MapActionKind.TaskView:
                    KeySender.Tap(new ushort[] { 0x5B, 0x09 });      // Win+Tab
                    break;
                case MapActionKind.Launch: {
                    string path, args;
                    SplitCommand(act.command, out path, out args);
                    if (path.Length > 0)
                        Process.Start(new ProcessStartInfo { FileName = path, Arguments = args, UseShellExecute = true });
                    break;
                }
                case MapActionKind.Cmd:
                    Process.Start(new ProcessStartInfo {
                        FileName = "cmd.exe", Arguments = "/c " + act.command,
                        CreateNoWindow = true, UseShellExecute = false
                    });
                    break;
            }
            string desc = KeyMapNames.Describe(act);
            Log.Info("[INPUT] " + a.EntryName + " -> " + desc + (a.Phase == GestureEngine.Phase.Up ? " (release)" : ""));
            FireAction(a.EntryId, desc);
        } catch (Exception ex) {
            Log.Error("[INPUT] exec " + a.EntryName + ": " + ex.Message);
        }
    }

    internal static void SplitCommand(string command, out string path, out string args) {
        path = ""; args = "";
        if (string.IsNullOrWhiteSpace(command)) return;
        command = command.Trim();
        if (command[0] == '"') {
            int end = command.IndexOf('"', 1);
            if (end > 0) {
                path = command.Substring(1, end - 1);
                args = end + 1 < command.Length ? command.Substring(end + 1).Trim() : "";
                return;
            }
        }
        int sp = command.IndexOf(' ');
        if (sp < 0) { path = command; return; }
        path = command.Substring(0, sp);
        args = command.Substring(sp + 1).Trim();
    }

    // ---- injection-time hook suspension (shared with HotkeyInjector) -------
    static int suspendDepth;
    public static void Suspend() {
        if (Interlocked.Increment(ref suspendDepth) == 1 && hhk != IntPtr.Zero) {
            UnhookWindowsHookEx(hhk); hhk = IntPtr.Zero;
        }
    }
    public static void Resume() {
        if (Interlocked.Decrement(ref suspendDepth) == 0 && pumpTid != 0)
            PostThreadMessage(pumpTid, WM_APP_REHOOK, IntPtr.Zero, IntPtr.Zero);
    }
}

// ---- gesture engine (pure logic, unit-testable) ---------------------------
sealed class GestureEngine {
    public enum Phase { Down, Up, Once }

    public struct RawEvent { public ushort Vk; public bool Down; public long TickMs; }

    public struct OutAction { public string EntryId; public string EntryName; public KeyMapAction Action; public Phase Phase; }

    sealed class KeyState {
        public long DownSince; public bool Armed; public bool HoldFired; public KeyMapEntry Entry;
    }

    readonly Dictionary<ushort, KeyMapEntry> byVk = new Dictionary<ushort, KeyMapEntry>();
    readonly Dictionary<ushort, KeyState> states = new Dictionary<ushort, KeyState>();

    public GestureEngine(IList<KeyMapEntry> entries) {
        if (entries == null) return;
        foreach (KeyMapEntry e in entries) {
            ushort vk = e.VkCode;
            if (vk != 0 && !byVk.ContainsKey(vk)) byVk[vk] = e;
        }
    }

    public int Count { get { return byVk.Count; } }

    public bool HasBinding(uint vk) { return byVk.ContainsKey((ushort)vk); }

    static bool HoldConfigured(KeyMapEntry e) { return e.hold != null && e.hold.HasPayload; }

    public void Feed(ushort vk, bool down, long now, List<OutAction> out_) {
        KeyMapEntry e;
        if (!byVk.TryGetValue(vk, out e)) return;
        if (down) {
            KeyState st;
            if (!states.TryGetValue(vk, out st)) { st = new KeyState(); states[vk] = st; }
            if (st.DownSince > 0) return;                        // repeat / stuck: ignore
            st.Entry = e; st.DownSince = now; st.HoldFired = false;
            if (HoldConfigured(e)) { st.Armed = true; }          // wait for threshold or early release
            else {
                st.Armed = false;
                EmitClick(e, Phase.Down, out_);                  // no hold gesture: act immediately
            }
        } else {
            KeyState st;
            if (!states.TryGetValue(vk, out st) || st.DownSince == 0) return;
            st.DownSince = 0;
            if (st.HoldFired) {
                if (e.hold != null && e.hold.Kind == MapActionKind.Combo && !e.hold.tap)
                    out_.Add(Make(e, e.hold, Phase.Up));          // release hold-through combo
            } else if (st.Armed) {
                EmitClick(e, Phase.Once, out_);                  // released before hold threshold
            } else {
                if (e.click != null && e.click.Kind == MapActionKind.Combo && !e.click.tap)
                    out_.Add(Make(e, e.click, Phase.Up));         // release hold-through combo
            }
            st.Armed = false;
        }
    }

    void EmitClick(KeyMapEntry e, Phase phase, List<OutAction> out_) {
        if (e.click == null || !e.click.HasPayload) return;
        if (phase == Phase.Once && e.click.Kind == MapActionKind.Combo && !e.click.tap) {
            // hold-through combo released quickly: press + release back-to-back
            out_.Add(Make(e, e.click, Phase.Down));
            out_.Add(Make(e, e.click, Phase.Up));
        } else {
            out_.Add(Make(e, e.click, phase));
        }
    }

    public void Tick(long now, List<OutAction> out_) {
        foreach (var kv in states) {
            KeyState st = kv.Value;
            if (st.DownSince == 0 || st.HoldFired || !st.Armed) continue;
            KeyMapEntry e = st.Entry;
            uint ms = e.hold != null && e.hold.ms > 0 ? e.hold.ms : 600;
            if (now - st.DownSince >= ms) {
                st.HoldFired = true;
                if (e.hold != null && e.hold.HasPayload) {
                    if (e.hold.Kind == MapActionKind.Combo && !e.hold.tap)
                        out_.Add(Make(e, e.hold, Phase.Down));
                    else
                        out_.Add(Make(e, e.hold, Phase.Once));
                }
            }
        }
    }

    static OutAction Make(KeyMapEntry e, KeyMapAction a, Phase p) {
        OutAction o;
        o.EntryId = e.id; o.EntryName = e.name; o.Action = a; o.Phase = p;
        return o;
    }
}

// ---- combo sender (SendInput, hook suspended during injection) -------------
static class KeySender {
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    const int INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort vk; public ushort scan; public uint flags; public uint time; public UIntPtr extra; }

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
        INPUT i = new INPUT();
        i.type = INPUT_KEYBOARD;
        i.keyboard = new KEYBDINPUT { vk = vk, scan = (ushort)MapVirtualKey(vk, 0), flags = flags };
        return i;
    }

    public static void Press(ushort[] keys) { SendAll(keys, true); }
    public static void Release(ushort[] keys) { SendAll(keys, false); }
    public static void Tap(ushort[] keys) {
        var inputs = new List<INPUT>();
        for (int i = 0; i < keys.Length; i++) inputs.Add(Make(keys[i], true));
        for (int i = keys.Length - 1; i >= 0; i--) inputs.Add(Make(keys[i], false));
        InputRouter.Suspend();
        try { SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT))); }
        finally { InputRouter.Resume(); }
    }

    static void SendAll(ushort[] keys, bool down) {
        var inputs = new INPUT[keys.Length];
        int idx = 0;
        for (int i = 0; i < keys.Length; i++) inputs[idx++] = down ? Make(keys[i], true) : Make(keys[keys.Length - 1 - i], false);
        InputRouter.Suspend();
        try { SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))); }
        finally { InputRouter.Resume(); }
    }
}

// ---- raw input attribution sink --------------------------------------------
static class RawSink {
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern short RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, System.Text.StringBuilder pData, ref uint pcbSize);
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);

    struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    /// Startup visibility: list every raw-input keyboard and mark which one
    /// attribution picks as the remote (goes to MiVoiceMic.log).
    static void LogRawKeyboards() {
        try {
            uint count = 0, size = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
            if (GetRawInputDeviceList(null, ref count, size) == unchecked((uint)-1) || count == 0) return;
            var list = new RAWINPUTDEVICELIST[count];
            if (GetRawInputDeviceList(list, ref count, size) == unchecked((uint)-1)) return;
            int keyboards = 0; string matched = null;
            lock (ringGate) {
                foreach (var d in list) {
                    if (d.dwType != 1) continue;                  // RIM_TYPEKEYBOARD
                    keyboards++;
                    var sb = new System.Text.StringBuilder(512);
                    uint sz = (uint)sb.Capacity;
                    uint r = GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICENAME, sb, ref sz);
                    if (r == unchecked((uint)-1) || r == 0) continue;
                    string name = sb.ToString();
                    bool remote = DeviceIsRemote(d.hDevice);
                    Log.Info("[INPUT] raw 键盘: " + name + (remote ? "  <= 遥控器" : ""));
                    if (remote) matched = name;
                }
            }
            Log.Info("[INPUT] raw 键盘共 " + keyboards + " 台; 遥控器归因: " +
                     (matched != null ? "已匹配" : "未发现（检查 keymap.matchVidPid / deviceMacPrefix）"));
        } catch (Exception ex) { Log.Error("[INPUT] rawlist: " + ex.Message); }
    }
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS { public uint style; public WndProc lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hBackground; public string lpszMenuName; public string lpszClassName; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWKEYBOARD { public ushort MakeCode; public ushort Flags; public ushort Reserved; public ushort VKey; public uint Message; public ulong ExtraInformation; }

    [StructLayout(LayoutKind.Explicit)]
    struct RAWINPUT {
        [FieldOffset(0)] public RAWINPUTHEADER header;
        [FieldOffset(24)] public RAWKEYBOARD keyboard;
    }

    const uint WM_INPUT = 0x00FF;
    const uint RIDEV_INPUTSINK = 0x00000100;
    const uint RID_INPUT = 0x100000;
    const uint RIDI_DEVICENAME = 0x20000007;   // NOT 0x20000003 - the wrong value made every query fail

    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int px, py; }

    static Thread thread;
    static IntPtr hwnd = IntPtr.Zero;
    static readonly WndProc proc = SinkProc;
    // raw config (re-parsed on Start) + derived substrings matched against the
    // lowercase raw-input device path
    static List<string> vidPid = new List<string> { "2717:32B8" };
    static string macPrefix = "";
    static List<string> matchers = BuildMatchers(vidPid, macPrefix);

    // correlation ring
    struct Rec { public ushort Vk; public bool Down; public long Tick; public bool Remote; }
    const int RING = 64;
    static readonly Rec[] ring = new Rec[RING];
    static int ringHead;                                    // next write slot
    static readonly object ringGate = new object();
    static readonly Dictionary<IntPtr, bool> deviceCache = new Dictionary<IntPtr, bool>();

    /// <param name="vidPidList">Config entries like "2717:32B8" (hex, "0x" ok).</param>
    /// <param name="macPrefix">Optional BLE MAC prefix like "C0:5D:39" - the raw
    /// input path of a HID-over-GATT device embeds its MAC, so this is a
    /// model-independent extra matcher.</param>
    public static void SetMatchers(List<string> vidPidList, string mac) {
        if (vidPidList != null && vidPidList.Count > 0) vidPid = vidPidList;
        macPrefix = mac ?? "";
        List<string> m = BuildMatchers(vidPid, macPrefix);
        lock (ringGate) { matchers = m; deviceCache.Clear(); }
    }

    /// USB keyboards enumerate as HID\VID_2717&amp;PID_32B8\... but HID-over-GATT
    /// (BLE) keyboards as HID\{1812guid}_DEV_VID&amp;012717_PID&amp;32b8_REV&amp;00a4_&lt;mac&gt; -
    /// VID is stored as (vendorIdSource&lt;&lt;16)|vid, source 01=Bluetooth SIG /
    /// 02=USB-IF, so both spellings must be matched (the USB-style substring
    /// alone never matches a BLE remote - that silently disabled keymap
    /// attribution on the real device).
    static List<string> BuildMatchers(List<string> vidPidList, string macPrefix) {
        var m = new List<string>();
        if (vidPidList != null)
            foreach (string s in vidPidList) {
                if (string.IsNullOrWhiteSpace(s)) continue;
                string[] parts = s.Split(':');
                uint vid, pid;
                if (parts.Length == 2 && TryHex(parts[0].Trim(), out vid) && TryHex(parts[1].Trim(), out pid)) {
                    string v = vid.ToString("x4"), p = pid.ToString("x4");
                    m.Add("vid_" + v + "&pid_" + p);          // USB HID
                    m.Add("vid&0" + v + "_pid&" + p);         // BLE HOGP, SIG vendor source
                    m.Add("vid&2" + v + "_pid&" + p);         // BLE HOGP, USB vendor source
                }
            }
        if (!string.IsNullOrWhiteSpace(macPrefix)) {
            var hex = new System.Text.StringBuilder();
            foreach (char c in macPrefix)
                if (Uri.IsHexDigit(c)) hex.Append(char.ToLowerInvariant(c));
            if (hex.Length >= 6) m.Add(hex.ToString(0, 6));   // "C0:5D:39" -> "c05d39"
        }
        if (m.Count == 0) return BuildMatchers(new List<string> { "2717:32B8" }, "");
        return m;
    }

    static bool TryHex(string s, out uint v) {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    public static void Start() {
        lock (ringGate) matchers = BuildMatchers(vidPid, macPrefix);   // re-derive (config may have landed before Start)
        if (thread != null) return;
        thread = new Thread((ThreadStart)delegate {
            var wc = new WNDCLASS();
            wc.lpfnWndProc = proc;
            wc.hInstance = GetModuleHandle(null);
            wc.lpszClassName = "MivmRawSink";
            RegisterClassW(ref wc);
            hwnd = CreateWindowEx(0, "MivmRawSink", "", 0, 0, 0, 0, 0, (IntPtr)(-3) /*HWND_MESSAGE*/, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { Log.Error("[INPUT] raw sink window failed: " + Marshal.GetLastWin32Error()); return; }
            var rid = new RAWINPUTDEVICE[1];
            rid[0].usUsagePage = 1; rid[0].usUsage = 6;       // generic desktop / keyboard
            rid[0].dwFlags = RIDEV_INPUTSINK;
            rid[0].hwndTarget = hwnd;
            if (!RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                Log.Error("[INPUT] RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());
            LogRawKeyboards();
            MSG m;
            while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
        }) { IsBackground = true, Name = "rawsink" };
        thread.Start();
    }

    public static void Stop() {
        if (hwnd != IntPtr.Zero) { DestroyWindow(hwnd); hwnd = IntPtr.Zero; }
    }

    static IntPtr SinkProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam) {
        if (msg == WM_INPUT) {
            uint size = 0;
            GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
            if (size > 0 && size <= 512) {
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try {
                    if (GetRawInputData(lParam, RID_INPUT, buf, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER))) == size) {
                        var raw = (RAWINPUT)Marshal.PtrToStructure(buf, typeof(RAWINPUT));
                        if (raw.header.dwType == 1) {          // RIM_TYPEKEYBOARD
                            bool down = raw.keyboard.Message == 0x0100 || raw.keyboard.Message == 0x0104;
                            bool remote = DeviceIsRemote(raw.header.hDevice);
                            long tick = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                            lock (ringGate) {
                                ring[ringHead].Vk = raw.keyboard.VKey;
                                ring[ringHead].Down = down;
                                ring[ringHead].Tick = tick;
                                ring[ringHead].Remote = remote;
                                ringHead = (ringHead + 1) % RING;
                            }
                        }
                    }
                } finally { Marshal.FreeHGlobal(buf); }
            }
        }
        return DefWindowProcW(h, msg, wParam, lParam);
    }

    static bool DeviceIsRemote(IntPtr hDevice) {
        if (hDevice == IntPtr.Zero) return false;             // SendInput injections
        lock (ringGate) {
            bool cached;
            if (deviceCache.TryGetValue(hDevice, out cached)) return cached;
            bool remote = false;
            try {
                // no NULL size-probe here: RIDI_DEVICENAME with pData=NULL fails
                // outright - a big-enough buffer must be passed in one call
                var sb = new System.Text.StringBuilder(512);
                uint sz = (uint)sb.Capacity;
                uint r = GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, sb, ref sz);
                if (r != unchecked((uint)-1) && r > 0) {
                    string path = sb.ToString().ToLowerInvariant();
                    foreach (string m in matchers)
                        if (path.IndexOf(m) >= 0) { remote = true; break; }
                }
            } catch { }
            deviceCache[hDevice] = remote;
            return remote;
        }
    }

    /// Correlate a hook event against recent raw input. Bounded wait (max ~8ms)
    /// because WM_INPUT may reach the sink thread slightly after the hook fires.
    public static bool IsFromRemote(uint vk, bool down, uint llTime) {
        long start = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        for (int attempt = 0; attempt < 12; attempt++) {
            lock (ringGate) {
                int newest = (ringHead - 1 + RING) % RING;
                for (int i = 0; i < RING; i++) {
                    int idx = (newest - i + RING * 2) % RING;
                    if (ring[idx].Vk == 0) break;
                    long age = start - ring[idx].Tick;
                    if (age > 250) break;
                    if (ring[idx].Vk == (ushort)vk && ring[idx].Down == down) return ring[idx].Remote;
                }
            }
            if (attempt < 11) Thread.Sleep(1);
        }
        return false;                                         // unattributed -> assume physical
    }

    /// Test helper: pretend the given event just arrived from the remote.
    internal static void SeedForTest(uint vk, bool down) {
        lock (ringGate) {
            ring[ringHead].Vk = (ushort)vk;
            ring[ringHead].Down = down;
            ring[ringHead].Tick = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            ring[ringHead].Remote = true;
            ringHead = (ringHead + 1) % RING;
        }
    }
}

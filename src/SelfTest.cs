// SelfTest.cs - offline logic tests (run with: MiVoiceMic.exe --selftest)
// 中文：离线单元测试 —— 解码/配置/手势/按键归因/会话恢复/蓝牙超时/快捷键捕获
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

static class SelfTest {
    static int passed, failed;

    static void Check(bool cond, string name) {
        if (cond) { passed++; Console.WriteLine("  PASS  " + name); }
        else { failed++; Console.WriteLine("  FAIL  " + name); }
    }

    public static int Run() {
        Console.WriteLine("== selftest ==");
        TestAdpcmRoundtrip();
        TestNibbleOrder();
        TestFrameAccumulation();
        TestSyncResetsDecoder();
        TestVkNames();
        TestConfigRoundtrip();
        TestWavWriter();
        TestF5Blocker();
        TestHookReplacement();
        TestKeyMap();
        TestChargeState();
        TestLooksFromRemote();
        TestDeferredAttribution();
        TestVoiceRecovery();
        TestVoiceKeyOwnership();
        TestBluetoothDeadlines();
        TestHotkeyValidation();
        TestComboCapture();
        Console.WriteLine("== " + passed + " passed, " + failed + " failed ==");
        return failed == 0 ? 0 : 1;
    }

    static void TestVoiceRecovery() {
        var guard = new VoiceSessionGuard();
        Check(guard.TimeoutReason(999999) == null, "voice: idle does not reconnect");
        guard.Start(6, 1000);
        long generation = guard.Generation;
        Check(guard.TimeoutReason(4999) == null, "voice: allow startup latency");
        Check(guard.TimeoutReason(5000) != null, "voice: no audio after START recovers");
        guard.Audio(2000);
        Check(guard.TimeoutReason(4499) == null, "voice: short packet gap tolerated");
        Check(guard.TimeoutReason(4500) != null, "voice: missing STOP with stalled audio recovers");
        Check(guard.IsDuplicate(6), "voice: duplicate START detected without resetting deadline");
        guard.Stop();
        Check(!guard.IsCurrent(generation) && guard.TimeoutReason(9000) == null, "voice: STOP cancels queued press and watchdog");
        guard.Start(7, 10000);
        Check(!guard.IsCurrent(generation), "voice: old audio/press cannot enter next session");
        for (int ms = 11000; ms <= 309000; ms += 1000) guard.Audio(ms);
        Check(guard.TimeoutReason(309000) == null, "voice: continuous audio survives ordinary pauses in speech");
        guard.Audio(310000);
        Check(guard.TimeoutReason(310000) != null, "voice: hard limit ends endless audio without STOP");
    }

    static void TestHookReplacement() {
        var live = new HashSet<IntPtr> { (IntPtr)1 };
        bool coveredDuringInstall = false, coveredDuringRemoval = false;
        IntPtr next = InputRouter.ReplaceHook((IntPtr)1, delegate {
            coveredDuringInstall = live.Contains((IntPtr)1);
            live.Add((IntPtr)2);
            return (IntPtr)2;
        }, delegate(IntPtr old) {
            coveredDuringRemoval = live.Contains((IntPtr)2);
            live.Remove(old);
        });
        Check(coveredDuringInstall && coveredDuringRemoval && next == (IntPtr)2 && live.Count == 1,
            "voice: moving blocker ahead of IME never opens an unfiltered gap");
        next = InputRouter.ReplaceHook(next, delegate { return IntPtr.Zero; }, delegate(IntPtr old) { live.Remove(old); });
        Check(next == (IntPtr)2 && live.Contains(next), "voice: failed hook replacement retains existing blocker");
        bool removed = false;
        next = InputRouter.ReplaceHook(IntPtr.Zero, delegate { return (IntPtr)3; }, delegate(IntPtr old) { removed = true; });
        Check(next == (IntPtr)3 && !removed, "voice: first hook installation does not remove an invalid handle");
    }

    sealed class FakeVoiceHotkey : IVoiceHotkey {
        public int Down, Up, Released;
        public bool FailDown, FailUp;
        public Action Prepare;
        public void PrepareVoiceDown() { if (Prepare != null) Prepare(); }
        public void OnVoiceDown() { Down++; if (FailDown) throw new Exception("partial press"); }
        public void OnVoiceUp() { Up++; if (FailUp) throw new Exception("release failed"); }
        public void ForceRelease() { Released++; }
    }

    static void TestVoiceKeyOwnership() {
        int restores = 0;
        var lease = new VoiceKeySession(delegate { restores++; });
        var oldKeys = new FakeVoiceHotkey();
        var newKeys = new FakeVoiceHotkey();
        lease.Begin(oldKeys, delegate { }, delegate { return true; });
        lease.Begin(newKeys, null, delegate { return true; });
        Check(oldKeys.Down == 1 && oldKeys.Up == 1 && oldKeys.Released == 1 && restores == 1,
            "voice: changing combo releases original keys and restores mic");
        lease.End(); lease.End();
        Check(newKeys.Up == 1 && newKeys.Released == 1, "voice: duplicate STOP does not toggle IME twice");
        var cancelled = new FakeVoiceHotkey();
        lease.Begin(cancelled, delegate { restores += 100; }, delegate { return false; });
        Check(cancelled.Down == 0 && restores == 1, "voice: fast release cancels queued key press");
        bool current = true;
        lease.Begin(cancelled, delegate { current = false; }, delegate { return current; });
        Check(cancelled.Down == 0 && restores == 2, "voice: STOP during mic delay restores without injecting");
        var broken = new FakeVoiceHotkey { FailDown = true };
        try { lease.Begin(broken, delegate { }, delegate { return true; }); } catch { }
        Check(broken.Released == 1 && restores == 3, "voice: partial key press failure still releases and restores");
        broken = new FakeVoiceHotkey { FailUp = true };
        lease.Begin(broken, delegate { }, delegate { return true; });
        try { lease.End(); } catch { }
        Check(broken.Released == 1 && restores == 4, "voice: release failure still forces key up and restores mic");
        lease.Begin(null, delegate { }, delegate { return true; });
        lease.End();
        Check(restores == 5, "voice: disabled injection still restores audio-only session");
        current = true;
        var stoppedDuringPrepare = new FakeVoiceHotkey { Prepare = delegate { current = false; } };
        lease.Begin(stoppedDuringPrepare, delegate { }, delegate { return current; });
        lease.End();
        Check(stoppedDuringPrepare.Down == 0 && stoppedDuringPrepare.Up == 0 && restores == 6,
            "voice: STOP during key preparation cancels both hold and tap without toggling IME");
        var gate = new object();
        bool preparedOutsideGate = false, checkedInsideGate = false;
        var guarded = new FakeVoiceHotkey { Prepare = delegate { preparedOutsideGate = !Monitor.IsEntered(gate); } };
        lease.Begin(guarded, null, delegate {
            if (Monitor.IsEntered(gate)) checkedInsideGate = true;
            return true;
        }, gate);
        lease.End();
        Check(preparedOutsideGate && checkedInsideGate && guarded.Down == 1 && guarded.Up == 1,
            "voice: preparation allows STOP while final validation and press share session lock");
    }

    static void TestBluetoothDeadlines() {
        var completed = new TaskCompletionSource<int>();
        completed.SetResult(42);
        Check(AsyncDeadline.Wait(completed.Task, CancellationToken.None, 1000, null).GetAwaiter().GetResult() == 42,
            "BLE: response received before await is retained");
        bool cancelled = false, timedOut = false;
        var pending = new TaskCompletionSource<int>();
        try { AsyncDeadline.Wait(pending.Task, CancellationToken.None, 20, delegate { cancelled = true; }).GetAwaiter().GetResult(); }
        catch (TimeoutException) { timedOut = true; }
        Check(timedOut && cancelled, "BLE: hung native operation times out and is cancelled");
        pending.SetException(new Exception("late native failure"));
        using (var source = new CancellationTokenSource()) {
            source.Cancel(); cancelled = false;
            bool stopped = false;
            try { AsyncDeadline.Wait(new TaskCompletionSource<int>().Task, source.Token, 10000,
                    delegate { cancelled = true; }).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && cancelled, "BLE: reconnect cancels old operation without waiting for timeout");
        }
    }

    static void TestHotkeyValidation() {
        ushort[] parsed;
        Check(!VkNames.TryParseList(new[] { "LCTRL", "0xFC" }, out parsed) && parsed.Length == 0,
            "hotkey: Ctrl+unknown is rejected as a whole, never downgraded to Ctrl");
        Check(VkNames.ParseList(new[] { "LCTRL", "0xFC" }).Length == 0, "hotkey: invalid saved combo injects nothing");
        Check(VkNames.TryParseList(new[] { " ctrl ", "f13", "CTRL" }, out parsed) && parsed.Length == 2,
            "hotkey: names match capture vocabulary, case and duplicates handled");
        Check(VkNames.TryParseList(new[] { "LWIN", "0x48" }, out parsed) && parsed[1] == 0x48,
            "hotkey: supported numeric key codes remain usable");
        Check(!VkNames.TryParseList(new[] { "LCTRL", "0xFFFF" }, out parsed), "hotkey: out-of-range key rejected");
        string prev = Config.OverridePath;
        string tmp = Path.Combine(Path.GetTempPath(), "mivoicemic_invalid_" + Guid.NewGuid().ToString("N") + ".json");
        try {
            Config.OverridePath = tmp;
            var cfg = new Config(); cfg.hotkey.keys = new List<string> { "LCTRL", "0xFC" }; cfg.Save();
            var loaded = Config.Load();
            Check(!loaded.hotkeyEnabled && loaded.hotkey.keys.Count == 2,
                "hotkey: invalid configuration disables injection without silently replacing user's combo");
        } finally { Config.OverridePath = prev; if (File.Exists(tmp)) File.Delete(tmp); }
    }

    sealed class CaptureProbe : ComboCaptureBox {
        public CaptureProbe() : base("LWIN+H") { }
        public void Arm() { OnMouseDown(new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 2, 2, 0)); }
        public void Down(System.Windows.Forms.Keys key) { OnKeyDown(new System.Windows.Forms.KeyEventArgs(key)); }
        public void Up(System.Windows.Forms.Keys key) { OnKeyUp(new System.Windows.Forms.KeyEventArgs(key)); }
    }

    static void TestComboCapture() {
        using (var box = new CaptureProbe()) {
            box.Arm();
            box.Down(System.Windows.Forms.Keys.ControlKey | System.Windows.Forms.Keys.Control);
            box.Down(System.Windows.Forms.Keys.LWin | System.Windows.Forms.Keys.Control);
            box.Down((System.Windows.Forms.Keys)0xFC | System.Windows.Forms.Keys.Control);
            Check(box.Capturing && box.Value == "LWIN+H", "capture: IME placeholder does not overwrite shortcut");
            box.Up(System.Windows.Forms.Keys.LWin | System.Windows.Forms.Keys.Control);
            Check(!box.Capturing && box.Value == "LCTRL+LWIN", "capture: modifier-only Ctrl+Win saved on release");
            box.Arm(); box.Down(System.Windows.Forms.Keys.H);
            Check(box.Value == "H", "capture: ordinary key does not acquire a phantom Win modifier");
            box.Arm(); box.Down(System.Windows.Forms.Keys.Escape);
            Check(box.Value == "H" && !box.Capturing, "capture: Escape preserves prior combo");
        }
    }

    static short[] MakeSine(int n, double freqHz, int amp) {
        var s = new short[n];
        for (int i = 0; i < n; i++)
            s[i] = (short)Math.Round(amp * Math.Sin(2 * Math.PI * freqHz * i / 16000.0));
        return s;
    }

    static void TestAdpcmRoundtrip() {
        var decoder = new AdpcmDecoder(false, 0);
        var signal = MakeSine(4800, 440, 6000);
        byte[] encoded = AdpcmDecoder.Encode(signal);
        Check(encoded.Length == (signal.Length + 1) / 2,
            "adpcm encode length (" + encoded.Length + " bytes for " + signal.Length + " samples)");
        var all = new List<short>();
        var decoderOut = decoder.Feed(encoded);
        int frames = decoderOut == null ? 0 : decoderOut.Count;
        if (decoderOut != null) foreach (var block in decoderOut) all.AddRange(block);
        int maxErr = 0;
        int skip = 480;   // decoder cold-start transient: step ramps 7 -> tracking in ~2 frames
        for (int i = skip; i < Math.Min(all.Count, signal.Length); i++)
            maxErr = Math.Max(maxErr, Math.Abs(all[i] - signal[i]));
        Check(all.Count == signal.Length, "adpcm sample count " + all.Count + "/" + signal.Length);
        Check(maxErr < 1500, "adpcm roundtrip max error " + maxErr + " < 1500");
        Check(frames > 0, "adpcm frames decoded: " + frames);
    }

    static void TestNibbleOrder() {
        // one byte 0x0F: high nibble 0 -> predictor += step>>3 = 0 (step 7: 7>>3=0) -> sample 0
        //                 low nibble F -> predictor -= 7+3+1+0? step 7: diff = 0+1+3+7=11 -> -11
        var decoder = new AdpcmDecoder(false, 0);
        decoder.FrameSize = 1;
        var outFrames = decoder.Feed(new byte[] { 0x0F });
        Check(outFrames != null && outFrames.Count == 1 && outFrames[0].Length == 2, "nibble: 2 samples from 1 byte");
        if (outFrames != null && outFrames.Count == 1) {
            // raw decode of byte 0x0F is [0, -11]; the 3-tap lowpass blends the
            // neighbour into sample 0: (0 + 2*0 + (-11)) >> 2 = -3
            Check(outFrames[0][0] == -3 && outFrames[0][1] == -11,
                "nibble order high-first + lowpass (" + outFrames[0][0] + "," + outFrames[0][1] + ")");
        }
    }

    static void TestFrameAccumulation() {
        // 120-byte frames delivered in odd chunks must decode identically to whole frames
        var signal = MakeSine(2400, 200, 4000);
        byte[] encoded = AdpcmDecoder.Encode(signal);
        var a = new AdpcmDecoder(false, 0);
        var whole = new List<short>();
        foreach (var b in a.Feed(encoded)) whole.AddRange(b);

        var c = new AdpcmDecoder(false, 0);
        var frag = new List<short>();
        int chunk = 53;
        for (int off = 0; off < encoded.Length; off += chunk) {
            int len = Math.Min(chunk, encoded.Length - off);
            var piece = new byte[len];
            Array.Copy(encoded, off, piece, 0, len);
            var r = c.Feed(piece);
            if (r != null) foreach (var b in r) frag.AddRange(b);
        }
        bool equal = whole.Count == frag.Count;
        if (equal) for (int i = 0; i < whole.Count; i++) if (whole[i] != frag[i]) { equal = false; break; }
        Check(equal, "fragmented feed == whole feed (" + frag.Count + " vs " + whole.Count + " samples)");
    }

    static void TestSyncResetsDecoder() {
        var d = new AdpcmDecoder(false, 0);
        d.FrameSize = 2;
        d.ApplySync(-1234, 30);
        // two zero nibbles: STEP[30]=130, diff=130>>3=16 -> -1218; then idx 29 (118): diff 14 -> -1204
        var r = d.Feed(new byte[] { 0x00, 0x00 });
        bool ok = false;
        if (r != null && r.Count == 1 && r[0].Length == 4) {
            // raw nibbles (all zero, step 130->29->28->27): [-1218,-1204,-1191,-1179]
            // lowpass: s0=(0+2*-1218+-1204)>>2=-910, s1=(-1218+2*-1204+-1191)>>2=-1205,
            //          s2=-1192, s3=-1179 (last sample untouched)
            ok = r[0][0] == -910 && r[0][1] == -1205 && r[0][2] == -1192 && r[0][3] == -1179;
        }
        Check(ok, "AUDIO_SYNC applied before next frame (" +
            (r != null && r.Count == 1 ? r[0][0] + "," + r[0][1] : "no output") + ")");
    }

    static void TestVkNames() {
        ushort vk;
        Check(VkNames.TryParse("RALT", out vk) && vk == 0xA5, "vk RALT=0xA5");
        Check(VkNames.TryParse("H", out vk) && vk == 0x48, "vk H=0x48");
        Check(!VkNames.TryParse("NOPE", out vk), "vk unknown rejected");
        var list = VkNames.ParseList(new List<string> { "LWIN", "H" });
        Check(list.Length == 2 && list[0] == 0x5B && list[1] == 0x48, "vk parse list LWIN+H");
        var empty = VkNames.ParseList(new List<string>());
        Check(empty.Length == 2 && empty[0] == 0xA2, "vk empty list falls back to Ctrl+Win");
    }

    static void TestConfigRoundtrip() {
        string tmp = Path.Combine(Path.GetTempPath(), "mivoicemic_test_cfg.json");
        string prevOverride = Config.OverridePath;
        try {
            Config.OverridePath = tmp;
            var cfg = new Config();
            cfg.hotkey.keys = new List<string> { "LWIN", "H" };
            cfg.hotkey.mode = "tap";
            cfg.agc = false;
            cfg.gainDb = 12;
            cfg.blockF5 = false;
            cfg.Save();
            var loaded = Config.Load();
            Check(loaded.hotkey.mode == "tap" && loaded.hotkey.keys.Count == 2 && loaded.hotkey.keys[0] == "LWIN",
                "config json roundtrip hotkey");
            Check(!loaded.agc && Math.Abs(loaded.gainDb - 12) < 0.001, "config json roundtrip audio");
            Check(!loaded.blockF5, "config json roundtrip blockF5");
        } finally {
            Config.OverridePath = prevOverride;
            try { File.Delete(tmp); } catch { }
        }
    }

    static void TestWavWriter() {
        var pcm = new List<short>();
        for (int i = 0; i < 100; i++) pcm.Add((short)i);
        string path = Path.Combine(Path.GetTempPath(), "mivoicemic_test.wav");
        WavWriter.Write(path, pcm, 16000);
        var bytes = File.ReadAllBytes(path);
        bool ok = bytes.Length == 44 + 200 &&
                  bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
                  bytes[8] == 'W' && bytes[9] == 'A' && bytes[10] == 'V' && bytes[11] == 'E';
        Check(ok, "wav header (" + bytes.Length + " bytes)");
        try { File.Delete(path); } catch { }
    }

    static void TestF5Blocker() {
        // drive the hook callback directly (SendInput itself is denied on a locked desktop)
        InputRouter.SetMappingEnabled(false);
        InputRouter.SetBlockF5(true);
        InputRouter.SetLinked(true);
        long before = InputRouter.SwallowedCount;
        IntPtr swallowed = InputRouter.TestDispatch(0x74, true, true);
        long mid = InputRouter.SwallowedCount;
        IntPtr passed = InputRouter.TestDispatch(0x41, true, true);
        long afterLinked = InputRouter.SwallowedCount;
        InputRouter.SetLinked(false);
        IntPtr passedWhenUnlinked = InputRouter.TestDispatch(0x74, true, true);
        long end = InputRouter.SwallowedCount;
        InputRouter.SetLinked(true);
        Check(swallowed == (IntPtr)1 && mid == before + 1, "hook swallows F5 while linked");
        Check(passed != (IntPtr)1 && afterLinked == mid, "hook passes non-F5 while linked");
        Check(passedWhenUnlinked != (IntPtr)1 && end == afterLinked, "hook passes F5 when unlinked");
        Check(InputRouter.TestDispatch(0x83, true) == (IntPtr)1, "voice: driver F20 down is blocked even with key mapping off");
        Check(InputRouter.TestDispatch(0x83, true) == (IntPtr)1, "voice: held F20 repeats cannot disturb the IME combo");
        Check(InputRouter.TestDispatch(0x83, false) == (IntPtr)1, "voice: driver F20 up is blocked");
        Check(InputRouter.TestDispatch(0xA2, true, true, true) != (IntPtr)1 &&
              InputRouter.TestDispatch(0x5B, true, true, true) != (IntPtr)1 &&
              InputRouter.TestDispatch(0xA2, false, true, true) != (IntPtr)1 &&
              InputRouter.TestDispatch(0x5B, false, true, true) != (IntPtr)1,
              "voice: Ctrl+Win injection passes the installed blocker on press and release");
        Check(InputRouter.TestDispatch(0x83, true, true, true) != (IntPtr)1,
              "voice: injected F20 remains usable as a configured output shortcut");
        InputRouter.SetBlockF5(false);
        Check(InputRouter.TestDispatch(0x74, true) != (IntPtr)1 && InputRouter.TestDispatch(0x83, true) != (IntPtr)1,
              "voice: disabling the blocker passes both raw and driver voice keys");
        InputRouter.SetBlockF5(true);
        InputRouter.SetLinked(false);
        Check(InputRouter.TestDispatch(0x83, true) != (IntPtr)1, "voice: F20 follows existing disconnected passthrough behavior");
        InputRouter.SetLinked(true);
    }

    static void TestKeyMap() {
        // JSON model roundtrip via Config
        string prevOverride = Config.OverridePath;
        string tmp = Path.Combine(Path.GetTempPath(), "mivoicemic_km.json");
        Config.OverridePath = tmp;
        try {
            var cfg = new Config();
            cfg.keymap.enabled = true;
            cfg.keymap.Find("up").click.kind = "combo";
            cfg.keymap.Find("up").click.keys = "LCTRL+Z";
            cfg.keymap.Find("ok").hold = new KeyMapAction { kind = "taskview", ms = 500 };
            cfg.keymap.Find("voice").click.kind = "combo";       // payload-less -> must normalize away
            cfg.keymap.Find("voice").click.keys = "";
            cfg.Save();
            var loaded = Config.Load();
            var up = loaded.keymap.Find("up");
            var ok = loaded.keymap.Find("ok");
            var voice = loaded.keymap.Find("voice");
            Check(loaded.keymap.enabled && loaded.keymap.keys.Count == 13, "keymap roundtrip shape");
            Check(up != null && up.click.Kind == MapActionKind.Combo && up.click.keys == "LCTRL+Z", "keymap roundtrip combo");
            Check(ok != null && ok.hold != null && ok.hold.Kind == MapActionKind.TaskView && ok.hold.ms == 500, "keymap roundtrip hold");
            Check(voice != null && !voice.Mapped, "keymap empty payload dropped");
        } finally {
            Config.OverridePath = prevOverride;
            try { File.Delete(tmp); } catch { }
        }

        // gesture engine: immediate click when no hold, hold threshold, early-release click
        var mk = new KeyMapConfig();
        mk.Find("up").click.kind = "combo";
        mk.Find("up").click.keys = "LCTRL+Z";
        mk.Find("up").click.tap = false;                          // hold-through combo
        mk.Find("ok").click.kind = "combo";
        mk.Find("ok").click.keys = "LALT+TAB";
        mk.Find("ok").hold = new KeyMapAction { kind = "taskview", ms = 400 };

        var bound = new List<KeyMapEntry>();
        foreach (KeyMapEntry e in mk.keys) if (e.Mapped) bound.Add(e);
        var eng = new GestureEngine(bound);
        var outc = new List<GestureEngine.OutAction>();

        eng.Feed(0x26, true, 1000, outc);                         // UP down (no hold gesture)
        Check(outc.Count == 1 && outc[0].Phase == GestureEngine.Phase.Down && outc[0].Action.keys == "LCTRL+Z",
            "gesture: no-hold combo presses on down");
        outc.Clear();
        eng.Feed(0x26, false, 1100, outc);
        Check(outc.Count == 1 && outc[0].Phase == GestureEngine.Phase.Up, "gesture: combo releases on up");
        outc.Clear();

        eng.Feed(0x0D, true, 2000, outc);
        Check(outc.Count == 0, "gesture: hold candidate waits");
        eng.Tick(2300, outc);
        Check(outc.Count == 0, "gesture: hold not fired before threshold");
        eng.Tick(2500, outc);
        Check(outc.Count == 1 && outc[0].Action.Kind == MapActionKind.TaskView, "gesture: hold fires at threshold");
        outc.Clear();
        eng.Feed(0x0D, false, 3000, outc);
        Check(outc.Count == 0, "gesture: hold release no repeat click");
        outc.Clear();

        eng.Feed(0x0D, true, 5000, outc);
        outc.Clear();
        eng.Feed(0x0D, false, 5200, outc);                        // released early -> click (tap combo = single fire)
        Check(outc.Count == 1 && outc[0].Phase == GestureEngine.Phase.Once && outc[0].Action.keys == "LALT+TAB",
            "gesture: early release taps click combo");
    }

    static void TestChargeState() {
        // 00 61 00: RC003 real-device sample (reference/remote-mic-app bug doc 2026-08-08)
        Check(BleVoiceLink.ParseChargeState(new byte[] { 0x00, 0x61, 0x00 }) == BleVoiceLink.CHG_DISCHARGING,
            "2BED 00 61 00 -> 未充电");
        Check(BleVoiceLink.ParseChargeState(new byte[] { 0x00, 0x21, 0x00 }) == BleVoiceLink.CHG_CHARGING,
            "2BED 00 21 00 -> 充电中");
        Check(BleVoiceLink.ParseChargeState(new byte[] { 0x00, 0x01, 0x00 }) == BleVoiceLink.CHG_UNKNOWN,
            "2BED charge-state=unknown -> 未知");
        Check(BleVoiceLink.ParseChargeState(new byte[] { 0x00, 0x61 }) == BleVoiceLink.CHG_UNKNOWN,
            "2BED short frame rejected");
        Check(BleVoiceLink.ParseChargeState(null) == BleVoiceLink.CHG_UNKNOWN,
            "2BED null rejected");
        Check(BleVoiceLink.ChargeText(BleVoiceLink.CHG_CHARGING) == "充电中" &&
              BleVoiceLink.ChargeText(BleVoiceLink.CHG_DISCHARGING) == "未充电" &&
              BleVoiceLink.ChargeText(BleVoiceLink.CHG_UNKNOWN) == "未知",
            "charge text mapping");
    }

    static void TestLooksFromRemote() {
        // mapped-key device decision: latest speaker wins; no evidence -> the
        // DOWN is deferred (swallowed) and the UP's own WM_INPUT will decide
        RawSink.SeedEvidence(-1, -1);
        Check(!RawSink.LooksFromRemote(), "无证据（安静期）-> 延迟归因");
        RawSink.SeedEvidence(300, -1);
        Check(RawSink.LooksFromRemote(), "遥控器 300ms 前按过 -> 遥控器");
        RawSink.SeedEvidence(-1, 300);
        Check(!RawSink.LooksFromRemote(), "只有物理键盘 300ms 前按过 -> 物理键");
        RawSink.SeedEvidence(500, 100);
        Check(!RawSink.LooksFromRemote(), "物理键盘(100ms)比遥控器(500ms)新 -> 物理键");
        RawSink.SeedEvidence(100, 500);
        Check(RawSink.LooksFromRemote(), "遥控器(100ms)比物理键盘(500ms)新 -> 遥控器");
        RawSink.SeedEvidence(5000, 5000);
        Check(!RawSink.LooksFromRemote(), "双方证据都过期 -> 延迟归因");
        RawSink.SeedEvidence(-1, 5000);
        Check(!RawSink.LooksFromRemote(), "物理键盘证据过期、无遥控器证据 -> 延迟归因");
        RawSink.SeedEvidence(5000, -1);
        Check(!RawSink.LooksFromRemote(), "遥控器证据过期、无物理键盘证据 -> 延迟归因（首按不透传）");
        RawSink.SeedEvidence(2100, 2500);
        Check(!RawSink.LooksFromRemote(), "双方证据都过期(2100/2500ms) -> 延迟归因");
        RawSink.SeedEvidence(2100, 100);
        Check(!RawSink.LooksFromRemote(), "遥控器证据过期但物理键盘新鲜 -> 物理键");
        // latch: a swallowed remote key refreshes its own clock (swallowed keys
        // produce no WM_INPUT), keeping a burst remapped without gaps
        RawSink.SeedEvidence(-1, -1);
        RawSink.NoteRemoteActivity();
        Check(RawSink.LooksFromRemote(), "吞键闩锁续期 -> 遥控器");
        RawSink.SeedEvidence(5000, 5000);
        RawSink.NoteRemoteActivity();
        Check(RawSink.LooksFromRemote(), "证据过期后吞键闩锁 -> 遥控器（连发不断链）");
        RawSink.NoteRemoteActivity();
        RawSink.SeedEvidence(150, 100); // 闩锁发生在150ms前、之后物理键盘100ms前按过
        Check(!RawSink.LooksFromRemote(), "闩锁后物理键盘更新 -> 物理键（打字随时夺回）");
        RawSink.SeedEvidence(-1, -1);   // leave the clean default for other tests
    }

    static void TestDeferredAttribution() {
        // ring lookup: the passed orphan UP's own WM_INPUT is per-event truth
        long now = RawSink.TicksNow();
        RawSink.SeedRing(0x25, false, true, 100);
        Check(RawSink.FindRecent(0x25, false, now - 300, now + 1000) == 1, "ring: 命中遥控器 up");
        Check(RawSink.FindRecent(0x26, false, now - 300, now + 1000) == -1, "ring: vk 不符未命中");
        Check(RawSink.FindRecent(0x25, true, now - 300, now + 1000) == -1, "ring: down/up 类型不符未命中");
        RawSink.SeedRing(0x25, false, false, 50);
        Check(RawSink.FindRecent(0x25, false, now - 300, now + 1000) == 0, "ring: 更新的物理键盘 up 优先");

        // hook deferred path: ambiguous mapped DOWN is swallowed (no ghost) and
        // its UP passes so the sink can attribute it; confident remote presses
        // still swallow down+up and feed the gesture engine
        InputRouter.SetBlockF5(false);
        InputRouter.SetLinked(true);
        RawSink.SeedEvidence(-1, -1);
        var map = new KeyMapConfig();
        map.enabled = true;
        map.Find("left").click.kind = "combo";
        map.Find("left").click.keys = "BACK";
        InputRouter.SetKeyMap(map, null);
        IntPtr defDown = InputRouter.TestDispatch(0x25, true);
        IntPtr defUp = InputRouter.TestDispatch(0x25, false);
        Check(defDown == (IntPtr)1, "延迟归因：无证据 DOWN 吞下（不透传防 ghost）");
        Check(defUp != (IntPtr)1, "延迟归因：UP 放行（孤儿抬起，供 WM_INPUT 归因）");
        RawSink.SeedEvidence(100, -1);
        IntPtr remDown = InputRouter.TestDispatch(0x25, true);
        IntPtr remUp = InputRouter.TestDispatch(0x25, false);
        Check(remDown == (IntPtr)1 && remUp == (IntPtr)1, "证据明确遥控器：down/up 都吞下并映射");
        InputRouter.SetKeyMap(null);
        RawSink.SeedEvidence(-1, -1);
        InputRouter.SetBlockF5(true);
    }
}

// SelfTest.cs - offline logic tests (run with: MiVoiceMic.exe --selftest)
// 中文：离线单元测试 —— 解码/配置/手势引擎/钩子判定/充电状态解析/按键归因（45 项）
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

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
        TestKeyMap();
        TestChargeState();
        TestLooksFromRemote();
        Console.WriteLine("== " + passed + " passed, " + failed + " failed ==");
        return failed == 0 ? 0 : 1;
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
        // mapped-key device decision: latest speaker wins, no evidence -> remote
        RawSink.SeedEvidence(-1, -1);
        Check(RawSink.LooksFromRemote(), "无证据（连接后第一按）-> 遥控器");
        RawSink.SeedEvidence(300, -1);
        Check(RawSink.LooksFromRemote(), "遥控器 300ms 前按过 -> 遥控器");
        RawSink.SeedEvidence(-1, 300);
        Check(!RawSink.LooksFromRemote(), "只有物理键盘 300ms 前按过 -> 物理键");
        RawSink.SeedEvidence(500, 100);
        Check(!RawSink.LooksFromRemote(), "物理键盘(100ms)比遥控器(500ms)新 -> 物理键");
        RawSink.SeedEvidence(100, 500);
        Check(RawSink.LooksFromRemote(), "遥控器(100ms)比物理键盘(500ms)新 -> 遥控器");
        RawSink.SeedEvidence(5000, 5000);
        Check(RawSink.LooksFromRemote(), "双方证据都过期 -> 遥控器（映射已启用）");
        RawSink.SeedEvidence(-1, 5000);
        Check(RawSink.LooksFromRemote(), "物理键盘证据过期 -> 遥控器");
        RawSink.SeedEvidence(2100, 2500);
        Check(RawSink.LooksFromRemote(), "双方证据都过期(2100/2500ms) -> 遥控器（默认）");
        RawSink.SeedEvidence(2100, 100);
        Check(!RawSink.LooksFromRemote(), "遥控器证据过期但物理键盘新鲜 -> 物理键");
        RawSink.SeedEvidence(-1, -1);   // leave the clean default for other tests
    }
}

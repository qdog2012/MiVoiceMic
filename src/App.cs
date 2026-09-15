// App.cs - orchestrator: BLE voice events -> ADPCM decode -> cable push,
// default-mic switch + IME hotkey injection on a dedicated worker thread.
// 中文：主编排器 —— 串联 BLE 语音事件、解码、声卡推流、默认麦克风切换、热键注入与按键映射路由
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

sealed class App : BleVoiceLink.IHandler {
    readonly Config cfg;
    readonly BleVoiceLink link;
    readonly AdpcmDecoder decoder;
    AudioOut audioOut = new AudioOut();
    DeviceSwitcher switcher = new DeviceSwitcher();
    HotkeyInjector injector;

    // key/voice action worker (never inject SendInput from hook or WinRT threads)
    enum KeyActionKind { VoiceDown, VoiceUp, Test, AudioDevices }
    sealed class KeyAction {
        public KeyActionKind Kind;
        public long Generation;
        public IVoiceHotkey Injector;
        public bool SwitchMic;
        public int LeadMs;
        public Action<string> TestProgress;
        public AudioDeviceInfo Render, Capture;
        public System.Threading.Tasks.TaskCompletionSource<string> Completion;
    }
    readonly BlockingCollection<KeyAction> keyQueue = new BlockingCollection<KeyAction>();
    Thread keyWorker;

    // decode worker (serializes BLE audio notifications)
    sealed class AudioFrame { public byte[] Data; public long Generation; }
    readonly BlockingCollection<AudioFrame> audioQueue = new BlockingCollection<AudioFrame>(200);
    Thread audioThread;

    readonly object voiceGate = new object();
    readonly VoiceSessionGuard session = new VoiceSessionGuard();
    Timer voiceWatchdog;
    volatile bool shuttingDown;
    bool injectionEnabled;
    readonly VoiceKeySession keySession;
    volatile bool talking;
    volatile bool hotkeyHeld;          // physical voice-key session (HTT, 0x03); drives mic switch + hotkey
    DateTime talkStart;
    long framesDecoded;
    List<short> dumpBuffer;
    DateTime lastAudioRetry = DateTime.MinValue;   // late-attach: VB-CABLE may be installed while we run
    bool testTonePlaying;

    public bool AudioOk { get; private set; }
    public string AudioError { get { return audioOut.LastError; } }
    public bool SwitcherOk { get { return switcher.TargetFound; } }
    public Config Config { get { return cfg; } }
    public bool IsRunning { get { return keyWorker != null && !shuttingDown; } }

    public App(Config cfg) {
        this.cfg = cfg;
        decoder = new AdpcmDecoder(cfg.agc, cfg.gainDb);
        injector = new HotkeyInjector(cfg.hotkey.keys, cfg.hotkey.mode);
        injectionEnabled = cfg.hotkeyEnabled;
        keySession = new VoiceKeySession(delegate { switcher.Restore(); });
        link = new BleVoiceLink(cfg, this);
    }

    public void Run() {
        Log.Info("== MiVoiceMic - 小米蓝牙遥控器 2 Pro -> 微信输入法语音 ==");
        Log.Info("config: " + Config.ConfigPath);

        // 1. cable render side
        AudioOk = audioOut.Start(cfg.cableRenderName, 16000, cfg.cableRenderId);
        if (!AudioOk) {
            Log.Error("[AUDIO] " + audioOut.LastError + "；请在连接与语音 → 选择音频设备中设置");
            Log.Error("[AUDIO] 遥控器声音未送入输入法；热键图标和调试录音正常不代表音频输出正常");
        } else {
            Log.Info("[AUDIO] 推流目标: " + audioOut.DeviceUsed);
        }

        // 2. cable capture side (default-mic switching)
        bool capOk = switcher.FindTarget(cfg.cableCaptureName, cfg.cableCaptureId);
        if (!capOk) Log.Warn("[AUDIO] 未找到录音设备 \"" + cfg.cableCaptureName + "\" - 说话期间将不切换默认麦克风");

        // 3. hotkey
        injector.ForceRelease(); // Recover configured modifiers left by a previous crashed process.
        Log.Info("[KEY] 语音热键: " + injector.Describe() + (cfg.hotkeyEnabled ? "" : " (注入已禁用，切麦/推流照常)"));

        // 4. input router (F5 blocker + key mapping) + workers
        InputRouter.SetBlockF5(cfg.blockF5);
        InputRouter.SetKeyMap(cfg.keymap, cfg.deviceMacPrefix);
        InputRouter.Start();
        keyWorker = new Thread(KeyWorkerLoop) { IsBackground = true, Name = "keyworker" };
        keyWorker.Start();
        audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "decode" };
        audioThread.Start();
        voiceWatchdog = new Timer(CheckVoiceTimeout, null, 250, 250);

        // 5. BLE
        link.Start();
    }

    public void Shutdown() {
        lock (voiceGate) {
            if (shuttingDown) return;
            shuttingDown = true;
            StopVoiceLocked("程序退出");
            keyQueue.Add(new KeyAction { Kind = KeyActionKind.VoiceUp });
            keyQueue.CompleteAdding();
            audioQueue.CompleteAdding();
        }
        if (voiceWatchdog != null) voiceWatchdog.Dispose();
        try { link.Stop(); } catch { }
        if (keyWorker != null && !keyWorker.Join(3000)) Log.Warn("[KEY] 退出时等待按键释放超时");
        if (audioThread != null) audioThread.Join(1000);
        try { audioOut.Stop(); } catch { }
        InputRouter.Stop();
    }

    public void Reconnect() {
        lock (voiceGate) {
            if (shuttingDown) return;
            StopVoiceLocked("重新连接");
            session.Stop();
        }
        link.Reconnect();
    }

    public void TestHotkey(ushort[] keys, Action<string> progress) {
        lock (voiceGate) {
            if (shuttingDown) return;
            StopVoiceLocked("测试热键");
            session.Stop();
            var names = new List<string>();
            foreach (ushort key in keys) names.Add(KeyMapNames.Name(key));
            keyQueue.Add(new KeyAction {
                Kind = KeyActionKind.Test, Generation = session.Generation,
                Injector = new HotkeyInjector(names, cfg.hotkey.mode), TestProgress = progress
            });
        }
    }

    /// Runtime gain change from the settings UI.
    public void SetAudioGain(bool agcOn, double gainDb) { lock (voiceGate) decoder.SetGain(agcOn, gainDb); }

    // Serialize device changes after key-up/default-mic restoration. The UI
    // awaits completion without blocking the window or the key worker.
    public System.Threading.Tasks.Task<string> SetAudioDevices(AudioDeviceInfo render, AudioDeviceInfo capture) {
        var completion = new System.Threading.Tasks.TaskCompletionSource<string>();
        lock (voiceGate) {
            if (shuttingDown || keyWorker == null) completion.SetResult("程序未运行，无法应用音频设备。");
            else if (talking) completion.SetResult("请先松开语音键，再应用音频设备。");
            else {
                session.Stop();
                keyQueue.Add(new KeyAction { Kind = KeyActionKind.AudioDevices, Render = render, Capture = capture, Completion = completion });
            }
        }
        return completion.Task;
    }

    string ConfigureAudioDevices(AudioDeviceInfo render, AudioDeviceInfo capture) {
        lock (voiceGate) {
            if (shuttingDown || talking) return "请先结束说话，再应用音频设备。";
            if (render == null || capture == null || !render.Available || !capture.Available)
                return "请选择可用的播放端和录音端。";
            var nextOutput = new AudioOut();
            var nextSwitcher = new DeviceSwitcher();
            string oldRender = cfg.cableRenderName, oldCapture = cfg.cableCaptureName;
            string oldRenderId = cfg.cableRenderId, oldCaptureId = cfg.cableCaptureId;
            try {
                if (!nextSwitcher.FindTarget(capture.Name, capture.Id)) return "录音端已不可用，请刷新设备列表。";
                if (!nextOutput.Start(render.Name, 16000, render.Id)) return nextOutput.LastError;
                cfg.cableRenderName = render.Name; cfg.cableRenderId = render.Id ?? "";
                cfg.cableCaptureName = capture.Name; cfg.cableCaptureId = capture.Id ?? "";
                try { cfg.SaveOrThrow(); }
                catch {
                    cfg.cableRenderName = oldRender; cfg.cableCaptureName = oldCapture;
                    cfg.cableRenderId = oldRenderId; cfg.cableCaptureId = oldCaptureId;
                    throw;
                }
                var previous = audioOut;
                audioOut = nextOutput; nextOutput = null;
                switcher = nextSwitcher;
                AudioOk = true;
                previous.Stop();
                Log.Info("[AUDIO] 设备设置已保存并生效：" + render.Name + " → " + capture.Name);
                UiState.SetLevel(0);
                return null;
            } finally { if (nextOutput != null) nextOutput.Stop(); }
        }
    }

    /// Push 1 s of 440 Hz tone through the cable so the user can verify the
    /// audio path end to end (mute the real mic first if unsure).
    public void PlayTestTone() {
        AudioOut output;
        lock (voiceGate) {
            if (!AudioOk || talking || testTonePlaying || shuttingDown) { Log.Warn("[AUDIO] 测试音未播放：请先选择可用设备并结束说话"); return; }
            output = audioOut;
            testTonePlaying = true;
        }
        ThreadPool.QueueUserWorkItem(delegate {
            try {
                const int sr = 16000, seg = 240;
                for (int off = 0; off < sr; off += seg) {
                    var buf = new short[Math.Min(seg, sr - off)];
                    for (int i = 0; i < buf.Length; i++) {
                        int t = off + i;
                        double env = Math.Min(1.0, Math.Min(t / 800.0, (sr - t) / 1600.0));
                        buf[i] = (short)(11000 * env * Math.Sin(2 * Math.PI * 440 * t / sr));
                    }
                    lock (voiceGate) {
                        if (shuttingDown || talking || audioOut != output) return;
                        output.Enqueue(buf);
                    }
                    Thread.Sleep(15);
                }
                Log.Info("[AUDIO] 已发送 1 秒测试音到 " + output.DeviceUsed);
            } finally { lock (voiceGate) testTonePlaying = false; }
        });
    }

    /// Re-apply config changes that can take effect at runtime.
    public void ApplyConfig(Config updated) {
        var next = new HotkeyInjector(updated.hotkey.keys, updated.hotkey.mode);
        lock (voiceGate) {
            if (shuttingDown) return;
            if (next.Describe() != injector.Describe() || injectionEnabled != updated.hotkeyEnabled) {
                StopVoiceLocked("语音热键设置已改变");
                session.Stop();
            }
            injector = next;
            injectionEnabled = updated.hotkeyEnabled;
        }
        InputRouter.SetBlockF5(updated.blockF5);
        InputRouter.SetKeyMap(updated.keymap, updated.deviceMacPrefix);
        Log.Info("[CFG] 热键: " + injector.Describe() + " | 拦截语音键 F5/F20: " + (updated.blockF5 ? "开" : "关") +
                 " | 按键映射: " + (updated.keymap.enabled ? "开" : "关"));
    }

    void KeyWorkerLoop() {
        foreach (var act in keyQueue.GetConsumingEnumerable()) {
            try {
                if (act.Kind == KeyActionKind.VoiceDown) {
                    Action switchMic = null;
                    if (act.SwitchMic) switchMic = delegate {
                        if (switcher.SwitchToTarget() && act.LeadMs > 0) Thread.Sleep(act.LeadMs);
                    };
                    keySession.Begin(act.Injector, switchMic, delegate {
                        lock (voiceGate) return !shuttingDown && session.IsCurrent(act.Generation);
                    }, voiceGate);
                } else {
                    keySession.End();
                    if (act.Kind == KeyActionKind.Test) RunHotkeyTest(act);
                    if (act.Kind == KeyActionKind.AudioDevices)
                        act.Completion.TrySetResult(ConfigureAudioDevices(act.Render, act.Capture));
                }
            } catch (Exception ex) {
                Log.Error("[KEY] worker: " + ex.Message);
                if (act.Completion != null) act.Completion.TrySetResult("应用失败：" + ex.Message);
            }
        }
        try { keySession.End(); } catch (Exception ex) { Log.Error("[KEY] cleanup: " + ex.Message); }
    }

    bool TestIsCurrent(KeyAction act) {
        lock (voiceGate) return !shuttingDown && session.Generation == act.Generation;
    }

    bool WaitForTest(KeyAction act, int milliseconds) {
        long until = VoiceSessionGuard.NowMs + milliseconds;
        while (VoiceSessionGuard.NowMs < until) {
            if (!TestIsCurrent(act)) return false;
            Thread.Sleep(20);
        }
        return TestIsCurrent(act);
    }

    void RunHotkeyTest(KeyAction act) {
        try {
            for (int count = 3; count > 0; count--) {
                if (act.TestProgress != null) act.TestProgress(count + " 秒后测试");
                if (!WaitForTest(act, 1000)) return;
            }
            if (act.TestProgress != null) act.TestProgress("测试中…");
            keySession.Begin(act.Injector, null, delegate { return TestIsCurrent(act); }, voiceGate);
            WaitForTest(act, 2000);
        } finally {
            try { keySession.End(); }
            finally { if (act.TestProgress != null) act.TestProgress("测试热键"); }
        }
    }

    void AudioLoop() {
        foreach (AudioFrame frame in audioQueue.GetConsumingEnumerable()) {
            try {
              lock (voiceGate) {
                if (!session.IsCurrent(frame.Generation)) continue;
                var blocks = decoder.Feed(frame.Data);
                if (blocks != null) {
                    for (int i = 0; i < blocks.Count; i++) {
                        audioOut.Enqueue(blocks[i]);
                        framesDecoded++;
                        if (dumpBuffer != null) dumpBuffer.AddRange(blocks[i]);
                        UpdateLevel(blocks[i]);
                    }
                }
              }
            } catch (Exception ex) { Log.Error("[DECODE] " + ex.Message); }
        }
    }

    static void UpdateLevel(short[] block) {
        if (block == null || block.Length == 0) return;
        int peak = 0;
        for (int i = 0; i < block.Length; i++) {
            int v = block[i] < 0 ? -block[i] : block[i];
            if (v > peak) peak = v;
        }
        UiState.SetLevel(peak / 32768.0);
    }

    // ======================= BleVoiceLink.IHandler =======================

    public void OnLinkState(bool connected, string detail) {
        if (connected) InputRouter.RefreshVoiceDriver();
        InputRouter.SetLinked(connected);
        if (connected) {
            Log.Info("[LINK] 已连接: " + detail);
            TryLateAttachAudio("link");      // VB-CABLE / audio stack may have come up since startup
        } else {
            lock (voiceGate) { if (!shuttingDown) StopVoiceLocked("蓝牙连接已结束"); }
            Log.Info("[LINK] " + detail);
        }
        UiState.SetStatus(connected, detail, link.RemoteName);
        TrayIcon.SetStatus(connected, detail);
    }

    /// Retry opening the cable if it was missing at startup (throttled to 1/30s).
    void TryLateAttachAudio(string reason) {
      lock (voiceGate) {
        if (shuttingDown) return;
        if (AudioOk && switcher.TargetFound) return;
        if ((DateTime.Now - lastAudioRetry).TotalSeconds < 30) return;
        lastAudioRetry = DateTime.Now;
        if (!AudioOk) {
            AudioOk = audioOut.Start(cfg.cableRenderName, 16000, cfg.cableRenderId);
            if (AudioOk) Log.Info("[AUDIO] late-attach OK (" + reason + "): " + audioOut.DeviceUsed);
        }
        if (!switcher.TargetFound && switcher.FindTarget(cfg.cableCaptureName, cfg.cableCaptureId))
            Log.Info("[AUDIO] late-attach capture OK (" + reason + ")");
      }
    }

    public void OnCaps(int version, int frameSize, int codec) {
        lock (voiceGate) decoder.FrameSize = frameSize;
    }

    public void OnVoiceStart(byte sessionId, byte interaction) {
      lock (voiceGate) {
        if (shuttingDown || interaction != 0x03 || session.IsDuplicate(sessionId)) return;
        StopVoiceLocked("新语音会话替换旧会话");
        TryLateAttachAudio("voice");         // first real use: make sure the cable is there
        session.Start(sessionId, VoiceSessionGuard.NowMs);
        talking = true;
        framesDecoded = 0;
        talkStart = DateTime.Now;
        decoder.ResetSession();
        AudioFrame stale;
        while (audioQueue.TryTake(out stale)) { }         // drain stale audio
        if (cfg.dumpAudio) dumpBuffer = new List<short>(16000 * 2);
        // 0x03 = HTT (voice key physically held); other reasons are firmware-initiated
        // sessions (e.g. right after MIC_OPEN) and must NOT hold the IME hotkey.
        hotkeyHeld = interaction == 0x03;
        // firmware-initiated sessions carry no user audio and no matching AUDIO_STOP,
        // so only a real key press may light up the talking UI (else it sticks on)
        UiState.SetTalking(hotkeyHeld);
        Log.Voice(">>> 语音会话开始 (session " + sessionId + ", interaction " + interaction +
                  (hotkeyHeld ? ", 语音键按下" : ", 固件自启(不注入热键)") + ")");
        if (hotkeyHeld)
            keyQueue.Add(new KeyAction {
                Kind = KeyActionKind.VoiceDown, Generation = session.Generation,
                Injector = injectionEnabled ? injector : null,
                SwitchMic = cfg.switchDefaultMic && AudioOk && switcher.TargetFound,
                LeadMs = Math.Max(0, Math.Min(1000, cfg.switchLeadMs))
            });
      }
    }

    public void OnVoiceStop() {
        lock (voiceGate) { if (!shuttingDown) StopVoiceLocked(null); }
    }

    void CheckVoiceTimeout(object state) {
        string reason;
        try {
            lock (voiceGate) {
                if (shuttingDown) return;
                reason = session.TimeoutReason(VoiceSessionGuard.NowMs);
                if (reason == null) return;
                Log.Warn("[VOICE] " + reason + "，释放热键并自动重连");
                StopVoiceLocked(reason);
            }
            link.Reconnect();
        } catch (Exception ex) { Log.Warn("[VOICE] 自动恢复: " + ex.Message); }
    }

    // Caller owns voiceGate. Release keys before any config or WAV file I/O.
    void StopVoiceLocked(string reason) {
        if (!talking) return;
        talking = false;
        session.Stop();
        if (hotkeyHeld) keyQueue.Add(new KeyAction { Kind = KeyActionKind.VoiceUp });
        hotkeyHeld = false;
        double secs = (DateTime.Now - talkStart).TotalSeconds;
        Log.Voice("<<< 松开语音键 (" + framesDecoded + " 帧, " + secs.ToString("0.0") + "s)" + (reason == null ? "" : " — " + reason));
        UiState.SetTalking(false);
        UiState.SetLevel(0);
        if (framesDecoded > 0) {             // sessions with no decoded audio (firmware self-start) don't count
            lock (cfg) {
                cfg.stats.AddSession(secs);
                cfg.Save();                  // persist usage counters after each session
            }
        }
        if (dumpBuffer != null && dumpBuffer.Count > 0) {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "dump_" + DateTime.Now.ToString("HHmmss") + ".wav");
            try { WavWriter.Write(path, dumpBuffer, 16000); Log.Info("[DUMP] " + path); }
            catch (Exception ex) { Log.Warn("[DUMP] " + ex.Message); }
        }
        dumpBuffer = null;
    }

    public void OnSync(int predictor, int stepIndex) {
        lock (voiceGate) decoder.ApplySync(predictor, stepIndex);
    }

    public void OnAudioFrame(byte[] frame) {
        lock (voiceGate) {
            if (shuttingDown || !talking || frame == null || frame.Length == 0) return;
            session.Audio(VoiceSessionGuard.NowMs);
            audioQueue.TryAdd(new AudioFrame { Data = frame, Generation = session.Generation });
        }
    }

    public void OnBattery(int percent) {
        Log.Info("[BAT] 遥控器电量: " + percent + "%");
        UiState.SetBattery(percent);
        TrayIcon.SetBattery(percent);
    }

    public void OnCharging(int chargeState) {
        UiState.SetCharging(chargeState);
        TrayIcon.SetCharging(chargeState);
    }
}

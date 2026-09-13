// BleVoiceLink.cs - WinRT BLE GATT client for the Xiaomi remote's ATVV voice
// service: discovery (paired device or advertisement scan + optional auto-pair),
// connection supervision, handshake, voice session events and keepalive.
//
// ATVV channels (cross-checked against QL-4/RemoteMapper NOTES.md and
// HD838A/remote-mic-app ATVVProtocol.swift):
//   service ab5e0001 / cmd-write ab5e0002 / audio-notify ab5e0003 / ctl-notify ab5e0004
//
// .NET Framework 4.8 csc WinRT projection notes (empirically probed on Win11 26200):
//  - WinRT events are not +=-subscribable (CS1545): call add_XXX()/remove_XXX() directly.
//  - BluetoothLEAdvertisementScanningMode / DevicePairingStatus enums are not
//    projected: use reflection (Enum.ToObject / property read).
// 中文：WinRT BLE GATT 客户端 —— 遥控器发现/自动配对/连接保活/ATVV 语音会话
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

sealed class RemoteDeviceStatus {
    public string Name;
    public bool? Connected;
}

sealed class RemoteConnectionReport {
    public readonly List<RemoteDeviceStatus> Devices = new List<RemoteDeviceStatus>();
    public string Error;
}

sealed class BleVoiceLink {
    static readonly Guid SVC   = new Guid("ab5e0001-5a21-4f05-bc7d-af01f617b664");
    static readonly Guid C_CMD = new Guid("ab5e0002-5a21-4f05-bc7d-af01f617b664");
    static readonly Guid C_AUD = new Guid("ab5e0003-5a21-4f05-bc7d-af01f617b664");
    static readonly Guid C_CTL = new Guid("ab5e0004-5a21-4f05-bc7d-af01f617b664");
    static readonly Guid SVC_BATTERY = new Guid("0000180f-0000-1000-8000-00805f9b34fb");
    static readonly Guid CHR_BATTERY = new Guid("00002a19-0000-1000-8000-00805f9b34fb");
    static readonly Guid CHR_BATT_STATUS = new Guid("00002bed-0000-1000-8000-00805f9b34fb");

    // 2BED Battery Level Status (BAS v1.1) -> charge state for the UI.
    // Exposed by the RC003 (2 Pro) only; RC001-style remotes report CHG_UNKNOWN.
    public const int CHG_UNKNOWN = -1;
    public const int CHG_DISCHARGING = 0;
    public const int CHG_CHARGING = 1;

    // ATVV opcodes
    const byte OP_AUDIO_STOP = 0x00;    // ctl: audio stop (any reason)
    const byte OP_CAPS_RESP  = 0x0B;
    const byte OP_AUDIO_SYNC = 0x0A;    // on ctl channel: decoder sync
    const byte OP_AUDIO_START = 0x04;   // ctl: audio start (b1=interaction, b2=codec, b3=session)
    const byte OP_MIC_OPEN_REQ = 0x08;  // ctl: remote asks host to (re)open mic
    const byte OP_MIC_CLOSED = 0x0D;

    public interface IHandler {
        void OnLinkState(bool connected, string detail);
        void OnCaps(int version, int frameSize, int codec);
        void OnVoiceStart(byte sessionId, byte interaction);
        void OnVoiceStop();
        void OnSync(int predictor, int stepIndex);
        void OnAudioFrame(byte[] frame);
        void OnBattery(int percent);
        void OnCharging(int chargeState);
    }

    readonly Config cfg;
    readonly IHandler handler;
    CancellationTokenSource cts;
    readonly object restartGate = new object();
    readonly SemaphoreSlim lifecycleGate = new SemaphoreSlim(1, 1);
    volatile bool stopped;
    CancellationToken connectionToken;

    BluetoothLEDevice device;
    GattDeviceService svcAtvv, svcBattery;
    GattCharacteristic chCmd, chAud, chCtl, chCharge;
    GattSession gattSession;
    readonly SemaphoreSlim writeGate = new SemaphoreSlim(1, 1);

    volatile bool linked;
    volatile bool micOpen;
    volatile bool chargeHooked;
    int version;             // ATVV protocol version from CAPS
    int frameSize = 120;
    int codec = 2;           // 2 = 16 kHz
    byte sessionId;
    Timer keepalive;
    int keepTicks;

    public bool Linked { get { return linked; } }
    public string RemoteName { get; private set; }
    public int Battery { get; private set; }
    public int Charging { get; private set; }

    public BleVoiceLink(Config cfg, IHandler handler) {
        this.cfg = cfg;
        this.handler = handler;
        Charging = CHG_UNKNOWN;
    }

    public void Start() {
        QueueConnection(null);
    }

    public void Stop() {
        lock (restartGate) {
            stopped = true;
            if (cts != null) cts.Cancel();
            if (keepalive != null) { keepalive.Dispose(); keepalive = null; }
        }
        handler.OnVoiceStop();
        Task.Run(async delegate {
            await lifecycleGate.WaitAsync();
            try { Cleanup(); }
            catch (Exception ex) { Log.Warn("[BLE] stop: " + ex.Message); }
            finally { lifecycleGate.Release(); }
        });
    }

    /// Manual reconnect from the tray.
    public void Reconnect() { QueueConnection(null); }

    void QueueConnection(CancellationToken? expected) {
        lock (restartGate) {
            if (stopped || (expected.HasValue && (cts == null || cts.Token != expected.Value))) return;
            if (cts != null) cts.Cancel();
            if (keepalive != null) { keepalive.Dispose(); keepalive = null; }
            var source = new CancellationTokenSource();
            cts = source;
            CancellationToken token = source.Token;
            linked = false;
            InputRouter.SetLinked(false);
            handler.OnVoiceStop();
            handler.OnLinkState(false, "正在重新连接遥控器...");
            Task.Run(async delegate {
                bool entered = false;
                try {
                    await lifecycleGate.WaitAsync(token);
                    entered = true;
                    token.ThrowIfCancellationRequested();
                    Cleanup();
                    await LinkLoop(token);
                } catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warn("[BLE] connection worker: " + ex.Message); }
                finally { if (entered) lifecycleGate.Release(); }
            });
        }
    }

    // ======================= link loop =======================

    async Task LinkLoop(CancellationToken token) {
        int failures = 0;
        while (!token.IsCancellationRequested) {
            bool failed = false;
            string error = null;
            try {
                Log.Info("[BLE] 正在查找已配对遥控器");
                var di = await FindPairedRemote(token);
                if (di == null && cfg.autoPair) di = await TryFindAndPairAdvertising(token);
                if (di == null) {
                    handler.OnLinkState(false, "未找到遥控器（先完成蓝牙配对；或按住遥控器任意键唤醒）");
                    await Delay(3.0, token);
                    continue;
                }
                Log.Info("[BLE] connecting: " + di.Name + " (" + di.Id + ")");
                handler.OnLinkState(false, "正在连接 " + di.Name + " ...");

                var dev = await AsT(BluetoothLEDevice.FromIdAsync(di.Id), token);
                if (dev == null) throw new Exception("FromIdAsync returned null");
                device = dev;
                RemoteName = string.IsNullOrEmpty(dev.Name) ? di.Name : dev.Name;

                dev.ConnectionStatusChanged += OnConnectionChanged;   // works with System.Runtime.InteropServices.WindowsRuntime referenced
                await TryMaintainConnection(dev, token);

                await SetupGatt(token);
                await Handshake(token);
                token.ThrowIfCancellationRequested();

                linked = true;
                micOpen = true;   // MIC_OPEN sent; remote signals sessions via CTL
                handler.OnLinkState(true, RemoteName);
                failures = 0;

                keepTicks = 0;
                lock (restartGate) {
                    token.ThrowIfCancellationRequested();
                    keepalive = new Timer(KeepaliveTick, token, 5000, 5000);
                }
                await TryReadBattery(token);
                return;   // supervision continues via events
            } catch (Exception ex) {
                failed = true;
                error = ex.Message;
            }
            if (failed && !token.IsCancellationRequested) {
                failures++;
                Log.Warn("[BLE] link attempt failed: " + error);
                handler.OnLinkState(false, "连接失败: " + error);
                Cleanup();
                await Delay(Math.Min(3 + failures * 2, 15), token);
            }
        }
    }

    async void KeepaliveTick(object state) {
        CancellationToken token = (CancellationToken)state;
        if (token.IsCancellationRequested || !await lifecycleGate.WaitAsync(0)) return;
        bool reconnect = false;
        try {
            token.ThrowIfCancellationRequested();
            if (micOpen && version >= 0x0100)
                await WriteCmd(new byte[] { 0x0E, sessionId }, token);   // MIC_EXTEND
            if (++keepTicks % 12 == 0) await TryReadBattery(token);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            Log.Warn("[ATVV] keepalive failed: " + ex.Message + " -> reconnect");
            reconnect = true;
        } finally { lifecycleGate.Release(); }
        if (reconnect) QueueConnection(token);
    }

    void OnConnectionChanged(BluetoothLEDevice sender, object args) {
        if (sender == device && sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && linked) {
            Log.Warn("[BLE] disconnected unexpectedly -> reconnect");
            linked = false;
            micOpen = false;
            InputRouter.SetLinked(false);
            handler.OnVoiceStop();          // release hotkey / restore mic
            handler.OnLinkState(false, "连接断开，正在重连...");
            var source = cts;
            if (source != null) QueueConnection(source.Token);
        }
    }

    async Task TryMaintainConnection(BluetoothLEDevice dev, CancellationToken token) {
        try {
            gattSession = await AsT(GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId), token);
            if (gattSession != null) gattSession.MaintainConnection = true;
            Log.Info("[BLE] GattSession.MaintainConnection = true");
        } catch (OperationCanceledException) { throw; }
        catch (Exception ex) {
            Log.Info("[BLE] MaintainConnection unavailable: " + ex.Message);
        }
    }

    async Task<DeviceInformation> FindPairedRemote(CancellationToken token) {
        var sel = BluetoothLEDevice.GetDeviceSelector();
        var devs = await AsT(DeviceInformation.FindAllAsync(sel), token);
        if (devs == null) return null;
        foreach (var d in devs) {
            if (IsRemoteName(d.Name)) return d;
            if (!string.IsNullOrEmpty(cfg.deviceMacPrefix) &&
                d.Id.IndexOf(cfg.deviceMacPrefix, StringComparison.OrdinalIgnoreCase) >= 0) return d;
        }
        return null;
    }

    bool IsRemoteName(string name) {
        if (string.IsNullOrEmpty(name)) return false;
        string trimmed = name.Trim();
        foreach (string want in cfg.deviceNames)
            if (trimmed.Equals(want.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// Watch BLE advertisements for an unpaired remote; try PairAsync() once.
    async Task<DeviceInformation> TryFindAndPairAdvertising(CancellationToken token) {
        var tcs = new TaskCompletionSource<DeviceInformation>();
        BluetoothLEAdvertisementWatcher watcher = null;
        Timer abort = null;
        ulong found = 0;
        var received = new TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs>(
            delegate (BluetoothLEAdvertisementWatcher s, BluetoothLEAdvertisementReceivedEventArgs e) {
                try {
                    if (found != 0) return;
                    string ln = e.Advertisement == null ? null : e.Advertisement.LocalName;
                    if (IsRemoteName(ln)) {
                        found = e.BluetoothAddress;
                        ThreadPool.QueueUserWorkItem(delegate {
                            try {
                                Log.Info("[BLE] advertisement from remote @ " + BtAddrStr(found) + ", attempting pair...");
                                var dev = AsT(BluetoothLEDevice.FromBluetoothAddressAsync(found)).GetAwaiter().GetResult();
                                if (dev == null) { tcs.TrySetResult(null); return; }
                                tcs.TrySetResult(PairDevice(dev) ? dev.DeviceInformation : null);
                            } catch (Exception ex) {
                                Log.Warn("[BLE] auto-pair error: " + ex.Message);
                                tcs.TrySetResult(null);
                            }
                        });
                    }
                } catch { }
            });
        try {
            watcher = new BluetoothLEAdvertisementWatcher();
            TrySetActiveScanning(watcher);
            watcher.Received += received;
            abort = new Timer(delegate { tcs.TrySetResult(null); }, null, 20000, Timeout.Infinite);
            watcher.Start();
            Log.Info("[BLE] 未找到已配对遥控器，扫描广播 20s (自动配对 " + (cfg.autoPair ? "开" : "关") + ")；" +
                     "如遥控器未配对: 同时长按 [主页+菜单] 进入配对模式");
            return await tcs.Task;
        } finally {
            try { if (abort != null) abort.Dispose(); } catch { }
            try { if (watcher != null) watcher.Stop(); } catch { }
        }
    }

    void TrySetActiveScanning(BluetoothLEAdvertisementWatcher watcher) {
        try {
            var prop = typeof(BluetoothLEAdvertisementWatcher).GetProperty("ScanningMode");
            if (prop != null) prop.SetValue(watcher, Enum.ToObject(prop.PropertyType, 1), null);  // 1 = Active
        } catch { }
    }

    /// Pair via reflection (DevicePairingStatus enum is not compile-projected).
    bool PairDevice(BluetoothLEDevice dev) {
        try {
            object pairing = typeof(DeviceInformation).GetProperty("Pairing").GetValue(dev.DeviceInformation, null);
            object isPaired = pairing.GetType().GetProperty("IsPaired").GetValue(pairing, null);
            if (isPaired is bool && (bool)isPaired) { Log.Info("[BLE] already paired"); return true; }
            var pairAsync = pairing.GetType().GetMethod("PairAsync", Type.EmptyTypes);
            object op = pairAsync.Invoke(pairing, null);
            object result = RuntimeAsync.AwaitObject(op);
            if (result == null) { Log.Warn("[BLE] pair: no result"); return false; }
            object status = result.GetType().GetProperty("Status").GetValue(result, null);
            int st = Convert.ToInt32(status);
            // DevicePairingStatus: 0 AlreadyPaired, 1 Paired, ...
            Log.Info("[BLE] pair status = " + st);
            return st == 0 || st == 1;
        } catch (Exception ex) {
            Log.Warn("[BLE] pair failed: " + ex.Message + " - 请在 Windows 设置中手动配对");
            return false;
        }
    }

    static string BtAddrStr(ulong a) {
        return string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
            (a >> 40) & 0xFF, (a >> 32) & 0xFF, (a >> 24) & 0xFF, (a >> 16) & 0xFF, (a >> 8) & 0xFF, a & 0xFF);
    }

    // ======================= GATT + handshake =======================

    async Task SetupGatt(CancellationToken token) {
        GattCharacteristic cmd = null, aud = null, ctl = null;
        GattDeviceService svc = null;
        for (int attempt = 0; attempt < 5; attempt++) {
            var svcRes = await AsT(device.GetGattServicesAsync(BluetoothCacheMode.Uncached), token);
            Log.Info("[GATT] services: " + (svcRes == null ? "no result" : svcRes.Status + ", count=" + svcRes.Services.Count));
            if (svcRes != null) {
                svc = svcRes.Services.FirstOrDefault(s => s.Uuid == SVC);
                if (svc != null) {
                    var chRes = await AsT(svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached), token);
                    if (chRes != null) {
                        cmd = chRes.Characteristics.FirstOrDefault(c => c.Uuid == C_CMD);
                        aud = chRes.Characteristics.FirstOrDefault(c => c.Uuid == C_AUD);
                        ctl = chRes.Characteristics.FirstOrDefault(c => c.Uuid == C_CTL);
                        if (cmd != null && aud != null && ctl != null) break;
                    }
                }
            }
            Log.Info("[GATT] service discovery retry " + (attempt + 1) + "/5");
            await Delay(1.0, token);
        }
        if (cmd == null || aud == null || ctl == null)
            throw new Exception("遥控器语音服务不可用，请按遥控器任意键唤醒；持续失败时重新配对");

        connectionToken = token;
        svcAtvv = svc; chCmd = cmd; this.chAud = aud; this.chCtl = ctl;
        HookValueChanged(ctl, CtlHandler);
        HookValueChanged(aud, AudioHandler);
        var r1 = await AsT(ctl.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify), token);
        var r2 = await AsT(aud.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify), token);
        if (r1 != GattCommunicationStatus.Success || r2 != GattCommunicationStatus.Success)
            throw new Exception("订阅 ATVV 通知失败: ctl=" + r1 + " aud=" + r2);
    }

    async Task Handshake(CancellationToken token) {
        // GET_CAPS v1.0 (both known remotes speak this)
        var capsWait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        capsPending = capsWait;
        capsError = null;
        try {
            // Register the waiter BEFORE sending: a fast response can arrive during the write.
            await WriteCmd(new byte[] { 0x0A, 0x01, 0x00, 0x00, 0x03, 0x03 }, token);
            if (!await AsyncDeadline.Wait(capsWait.Task, token, 4000, null))
                throw new Exception(string.IsNullOrEmpty(capsError) ? "CAPS 拒绝" : capsError);
        } finally { if (capsPending == capsWait) capsPending = null; }

        // MIC_OPEN (version-aware; codec from CAPS)
        if (version >= 0x0100) await WriteCmd(new byte[] { 0x0C, 0x00 }, token);
        else await WriteCmd(new byte[] { 0x0C, 0x00, (byte)codec }, token);
    }

    TaskCompletionSource<bool> capsPending;
    string capsError;

    async Task TryReadBattery(CancellationToken token) {
        try {
            if (svcBattery == null) {
                var svcRes = await AsT(device.GetGattServicesAsync(BluetoothCacheMode.Cached), token);
                svcBattery = svcRes.Services.FirstOrDefault(s => s.Uuid == SVC_BATTERY);
            }
            var bsvc = svcBattery;
            if (bsvc == null) return;
            var chRes = await AsT(bsvc.GetCharacteristicsAsync(BluetoothCacheMode.Cached), token);
            var bc = chRes.Characteristics.FirstOrDefault(c => c.Uuid == CHR_BATTERY);
            if (bc != null) {
                var read = await AsT(bc.ReadValueAsync(), token);
                if (read.Status == GattCommunicationStatus.Success && read.Value.Length >= 1) {
                    var b = ToBytes(read.Value);
                    Battery = b[0];
                    handler.OnBattery(Battery);
                }
            }
            var cc = chRes.Characteristics.FirstOrDefault(c => c.Uuid == CHR_BATT_STATUS);
            if (cc == null) return;               // RC001-style: no charging info
            var cs = await AsT(cc.ReadValueAsync(), token);
            if (cs.Status == GattCommunicationStatus.Success) {
                var b = ToBytes(cs.Value);
                int st = ParseChargeState(b);
                Charging = st;
                Log.Info("[BAT] 充电状态: " + ChargeText(st) + " (2BED=" + HexStr(b) + ")");
                handler.OnCharging(st);
            }
            if (!chargeHooked) {                  // subscribe after the first read so a
                chargeHooked = true;              // slow/hung CCCD write can't delay it
                try {
                    if ((cc.CharacteristicProperties & GattCharacteristicProperties.Notify) != 0) {
                        chCharge = cc;
                        HookValueChanged(cc, ChargeHandler);
                        var sub = await AsT(cc.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.Notify), token);
                        Log.Info("[BAT] 2BED notify subscribed: " + sub);
                    } else {
                        Log.Info("[BAT] 2BED has no notify property - polling every 60s");
                    }
                } catch (Exception ex) { Log.Warn("[BAT] 2BED subscribe: " + ex.Message); }
            }
        } catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn("[BAT] read: " + ex.Message); }
    }

    void ChargeHandler(GattCharacteristic sender, GattValueChangedEventArgs e) {
        if (sender != chCharge || stopped || connectionToken.IsCancellationRequested) return;
        try {
            byte[] b = ToBytes(e.CharacteristicValue);
            Log.Info("[BAT] 2BED notify: " + HexStr(b) + " -> " + ChargeText(ParseChargeState(b)));
            Charging = ParseChargeState(b);
            handler.OnCharging(Charging);
        } catch { }
    }

    // BAS v1.1 Power State (little-endian): bit0 battery present, bits1-2 wired
    // ext power, bits3-4 wireless ext power, bits5-6 charge state (1=charging,
    // 2/3=discharging). Verified against a real RC003: 00 61 00 -> discharging.
    internal static int ParseChargeState(byte[] b) {
        if (b == null || b.Length < 3) return CHG_UNKNOWN;
        int ps = b[1] | (b[2] << 8);
        int charge = (ps >> 5) & 3;
        return charge == 1 ? CHG_CHARGING : (charge >= 2 ? CHG_DISCHARGING : CHG_UNKNOWN);
    }

    internal static string ChargeText(int st) {
        return st == CHG_CHARGING ? "充电中" : st == CHG_DISCHARGING ? "未充电" : "未知";
    }

    static string HexStr(byte[] b) {
        var sb = new StringBuilder(b.Length * 3);
        for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    // ======================= notification handlers =======================

    void CtlHandler(GattCharacteristic sender, GattValueChangedEventArgs e) {
        if (sender != chCtl || stopped || connectionToken.IsCancellationRequested) return;
        byte[] b = ToBytes(e.CharacteristicValue);
        if (b.Length < 1) return;
        byte op = b[0];
        try {
            if (op == OP_AUDIO_START) {                      // 0x04
                byte interaction = b.Length >= 2 ? b[1] : (byte)0;
                if (b.Length >= 3) {
                    int startCodec = b[2];
                    if (startCodec == 2) codec = 2;
                    else if (startCodec == 1) { codec = 1; Log.Warn("[ATVV] remote selected 8 kHz codec (本应用按 16 kHz 解码)"); }
                }
                if (b.Length >= 4) sessionId = b[3];
                Log.Voice("[ATVV] AUDIO_START session=" + sessionId + " interaction=" + interaction + " codec=" + codec);
                handler.OnVoiceStart(sessionId, interaction);
            } else if (op == OP_AUDIO_STOP) {                // 0x00 (any reason)
                Log.Voice("[ATVV] AUDIO_STOP reason=" + (b.Length >= 2 ? b[1].ToString() : "?"));
                handler.OnVoiceStop();
            } else if (op == OP_CAPS_RESP && b.Length >= 7) {  // 0x0B
                version = (b[1] << 8) | b[2];
                int codecsByte, interaction;
                if (version >= 0x0100) {
                    codecsByte = b[3]; interaction = b[4];
                    if (codecsByte == 0 && b.Length >= 9 && (b[4] & 0x03) != 0) { codecsByte = b[4]; interaction = 3; }
                } else {
                    codecsByte = b[4]; interaction = 0;
                }
                int fs = (b[5] << 8) | b[6];
                if (fs > 0) frameSize = fs;
                codec = (codecsByte & 0x02) != 0 ? 2 : 1;
                if (codec == 1) {
                    capsError = "遥控器仅支持 8 kHz 编码，本应用需要 16 kHz (请重新配对后重试)";
                    Log.Error("[ATVV] " + capsError);
                    var ew = capsPending; if (ew != null) ew.TrySetResult(false);
                    return;
                }
                Log.Info("[ATVV] CAPS v0x" + version.ToString("X4") + " codec=" + codec + " frame=" + frameSize + " interaction=" + interaction);
                handler.OnCaps(version, frameSize, codec);
                var w = capsPending; if (w != null) w.TrySetResult(true);
            } else if (op == OP_AUDIO_SYNC && b.Length >= 7) {  // 0x0A on ctl
                int pred = (b[4] << 8) | b[5];
                if (pred >= 32768) pred -= 65536;
                handler.OnSync(pred, b[6]);
            } else if (op == OP_MIC_OPEN_REQ) {              // 0x08: remote asks host to open mic
                Log.Info("[ATVV] remote MIC_OPEN request");
                if (version >= 0x0100) WriteCmdSafe(new byte[] { 0x0C, 0x00 });
                else WriteCmdSafe(new byte[] { 0x0C, 0x00, (byte)codec });
            } else if (op == OP_MIC_CLOSED && b.Length >= 2 && b[1] == 0x00) {
                Log.Info("[ATVV] MIC_CLOSED by remote");
                micOpen = false;
                handler.OnVoiceStop();
            }
        } catch (Exception ex) {
            Log.Error("[ATVV] ctl handler: " + ex.Message);
        }
    }

    void AudioHandler(GattCharacteristic sender, GattValueChangedEventArgs e) {
        if (sender != chAud || stopped || connectionToken.IsCancellationRequested) return;
        try {
            byte[] b = ToBytes(e.CharacteristicValue);
            if (b.Length > 0) handler.OnAudioFrame(b);
        } catch (Exception ex) {
            Log.Error("[ATVV] audio handler: " + ex.Message);
        }
    }

    async void WriteCmdSafe(byte[] data) {
        var source = cts;
        if (source == null) return;
        bool entered = false;
        try {
            await lifecycleGate.WaitAsync(source.Token);
            entered = true;
            await WriteCmd(data, source.Token);
            micOpen = true;
        } catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("[ATVV] write: " + ex.Message); QueueConnection(source.Token); }
        finally { if (entered) lifecycleGate.Release(); }
    }

    async Task WriteCmd(byte[] data, CancellationToken token) {
        if (chCmd == null) throw new Exception("not connected");
        await writeGate.WaitAsync(token);
        try {
            token.ThrowIfCancellationRequested();
            var w = new DataWriter();
            w.WriteBytes(data);
            var res = await AsT(chCmd.WriteValueAsync(w.DetachBuffer(), GattWriteOption.WriteWithoutResponse), token);
            if (res != GattCommunicationStatus.Success) {
                var w2 = new DataWriter();
                w2.WriteBytes(data);
                res = await AsT(chCmd.WriteValueAsync(w2.DetachBuffer()), token);
                if (res != GattCommunicationStatus.Success)
                    throw new Exception("write failed: " + res);
            }
        } finally { writeGate.Release(); }
    }

    void HookValueChanged(GattCharacteristic ch, TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler) {
        var mi = ch.GetType().GetMethod("add_ValueChanged",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        mi.Invoke(ch, new object[] { handler });
    }

    void UnhookValueChanged(GattCharacteristic ch, TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler) {
        if (ch == null) return;
        try {
            var mi = ch.GetType().GetMethod("remove_ValueChanged", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            mi.Invoke(ch, new object[] { handler });
        } catch { }
    }

    void Cleanup() {
        long started = VoiceSessionGuard.NowMs;
        Log.Info("[BLE] cleanup begin");
        linked = false;
        micOpen = false;
        chargeHooked = false;
        InputRouter.SetLinked(false);
        handler.OnVoiceStop();
        capsPending = null;
        UnhookValueChanged(chCtl, CtlHandler);
        UnhookValueChanged(chAud, AudioHandler);
        UnhookValueChanged(chCharge, ChargeHandler);
        if (device != null) {
            try { device.ConnectionStatusChanged -= OnConnectionChanged; } catch { }
        }
        if (svcAtvv != null) { ReleaseGatt("ATVV service", svcAtvv.Dispose); svcAtvv = null; }
        if (svcBattery != null) { ReleaseGatt("battery service", svcBattery.Dispose); svcBattery = null; }
        chCmd = null; chAud = null; chCtl = null; chCharge = null;
        if (gattSession != null) { ReleaseGatt("GattSession", gattSession.Dispose); gattSession = null; }
        if (device != null) { ReleaseGatt("BluetoothLEDevice", device.Dispose); device = null; }
        Log.Info("[BLE] cleanup complete: " + (VoiceSessionGuard.NowMs - started) + " ms");
    }

    static void ReleaseGatt(string name, Action release) {
        long started = VoiceSessionGuard.NowMs;
        Log.Info("[BLE] releasing " + name);
        try { release(); }
        catch (Exception ex) { Log.Warn("[BLE] release " + name + ": " + ex.Message); }
        finally { Log.Info("[BLE] released " + name + ": " + (VoiceSessionGuard.NowMs - started) + " ms"); }
    }

    static Task Delay(double seconds, CancellationToken token) {
        return Task.Delay((int)(seconds * 1000), token);
    }

    // ======================= diagnostics helpers (for --check) =======================

    public static bool IsDiagnosticRemote(string name, string address, Config config) {
        if (!string.IsNullOrWhiteSpace(name)) foreach (string want in config.deviceNames)
            if (string.Equals(name.Trim(), want.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = (config.deviceMacPrefix ?? "").Replace(":", "").Replace("-", "");
        string mac = (address ?? "").Replace(":", "").Replace("-", "");
        return prefix.Length > 0 && mac.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static RemoteConnectionReport ReadRemoteConnection(Config config) {
        var report = new RemoteConnectionReport();
        try {
            // AEP properties are a read-only system snapshot. Do not create/dispose
            // a BluetoothLEDevice or discover GATT services just to check status.
            const string connectedKey = "System.Devices.Aep.IsConnected";
            const string addressKey = "System.Devices.Aep.DeviceAddress";
            var devices = AsT(DeviceInformation.FindAllAsync(
                BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                new[] { connectedKey, addressKey }, DeviceInformationKind.AssociationEndpoint)).GetAwaiter().GetResult();
            if (devices != null) foreach (var d in devices) {
                object value;
                string address = d.Properties.TryGetValue(addressKey, out value) ? Convert.ToString(value) : "";
                if (!IsDiagnosticRemote(d.Name, address, config)) continue;
                bool? connected = d.Properties.TryGetValue(connectedKey, out value) && value is bool ? (bool?)value : null;
                report.Devices.Add(new RemoteDeviceStatus {
                    Name = string.IsNullOrWhiteSpace(d.Name) ? address : d.Name, Connected = connected
                });
            }
        } catch (Exception ex) { report.Error = ex.Message; }
        return report;
    }

    /// All paired Bluetooth LE device names.
    public static List<string> ListPairedBleNames() {
        var names = new List<string>();
        var devs = AsT(DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true))).GetAwaiter().GetResult();
        if (devs != null)
            foreach (var d in devs)
                if (!string.IsNullOrEmpty(d.Name)) names.Add(d.Name);
        return names;
    }

    /// Human-readable default radio info; throws if no adapter.
    public static string GetDefaultRadioInfo() {
        var adapter = AsT(Windows.Devices.Bluetooth.BluetoothAdapter.GetDefaultAsync()).GetAwaiter().GetResult();
        if (adapter == null) throw new Exception("未检测到蓝牙适配器");
        if (!adapter.IsLowEnergySupported) throw new Exception("蓝牙适配器不支持 BLE");
        var radio = AsT(adapter.GetRadioAsync()).GetAwaiter().GetResult();
        if (radio == null || radio.State != Windows.Devices.Radios.RadioState.On)
            throw new Exception("蓝牙未开启或无线电不可用，请在 Windows 蓝牙设置中开启（" + (radio == null ? "未知" : radio.State.ToString()) + "）");
        ulong a = adapter.BluetoothAddress;
        return string.Format("适配器正常 ({0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2})",
            (a >> 40) & 0xFF, (a >> 32) & 0xFF, (a >> 24) & 0xFF, (a >> 16) & 0xFF, (a >> 8) & 0xFF, a & 0xFF);
    }

    // IAsyncOperation<T> -> Task, for WinRT generic ops referenced via plain reflection.
    static class RuntimeAsync {
        public static object AwaitObject(object op) {
            foreach (var iface in op.GetType().GetInterfaces()) {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition().FullName == "Windows.Foundation.IAsyncOperation`1") {
                    var method = typeof(RuntimeAsync).GetMethod("AwaitGeneric",
                        BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(iface.GetGenericArguments()[0]);
                    return method.Invoke(null, new object[] { op });
                }
            }
            throw new Exception("not an IAsyncOperation<T>: " + op.GetType());
        }
        static object AwaitGeneric<T>(object opObj) {
            var op = (IAsyncOperation<T>)opObj;
            var tcs = new TaskCompletionSource<object>();
            op.Completed = delegate (IAsyncOperation<T> o, AsyncStatus s) {
                if (s == AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                else tcs.TrySetResult(null);
            };
            return tcs.Task.Result;
        }
    }

    internal static Task<T> AsT<T>(IAsyncOperation<T> op, CancellationToken token = default(CancellationToken)) {
        // Never run the next GATT call inside a native completion callback.
        // Some Bluetooth drivers hold internal locks until that callback returns.
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        op.Completed = delegate (IAsyncOperation<T> o, AsyncStatus s) {
            try {
                if (s == AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                else if (s == AsyncStatus.Error) tcs.TrySetException(o.ErrorCode);
                else tcs.TrySetCanceled();
            } catch (Exception ex) { tcs.TrySetException(ex); }
        };
        return AsyncDeadline.Wait(tcs.Task, token, 10000, delegate { try { op.Cancel(); } catch { } });
    }

    internal static byte[] ToBytes(IBuffer buf) {
        var r = DataReader.FromBuffer(buf);
        var b = new byte[buf.Length];
        if (buf.Length > 0) r.ReadBytes(b);
        return b;
    }
}

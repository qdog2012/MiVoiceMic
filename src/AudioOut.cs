// AudioOut.cs - waveOut streamer: pushes decoded 16 kHz mono PCM into the
// render side of the virtual audio cable (default: "CABLE Input" from VB-CABLE),
// plus a small WAV writer for session dumps.
// 中文：waveOut 推流器 —— 把解码 PCM 写入虚拟声卡播放端；附 WAV 落盘工具
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

sealed class AudioOut {
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern int waveOutGetDevCaps(uint id, ref WAVEOUTCAPS pwoc, int cbwoc);
    [DllImport("winmm.dll")]
    static extern int waveOutOpen(out IntPtr phwi, uint id, ref WAVEFORMATEX pwfx, IntPtr cb, IntPtr inst, uint fdo);
    [DllImport("winmm.dll")]
    static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")]
    static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")]
    static extern int waveOutMessage(IntPtr device, uint message, IntPtr parameter, IntPtr reserved);
    [DllImport("winmm.dll")]
    static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")]
    static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")]
    static extern int waveOutClose(IntPtr hwo);
    [DllImport("winmm.dll")]
    static extern uint waveOutGetNumDevs();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WAVEOUTCAPS {
        public ushort wMid, wPid; public uint v;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name;
        public uint df; public ushort ch, r1; public uint sup;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX { public ushort tag; public ushort ch; public uint sr; public uint avg; public ushort blk; public ushort bits; public ushort cb; }

    const int NB = 10;                 // queued buffers (~150 ms at 15 ms/frame)
    const int HDR_SIZE = 48;           // WAVEHDR on x64
    const int OFF_FLAGS = 24;
    const int WHDR_DONE = 1;
    const int SAMPLES_PER_FRAME = 240; // 120 bytes ADPCM = 240 samples

    IntPtr hWave = IntPtr.Zero;
    readonly IntPtr[] hdr = new IntPtr[NB];
    readonly IntPtr[] data = new IntPtr[NB];
    readonly bool[] used = new bool[NB];
    readonly Queue<short[]> q = new Queue<short[]>();
    readonly object qlock = new object();
    volatile bool running;
    Thread thr;

    public string DeviceUsed { get; private set; }
    public long FramesPlayed { get; private set; }
    public long FramesDropped { get; private set; }
    public string LastError { get; private set; }

    /// List render device names (for diagnostics).
    public static List<string> ListRenderDevices() {
        var names = new List<string>();
        foreach (var device in ListRenderEndpoints()) names.Add(device.Name);
        return names;
    }

    public static List<AudioDeviceInfo> ListRenderEndpoints() {
        var devices = new List<AudioDeviceInfo>();
        List<AudioDeviceInfo> endpoints;
        try { endpoints = DeviceSwitcher.ListRenderEndpoints(); }
        catch { endpoints = new List<AudioDeviceInfo>(); }
        uint n = waveOutGetNumDevs();
        for (uint i = 0; i < n; i++) {
            var c = new WAVEOUTCAPS();
            if (waveOutGetDevCaps(i, ref c, Marshal.SizeOf(c)) != 0 || c.name == null) continue;
            string id = RenderEndpointId(i);
            var device = new AudioDeviceInfo { WaveId = i, Id = id, Name = c.name, LegacyName = c.name };
            foreach (var ep in endpoints)
                if (string.Equals(ep.Id, id, StringComparison.OrdinalIgnoreCase)) { device.Name = ep.Name; break; }
            devices.Add(device);
        }
        return devices;
    }

    static string RenderEndpointId(uint index) {
        IntPtr size = Marshal.AllocHGlobal(4), text = IntPtr.Zero;
        try {
            Marshal.WriteInt32(size, 0);
            // DRV_QUERYFUNCTIONINSTANCEIDSIZE / DRV_QUERYFUNCTIONINSTANCEID.
            if (waveOutMessage((IntPtr)index, 0x812, size, IntPtr.Zero) != 0) return null;
            int bytes = Marshal.ReadInt32(size);
            if (bytes < 2 || bytes > 65536) return null;
            text = Marshal.AllocHGlobal(bytes);
            if (waveOutMessage((IntPtr)index, 0x811, text, (IntPtr)bytes) != 0) return null;
            return Marshal.PtrToStringUni(text);
        } finally { Marshal.FreeHGlobal(size); if (text != IntPtr.Zero) Marshal.FreeHGlobal(text); }
    }

    public bool Start(string nameContains, int sr, string endpointId = null) {
        Stop();
        LastError = null;
        var device = AudioDevices.Resolve(ListRenderEndpoints(), nameContains, endpointId);
        if (device == null) { LastError = "未找到唯一匹配的播放设备：" + nameContains; return false; }
        DeviceUsed = device.Name;

        var wfx = new WAVEFORMATEX { tag = 1, ch = 1, sr = (uint)sr, avg = (uint)(sr * 2), blk = 2, bits = 16, cb = 0 };
        int hr = waveOutOpen(out hWave, device.WaveId, ref wfx, IntPtr.Zero, IntPtr.Zero, 0);
        if (hr != 0) { hWave = IntPtr.Zero; LastError = "播放设备无法打开（错误 " + hr + "）：" + DeviceUsed; return false; }

        try {
            for (int i = 0; i < NB; i++) {
                data[i] = Marshal.AllocHGlobal(SAMPLES_PER_FRAME * 2);
                hdr[i] = Marshal.AllocHGlobal(HDR_SIZE);
                for (int o = 0; o < HDR_SIZE; o++) Marshal.WriteByte(hdr[i], o, 0);
                Marshal.WriteIntPtr(hdr[i], 0, data[i]);
                Marshal.WriteInt32(hdr[i], 8, SAMPLES_PER_FRAME * 2);
                hr = waveOutPrepareHeader(hWave, hdr[i], HDR_SIZE);
                if (hr != 0) throw new InvalidOperationException("准备音频缓冲失败（错误 " + hr + "）");
            }
        } catch (Exception ex) { LastError = ex.Message; Stop(); return false; }
        running = true;
        thr = new Thread(Pump) { IsBackground = true, Name = "wavout" };
        thr.Start();
        return true;
    }

    public void Enqueue(short[] samples) {
        lock (qlock) {
            if (!running) return;
            q.Enqueue(samples);
            if (q.Count > 40) { q.Dequeue(); FramesDropped++; }  // drop oldest if backed up (>600 ms)
        }
    }

    public void Stop() {
        running = false;
        if (thr != null) { thr.Join(); thr = null; }
        if (hWave != IntPtr.Zero) {
            waveOutReset(hWave);
            for (int i = 0; i < NB; i++) {
                if (hdr[i] != IntPtr.Zero) {
                    waveOutUnprepareHeader(hWave, hdr[i], HDR_SIZE);
                    Marshal.FreeHGlobal(hdr[i]); hdr[i] = IntPtr.Zero;
                }
                if (data[i] != IntPtr.Zero) { Marshal.FreeHGlobal(data[i]); data[i] = IntPtr.Zero; }
                used[i] = false;
            }
            waveOutClose(hWave);
            hWave = IntPtr.Zero;
        }
        lock (qlock) q.Clear();
    }

    void Pump() {
        while (running) {
            int freeIdx = -1;
            for (int i = 0; i < NB; i++) {
                if (used[i] && (Marshal.ReadInt32(hdr[i], OFF_FLAGS) & WHDR_DONE) != 0)
                    used[i] = false;
                if (!used[i]) { freeIdx = i; break; }
            }
            short[] samples = null;
            if (freeIdx >= 0) lock (qlock) { if (q.Count > 0) samples = q.Dequeue(); }
            if (freeIdx >= 0 && samples != null) {
                int cnt = Math.Min(samples.Length, SAMPLES_PER_FRAME);
                Marshal.Copy(samples, 0, data[freeIdx], cnt);
                Marshal.WriteInt32(hdr[freeIdx], 8, cnt * 2);
                int error = waveOutWrite(hWave, hdr[freeIdx], HDR_SIZE);
                used[freeIdx] = error == 0;
                if (error == 0) FramesPlayed++;
                else { FramesDropped++; LastError = "音频写入失败（错误 " + error + "）"; }
            } else {
                Thread.Sleep(3);
            }
        }
    }
}

static class WavWriter {
    public static void Write(string path, List<short> pcm, int sr) {
        int ds = pcm.Count * 2;
        using (var fs = new FileStream(path, FileMode.Create))
        using (var w = new BinaryWriter(fs)) {
            var a = System.Text.Encoding.ASCII;
            w.Write(a.GetBytes("RIFF")); w.Write(36 + ds); w.Write(a.GetBytes("WAVE"));
            w.Write(a.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(sr); w.Write(sr * 2); w.Write((short)2); w.Write((short)16);
            w.Write(a.GetBytes("data")); w.Write(ds);
            byte[] b = new byte[ds];
            System.Buffer.BlockCopy(pcm.ToArray(), 0, b, 0, ds);
            w.Write(b);
        }
    }
}

// AdpcmDecoder.cs - IMA/DVI ADPCM (16 kHz mono, 4-bit, high-nibble-first) for the
// Xiaomi remote's ATVV audio, plus frame accumulation, AUDIO_SYNC handling and
// PCM post-processing (declip + 3-tap lowpass + AGC / fixed gain).
//
// Protocol notes cross-checked against QL-4/RemoteMapper NOTES.md and
// HD838A/remote-mic-app (ATVVProtocol.swift).
// 中文：IMA ADPCM 解码器 —— ATVV 语音帧解码为 16kHz PCM，含 AUDIO_SYNC 复位与去尖峰/低通/AGC 后处理
using System;
using System.Collections.Generic;

sealed class AdpcmDecoder {
    // ATVV frame accumulator: BLE notifications may split or merge audio frames
    readonly List<byte> pending = new List<byte>(256);
    int frameSize = 120;                 // bytes per audio frame (updated from CAPS)

    // decoder state
    int predictor = 0, stepIndex = 0;
    short lastSample = 0;                // cross-frame lowpass continuity
    short prevDecoded = 0;               // cross-frame declip continuity

    // AUDIO_SYNC: reset applied before the next decoded frame
    bool syncPending = false;
    int syncPredictor = 0, syncStepIndex = 0;

    // post-processing
    bool agc;
    double fixedGain;                     // linear, from gainDb
    double agcPeak = 1000;
    const double AGC_TARGET = 28000;
    const double AGC_DECAY = 0.9997;
    const double AGC_MAX_GAIN = 30;
    const double AGC_FLOOR = 200;
    readonly object gate = new object(); // notify callbacks can arrive on different pool threads

    static readonly int[] STEP = {
        7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,
        73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,
        494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,
        2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,
        11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767 };
    static readonly int[] IDX = { -1, -1, -1, -1, 2, 4, 6, 8 };

    public AdpcmDecoder(bool agc, double gainDb) {
        this.agc = agc;
        double db = Math.Max(-24.0, Math.Min(24.0, gainDb));
        this.fixedGain = Math.Pow(10.0, db / 20.0);
    }

    /// Runtime gain change from the settings UI (no session reset).
    public void SetGain(bool agcEnabled, double gainDb) {
        lock (gate) {
            agc = agcEnabled;
            double db = Math.Max(-24.0, Math.Min(24.0, gainDb));
            fixedGain = Math.Pow(10.0, db / 20.0);
        }
    }

    public int FrameSize {
        get { lock (gate) return frameSize; }
        set { lock (gate) { if (value > 0) frameSize = value; } }
    }

    /// Reset at the start of each voice session.
    public void ResetSession() {
        lock (gate) {
            predictor = 0; stepIndex = 0;
            lastSample = 0; prevDecoded = 0;
            agcPeak = 1000;
            syncPending = false;
            pending.Clear();
        }
    }

    /// Apply an AUDIO_SYNC packet (predictor big-endian signed, step index).
    public void ApplySync(int predictorBe, int stepIdx) {
        lock (gate) {
            syncPredictor = predictorBe;
            syncStepIndex = stepIdx;
            syncPending = true;
            pending.Clear();   // partial frame before a sync is invalid
        }
    }

    /// Feed one BLE audio notification; returns 0..n decoded 16-bit sample blocks.
    public List<short[]> Feed(byte[] data) {
        if (data == null || data.Length == 0) return null;
        lock (gate) {
            int fs = frameSize;
            if (pending.Count == 0 && data.Length == fs && !syncPending) {
                var one = DecodeFrame(data, fs);
                return one == null ? null : new List<short[]>(1) { one };
            }
            pending.AddRange(data);
            List<short[]> outFrames = null;
            while (pending.Count >= fs) {
                byte[] frame = new byte[fs];
                pending.CopyTo(0, frame, 0, fs);
                pending.RemoveRange(0, fs);
                var s = DecodeFrame(frame, fs);
                if (s != null) {
                    if (outFrames == null) outFrames = new List<short[]>(2);
                    outFrames.Add(s);
                }
            }
            return outFrames;
        }
    }

    short[] DecodeFrame(byte[] data, int fs) {
        if (syncPending) {
            predictor = syncPredictor;
            stepIndex = syncStepIndex;
            syncPending = false;
        }
        var samples = new short[fs * 2];
        int pred = predictor, si = stepIndex;
        int n = Math.Min(data.Length, fs);
        int k = 0;
        for (int i = 0; i < n; i++) {
            samples[k++] = (short)Nibble(data[i] >> 4, ref pred, ref si);
            samples[k++] = (short)Nibble(data[i] & 0xF, ref pred, ref si);
        }
        predictor = pred; stepIndex = si;
        Declip(samples);
        Lowpass(samples);
        if (agc) {
            for (int i = 0; i < samples.Length; i++) {
                double v = samples[i];
                double a = v < 0 ? -v : v;
                if (a > agcPeak) agcPeak = a; else agcPeak *= AGC_DECAY;
                double g = Math.Min(AGC_MAX_GAIN, AGC_TARGET / Math.Max(agcPeak, AGC_FLOOR));
                v *= g;
                if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                samples[i] = (short)v;
            }
        } else if (fixedGain != 1.0) {
            for (int i = 0; i < samples.Length; i++) {
                double v = samples[i] * fixedGain;
                if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                samples[i] = (short)Math.Round(v);
            }
        }
        return samples;
    }

    static int Nibble(int nibble, ref int predictor, ref int stepIndex) {
        int step = STEP[stepIndex];
        int diff = step >> 3;
        if ((nibble & 1) != 0) diff += step >> 2;
        if ((nibble & 2) != 0) diff += step >> 1;
        if ((nibble & 4) != 0) diff += step;
        if ((nibble & 8) != 0) predictor -= diff; else predictor += diff;
        if (predictor > 32767) predictor = 32767;
        if (predictor < -32768) predictor = -32768;
        stepIndex += IDX[nibble & 7];
        if (stepIndex < 0) stepIndex = 0;
        if (stepIndex > 88) stepIndex = 88;
        return predictor;
    }

    // Remove isolated spikes: current sample far from BOTH neighbours while the
    // neighbours are close to each other -> replace with their average.
    void Declip(short[] s) {
        const int TH = 1000;
        int len = s.Length;
        int prev = prevDecoded;
        for (int i = 0; i < len; i++) {
            int p = i == 0 ? prev : s[i - 1];
            int nx = i == len - 1 ? s[i] : s[i + 1];
            int cur = s[i];
            int dp = Math.Abs(cur - p), dn = Math.Abs(cur - nx);
            int nd = Math.Abs(nx - p);
            if (dp > TH && dn > TH && Math.Min(dp, dn) > nd * 2)
                s[i] = (short)((p + nx) / 2);
        }
        prevDecoded = s[len - 1];
    }

    void Lowpass(short[] s) {
        if (s.Length == 0) return;
        short prev = lastSample;
        for (int i = 0; i < s.Length - 1; i++) {
            short cur = s[i];
            s[i] = (short)((prev + 2 * cur + s[i + 1]) >> 2);
            prev = cur;
        }
        lastSample = s[s.Length - 1];
    }

    /// Encode samples back to ADPCM nibbles (test helper + round-trip check).
    public static byte[] Encode(params short[] samples) {
        var bytes = new List<byte>(samples.Length / 2 + 1);
        int pred = 0, si = 0;
        for (int i = 0; i < samples.Length; i += 2) {
            int hi = EncodeNibble(samples[i], ref pred, ref si);
            int lo = (i + 1 < samples.Length) ? EncodeNibble(samples[i + 1], ref pred, ref si) : 0;
            bytes.Add((byte)((hi << 4) | (lo & 0xF)));
        }
        return bytes.ToArray();
    }

    static int EncodeNibble(short target, ref int predictor, ref int stepIndex) {
        int step = STEP[stepIndex];
        int diff = target - predictor;
        int nibble = 0;
        if (diff < 0) { nibble = 8; diff = -diff; }
        if (diff >= step) { nibble |= 4; diff -= step; }
        if (diff >= step >> 1) { nibble |= 2; diff -= step >> 1; }
        if (diff >= step >> 2) { nibble |= 1; }
        // run the decoder forward with this nibble so encoder/decoder stay in sync
        Nibble(nibble, ref predictor, ref stepIndex);
        return nibble;
    }
}

// Session lifetime and key ownership, independent of Bluetooth/SendInput for tests.
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

static class AsyncDeadline {
    public static async Task<T> Wait<T>(Task<T> operation, CancellationToken token, int timeoutMs, Action cancel) {
        using (var timer = CancellationTokenSource.CreateLinkedTokenSource(token)) {
            var deadline = Task.Delay(timeoutMs, timer.Token);
            var completed = await Task.WhenAny(operation, deadline);
            if (completed != operation || token.IsCancellationRequested) {
                if (cancel != null) cancel();
                // Observe a late failure from a cancelled native operation.
                ObserveLateFailure(operation);
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("蓝牙操作超过 " + timeoutMs / 1000.0 + " 秒未响应");
            }
            timer.Cancel();
            return await operation;
        }
    }

    static void ObserveLateFailure(Task operation) {
        operation.ContinueWith(t => { var ignored = t.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

sealed class VoiceSessionGuard {
    public const int AudioTimeoutMs = 2500;
    public const int StartTimeoutMs = 4000;
    public const int MaxSessionMs = 300000;
    public bool Active { get; private set; }
    public long Generation { get; private set; }
    byte session;
    long started, lastAudio;
    bool receivedAudio;

    public static long NowMs { get { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); } }

    public bool IsDuplicate(byte id) { return Active && session == id; }
    public void Start(byte id, long now) {
        Generation++;
        Active = true; session = id;
        started = lastAudio = now; receivedAudio = false;
    }
    public void Audio(long now) { if (Active) { lastAudio = now; receivedAudio = true; } }
    public void Stop() { Active = false; Generation++; }
    public bool IsCurrent(long generation) { return Active && Generation == generation; }
    public string TimeoutReason(long now) {
        if (!Active) return null;
        if (now - started >= MaxSessionMs) return "语音会话超过 5 分钟";
        if (now - lastAudio >= (receivedAudio ? AudioTimeoutMs : StartTimeoutMs))
            return receivedAudio ? "音频流中断，未收到结束通知" : "语音开始后未收到音频";
        return null;
    }
}

interface IVoiceHotkey {
    void PrepareVoiceDown();
    void OnVoiceDown();
    void OnVoiceUp();
    void ForceRelease();
}

// Used only by the key worker. A session releases the exact injector it pressed,
// even if the UI changes the configured combo or disables injection meanwhile.
sealed class VoiceKeySession {
    readonly Action restoreMic;
    IVoiceHotkey active;
    bool restorePending;
    public VoiceKeySession(Action restore) { restoreMic = restore; }

    public void Begin(IVoiceHotkey hotkey, Action switchMic, Func<bool> stillCurrent, object pressGate = null) {
        End();
        if (!stillCurrent()) return;
        try {
            if (switchMic != null) { restorePending = true; switchMic(); }
            if (!stillCurrent()) { End(); return; }
            // Preparation may wait for key-up to settle. STOP must still be able
            // to cancel the press during this wait, including tap-mode toggles.
            if (hotkey != null) hotkey.PrepareVoiceDown();
            bool cancelled;
            lock (pressGate ?? this) {
                cancelled = !stillCurrent();
                if (!cancelled) {
                    active = hotkey;
                    if (active != null) active.OnVoiceDown();
                }
            }
            if (cancelled) End();
        } catch { End(); throw; }
    }

    public void End() {
        var previous = active;
        bool restore = restorePending;
        active = null; restorePending = false;
        try {
            if (previous != null) previous.OnVoiceUp();
        } finally {
            try { if (previous != null) previous.ForceRelease(); }
            finally { if (restore) restoreMic(); }
        }
    }
}

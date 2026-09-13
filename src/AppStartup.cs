using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// One GUI/voice connection per installation in the current Windows session.
// Tool modes and the independent update helper do not acquire this lock.
sealed class AppInstance : IDisposable {
    readonly Mutex mutex;
    readonly EventWaitHandle showRequested;
    public readonly bool IsPrimary;

    public AppInstance(string executablePath) {
        string identity;
        using (var hash = SHA256.Create())
            identity = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                Path.GetFullPath(executablePath).ToUpperInvariant()))).Replace("-", "");
        string name = @"Local\MiVoiceMic." + identity;
        // Create the signal before acquiring the lock, so a second launch cannot
        // lose its request while the primary process is still initializing.
        showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Show");
        mutex = new Mutex(false, name + ".Instance");
        try { IsPrimary = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
    }

    public void RequestShow() { showRequested.Set(); }
    public bool TakeShowRequest() { return showRequested.WaitOne(0); }

    public void Dispose() {
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
        showRequested.Dispose();
    }
}

sealed class TrayApplicationContext : ApplicationContext {
    readonly System.Windows.Forms.Timer showTimer;

    public TrayApplicationContext(MainWindow window, AppInstance instance, bool startInTray) {
        // Leave ApplicationContext.MainForm unset: Application.Run would otherwise
        // show it automatically. The tray owns the lifetime, with no startup flash.
        window.FormClosed += delegate { ExitThread(); };
        showTimer = new System.Windows.Forms.Timer { Interval = 200 };
        showTimer.Tick += delegate {
            if (instance.TakeShowRequest()) TrayIcon.ShowMain();
        };
        showTimer.Start();
        if (!startInTray) window.Show();
    }

    protected override void Dispose(bool disposing) {
        if (disposing) showTimer.Dispose();
        base.Dispose(disposing);
    }
}

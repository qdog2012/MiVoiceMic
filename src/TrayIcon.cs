// TrayIcon.cs - WinForms notify icon: status, preset switching, toggles, reconnect.
// TrayApplicationContext owns the message loop; this class creates the icon/menu.
// Status updates arrive from BLE threads; they are cached here and applied on the
// UI thread by a timer tick (no cross-thread control access).
// 中文：托盘图标 —— 状态/预设/开关快捷菜单；含 AutoStart 开机自启（HKCU Run 键）助手
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// A solid badge stays legible on both light and dark Windows taskbars.
// Render at the actual small-icon size so two-digit percentages remain crisp.
static class TrayBatteryIcon {
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    public static bool HasBattery(int percent) { return percent >= 0 && percent <= 100; }
    public static string Label(bool linked, int percent) {
        return !linked ? "×" : HasBattery(percent) ? percent.ToString(CultureInfo.InvariantCulture) : "?";
    }
    public static Color BadgeColor(bool linked, int percent, int charging) {
        if (!linked) return Color.FromArgb(99, 105, 115);
        if (charging == 1) return Color.FromArgb(24, 128, 65);
        if (!HasBattery(percent)) return Color.FromArgb(99, 105, 115);
        if (percent <= 15) return Color.FromArgb(190, 36, 44);
        if (percent <= 30) return Color.FromArgb(158, 87, 0);
        return Color.FromArgb(30, 94, 190);
    }
    public static string Tooltip(bool linked, int percent, int charging, string status) {
        string detail = !linked ? "未连接" : HasBattery(percent) ? "电量 " + percent + "%" : "电量读取中";
        if (linked && charging == 1) detail += "（充电中）";
        string text = "MiVoiceMic · " + detail + "\n" + (status ?? "");
        return text.Length > 63 ? text.Substring(0, 60) + "..." : text;
    }

    public static Bitmap Render(bool linked, int percent, int charging, int size) {
        size = Math.Max(16, Math.Min(64, size));
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap)) {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float scale = size / 16f, diameter = 5 * scale;
            using (var path = new GraphicsPath()) {
                float edge = size - 1;
                path.AddArc(0, 0, diameter, diameter, 180, 90);
                path.AddArc(edge - diameter, 0, diameter, diameter, 270, 90);
                path.AddArc(edge - diameter, edge - diameter, diameter, diameter, 0, 90);
                path.AddArc(0, edge - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                using (var brush = new SolidBrush(BadgeColor(linked, percent, charging))) g.FillPath(brush, path);
            }
            string label = Label(linked, percent);
            float fontSize = (label.Length == 3 ? 8.5f : 11.5f) * scale;
            using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat(StringFormat.GenericTypographic)) {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(label, font, Brushes.White, new RectangleF(0, 0, size - 1, size - 2 * scale), format);
            }
            if (linked && HasBattery(percent)) {
                float width = size - 6 * scale;
                using (var brush = new SolidBrush(Color.FromArgb(80, Color.White)))
                    g.FillRectangle(brush, 3 * scale, size - 3 * scale, width, scale);
                if (percent > 0) g.FillRectangle(Brushes.White, 3 * scale, size - 3 * scale, width * percent / 100f, scale);
            }
        }
        return bitmap;
    }

    public static Icon Create(bool linked, int percent, int charging, int size) {
        using (var bitmap = Render(linked, percent, charging, size)) {
            IntPtr handle = bitmap.GetHicon();
            try {
                using (var borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone();
            } finally { DestroyIcon(handle); }
        }
    }
}

static class TrayIcon {
    static NotifyIcon icon;
    static Icon ownedIcon;
    static Timer timer;
    static string renderedKey;
    static int renderedSize;
    static ToolStripMenuItem miStatus, miBattery, miBlockF5, miSwitchMic;
    static List<ToolStripMenuItem> presetItems = new List<ToolStripMenuItem>();
    static App app;

    public static System.Windows.Forms.Form MainWindow;

    public static NotifyIcon Instance { get { return icon; } }

    // cross-thread cached state
    static readonly object gate = new object();
    static volatile string statusText = "启动中...";
    static volatile bool linkedFlag;
    static volatile int battery = -1;
    static volatile int charging = -1;
    static volatile bool dirty = true;

    public static NotifyIcon Create(App application) {
        Dispose();
        app = application;
        var menu = new ContextMenuStrip();

        menu.Items.Add(new ToolStripMenuItem("打开主界面", null, delegate { Safe(ShowMain); }));
        menu.Items.Add(new ToolStripSeparator());

        miStatus = new ToolStripMenuItem("状态: 启动中...") { Enabled = false };
        miBattery = new ToolStripMenuItem("电量: --") { Enabled = false, Visible = false };
        menu.Items.Add(miStatus);
        menu.Items.Add(miBattery);
        menu.Items.Add(new ToolStripSeparator());

        var miPreset = new ToolStripMenuItem("语音热键");
        var presets = new[] {
            new { Text = "微信输入法 (右Alt+逗号 按住)", Key = "wetype" },
            new { Text = "Windows 语音键入 (Win+H 点按)", Key = "winh" },
            new { Text = "不注入热键 (仅推流音频)", Key = "none" },
        };
        foreach (var p in presets) {
            string key = p.Key;
            var item = new ToolStripMenuItem(p.Text, null, delegate { SetPreset(key); });
            presetItems.Add(item);
            miPreset.DropDownItems.Add(item);
        }
        menu.Items.Add(miPreset);

        miBlockF5 = new ToolStripMenuItem("拦截语音键附带的键盘信号", null, delegate { ToggleBlockF5(); });
        miBlockF5.Checked = app.Config.blockF5;
        miSwitchMic = new ToolStripMenuItem("说话时自动切换默认麦克风", null, delegate { ToggleSwitchMic(); });
        miSwitchMic.Checked = app.Config.switchDefaultMic;
        menu.Items.Add(miBlockF5);
        menu.Items.Add(miSwitchMic);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("重新连接遥控器", null, delegate { Safe(delegate { app.Reconnect(); }); }));
        menu.Items.Add(new ToolStripMenuItem("编辑配置 (config.json)", null, delegate { OpenConfig(); }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { ExitApplication(); }));

        icon = new NotifyIcon {
            Text = "MiVoiceMic - 小米遥控器语音",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += delegate { Safe(ShowMain); };

        timer = new Timer { Interval = 800 };
        timer.Tick += delegate { Safe(ApplyPending); };
        timer.Start();
        ApplyConfigChecks();
        ApplyPending();
        return icon;
    }

    public static void Dispose() {
        if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
        if (icon != null) {
            var menu = icon.ContextMenuStrip;
            icon.Visible = false; icon.Dispose(); icon = null;
            if (menu != null) menu.Dispose();
        }
        if (ownedIcon != null) { ownedIcon.Dispose(); ownedIcon = null; }
        presetItems.Clear();
        renderedKey = null; renderedSize = 0;
        dirty = true;
    }

    public static void ExitApplication() {
        // Release held hotkeys and restore audio before the updater replaces the executable.
        Safe(delegate { if (app != null) app.Shutdown(); });
        Dispose();
        Log.Close();
        Application.ExitThread();
        Environment.Exit(0);
    }

    public static void ShowMain() {
        if (MainWindow != null) { MainWindow.Show(); MainWindow.WindowState = FormWindowState.Normal; ActivateWindow(MainWindow); }
    }

    static void ActivateWindow(Form f) {
        try {
            uint fore = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), IntPtr.Zero);
            uint mine = Native.GetCurrentThreadId();
            if (fore != mine) Native.AttachThreadInput(fore, mine, true);
            Native.SetForegroundWindow(f.Handle);
            if (fore != mine) Native.AttachThreadInput(fore, mine, false);
        } catch { }
    }

    static void Safe(Action a) {
        try { a(); } catch (Exception ex) { Log.Error("[TRAY] " + ex.Message); }
    }

    static void SetPreset(string preset) {
        Safe(delegate {
            var cfg = app.Config;
            cfg.hotkey.preset = preset;
            if (preset == "wetype") {
                cfg.hotkey.mode = "hold";
                cfg.hotkey.keys = new List<string> { "LCTRL", "LWIN" };
                cfg.hotkeyEnabled = true;
            } else if (preset == "winh") {
                cfg.hotkey.mode = "tap";
                cfg.hotkey.keys = new List<string> { "LWIN", "H" };
                cfg.hotkeyEnabled = true;
            } else {
                cfg.hotkeyEnabled = false;
            }
            cfg.Save();
            app.ApplyConfig(cfg);
            ApplyConfigChecks();
            Log.Info("[TRAY] preset -> " + preset);
        });
    }

    static void ToggleBlockF5() {
        Safe(delegate {
            var cfg = app.Config;
            cfg.blockF5 = !cfg.blockF5;
            cfg.Save();
            app.ApplyConfig(cfg);
            ApplyConfigChecks();
        });
    }

    static void ToggleSwitchMic() {
        Safe(delegate {
            var cfg = app.Config;
            cfg.switchDefaultMic = !cfg.switchDefaultMic;
            cfg.Save();
            app.ApplyConfig(cfg);
            ApplyConfigChecks();
        });
    }

    static void ApplyConfigChecks() {
        string preset = app.Config.hotkeyEnabled ? app.Config.hotkey.preset : "none";
        for (int i = 0; i < presetItems.Count; i++)
            presetItems[i].Checked = (i == 0 && preset == "wetype") || (i == 1 && preset == "winh") || (i == 2 && preset == "none");
        miBlockF5.Checked = app.Config.blockF5;
        miSwitchMic.Checked = app.Config.switchDefaultMic;
    }

    static void OpenConfig() {
        try { Process.Start("notepad.exe", Config.ConfigPath); } catch (Exception ex) { Log.Error("[TRAY] " + ex.Message); }
    }

    /// Called from any thread.
    public static void SetStatus(bool connected, string detail) {
        lock (gate) {
            if (!connected || !linkedFlag) { battery = -1; charging = -1; }
            linkedFlag = connected;
            string text = connected ? "已连接: " + detail : (detail ?? "未连接");
            if (text.Length > 40) text = text.Substring(0, 40) + "...";
            statusText = text;
            dirty = true;
        }
    }

    /// Called from any thread.
    public static void SetBattery(int percent) {
        lock (gate) { battery = percent; dirty = true; }
    }

    /// Called from any thread.
    public static void SetCharging(int state) {
        lock (gate) { charging = state; dirty = true; }
    }

    static void ApplyPending() {
        if (icon == null) return;
        int size = Math.Max(16, Math.Min(64, SystemInformation.SmallIconSize.Width));
        if (!dirty && renderedSize == size) return;
        string st; bool linked; int bat; int chg;
        lock (gate) {
            st = statusText; linked = linkedFlag; bat = battery; chg = charging;
            dirty = false;
        }
        miStatus.Text = "状态: " + st;
        miBattery.Visible = linked;
        miBattery.Text = "电量: " + (TrayBatteryIcon.HasBattery(bat) ? bat + "%" : "读取中") + (chg == 1 ? " (充电中)" : "");
        icon.Text = TrayBatteryIcon.Tooltip(linked, bat, chg, st);
        string key = linked + ":" + bat + ":" + chg + ":" + size;
        if (renderedKey != key) {
            var next = TrayBatteryIcon.Create(linked, bat, chg, size);
            try { icon.Icon = next; }
            catch { next.Dispose(); dirty = true; throw; }
            var previous = ownedIcon;
            ownedIcon = next;
            renderedKey = key; renderedSize = size;
            if (previous != null) previous.Dispose();
        }
    }
}

// HKCU Run-key autostart toggle used by the remote-behaviour card.
static class AutoStart {
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "MiVoiceMic";

    public static string Command(string executablePath) { return "\"" + executablePath + "\" --autostart"; }

    public static void UpgradeExistingRegistration() {
        // Migrate the old command only for this installation. Never enable a
        // disabled startup entry or redirect another copy's registration.
        try {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath, true)) {
                if (k == null) return;
                string value = k.GetValue(ValueName) as string;
                if (value == null) return;
                string exe = Application.ExecutablePath;
                if (string.Equals(value.Trim(), "\"" + exe + "\"", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value.Trim(), exe, StringComparison.OrdinalIgnoreCase)) {
                    k.SetValue(ValueName, Command(exe));
                    Log.Info("[AUTOSTART] 已将现有开机自启改为托盘启动");
                }
            }
        } catch (Exception ex) { Log.Error("[AUTOSTART] migrate: " + ex.Message); }
    }

    public static bool IsEnabled() {
        try {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath))
                return k != null && k.GetValue(ValueName) != null;
        } catch { return false; }
    }

    public static void Set(bool on) {
        try {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath)) {
                if (on) k.SetValue(ValueName, Command(Application.ExecutablePath));
                else k.DeleteValue(ValueName, false);
            }
        } catch (Exception ex) { Log.Error("[AUTOSTART] " + ex.Message); }
    }
}

static class Native {
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}

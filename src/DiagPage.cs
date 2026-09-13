// DiagPage.cs - 环境自检 page + shared EnvChecks used by both the page and
// the --check CLI mode.
// 中文：环境自检页 —— 与命令行 --check 共用 EnvChecks 检查逻辑
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

enum CheckState { Ok, Warn, Fail, Info }

class CheckItem {
    public string Id;
    public string Title;
    public CheckState State;
    public bool Required = true;
    public RemoteConnectionReport RemoteReport;
    public List<string> Lines = new List<string>();
}

static class EnvChecks {
    public static List<CheckItem> Run(App app = null) {
        var items = new List<CheckItem>();

        // 1. Bluetooth adapter
        var bt = new CheckItem { Title = "蓝牙适配器" };
        try {
            bt.Lines.Add(BleVoiceLink.GetDefaultRadioInfo());
            bt.State = CheckState.Ok;
        } catch (Exception ex) {
            bt.State = CheckState.Fail;
            bt.Lines.Add(ex.Message);
        }
        items.Add(bt);

        // Paired and connected are separate checks, based on the same system snapshot.
        var report = BleVoiceLink.ReadRemoteConnection(app == null ? Config.Load() : app.Config);
        items.Add(PairingCheck(report));
        items.Add(ConnectionCheck(report, RuntimeState(app)));

        // 3. VB-CABLE render side
        var render = new CheckItem { Title = "虚拟声卡（播放端）" };
        var renderNames = AudioOut.ListRenderDevices();
        bool cableRender = false;
        foreach (string n in renderNames) if (n != null && n.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0) cableRender = true;
        render.State = renderNames.Count == 0 ? CheckState.Warn : (cableRender ? CheckState.Ok : CheckState.Fail);
        if (!cableRender) render.Lines.Add("未找到 CABLE 播放端 — 请安装 VB-CABLE（setup\\install-vbcable.cmd）");
        else render.Lines.Add("CABLE Input 就绪");
        if (renderNames.Count == 0) render.Lines.Add("系统报告 0 个播放设备，音频栈异常（尝试重启音频服务或重启电脑）");
        items.Add(render);

        // 4. VB-CABLE capture side
        var capture = new CheckItem { Title = "虚拟声卡（录音端）" };
        bool cableCapture = false;
        try {
            foreach (var ep in DeviceSwitcher.ListCaptureEndpoints())
                if (ep.Name != null && ep.Name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0) cableCapture = true;
            capture.Lines.Add("当前默认麦克风: " + DeviceSwitcher.CurrentDefaultCaptureName());
        } catch (Exception ex) {
            capture.Lines.Add("枚举失败: " + ex.Message);
        }
        capture.State = cableCapture ? CheckState.Ok : CheckState.Fail;
        if (!cableCapture) capture.Lines.Add("未找到 CABLE 录音端（与播放端同一驱动，缺一不可）");
        items.Add(capture);

        // 5. WeType
        var wetype = new CheckItem { Title = "微信输入法（可选）", Required = false };
        bool wt = Process.GetProcessesByName("wetype").Length > 0 ||
                  Process.GetProcessesByName("wetype_server").Length > 0;
        wetype.State = wt ? CheckState.Ok : CheckState.Warn;
        wetype.Lines.Add(wt ? "正在运行" : "未检测到进程 — 可从 https://z.weixin.com/qqjm 安装，或改用 Win+H 语音键入");
        items.Add(wetype);

        return items;
    }

    public static UiState.Snapshot? RuntimeState(App app) {
        return app != null && app.IsRunning ? (UiState.Snapshot?)UiState.Take() : null;
    }

    public static CheckItem PairingCheck(RemoteConnectionReport report) {
        var item = new CheckItem { Id = "remote-pairing", Title = "已配对的遥控器" };
        if (report.Error != null) {
            item.State = CheckState.Warn;
            item.Lines.Add("配对状态读取失败：" + report.Error);
        } else if (report.Devices.Count == 0) {
            item.State = CheckState.Fail;
            item.Lines.Add("未找到。长按遥控器 [主页+菜单]，在系统蓝牙设置中添加。");
        } else {
            item.State = CheckState.Ok;
            foreach (var device in report.Devices) item.Lines.Add(device.Name);
            item.Lines.Add("已配对表示保存了配对记录，当前是否连通请看下一项。");
        }
        return item;
    }

    public static CheckItem ConnectionCheck(RemoteConnectionReport report, UiState.Snapshot? runtime) {
        var item = new CheckItem { Id = "remote-connection", Title = "遥控器连接状态", RemoteReport = report };
        RemoteDeviceStatus connected = null;
        bool unknown = false;
        foreach (var device in report.Devices) {
            if (device.Connected == true) connected = device;
            if (!device.Connected.HasValue) unknown = true;
        }
        if (report.Error != null) {
            item.State = CheckState.Warn;
            item.Lines.Add("蓝牙连接状态读取失败：" + report.Error);
        } else if (connected != null) {
            item.State = runtime.HasValue && !runtime.Value.Linked ? CheckState.Warn : CheckState.Ok;
            item.Lines.Add("蓝牙：已连接 · " + connected.Name);
        } else if (report.Devices.Count == 0) {
            item.State = CheckState.Fail;
            item.Lines.Add("蓝牙：未找到已配对的目标遥控器");
        } else if (unknown) {
            item.State = CheckState.Warn;
            item.Lines.Add("蓝牙：系统未提供连接状态，暂时无法确认");
        } else {
            item.State = CheckState.Fail;
            item.Lines.Add("蓝牙：未连接（已配对）");
        }
        if (runtime.HasValue) {
            item.Lines.Add(runtime.Value.Linked ? "语音通道：已就绪（程序状态）" : "语音通道：未就绪 · " + runtime.Value.Status);
            if (runtime.Value.Linked && connected == null && report.Error == null && !unknown) {
                item.State = CheckState.Warn;
                item.Lines.Add("系统与程序的连接状态不一致，等待下一次刷新确认。");
            }
        } else {
            item.Lines.Add("语音通道：请在运行中的主程序确认是否就绪");
        }
        if (connected == null && report.Error == null && !unknown && report.Devices.Count > 0 && (!runtime.HasValue || !runtime.Value.Linked))
            item.Lines.Add("按一下遥控器方向键唤醒，稍等或在主界面点击“重新连接”。");
        return item;
    }
}

class DiagPage : MacPage {
    List<CheckItem> results;
    bool checkRunning, connectionRunning;
    MacButton rerunBtn;
    readonly Font titleFont;
    readonly DiagResultsView resultView;
    readonly System.Windows.Forms.Timer connectionTimer;

    public DiagPage(App app) : base(app) {
        titleFont = MacTheme.Font(14.5f, FontStyle.Bold);
        rerunBtn = new MacButton("重新检测", false, false) { Width = MacTheme.S(110) };
        rerunBtn.Clicked += delegate { RunChecks(); };
        Controls.Add(rerunBtn);
        resultView = new DiagResultsView();
        Controls.Add(resultView);
        connectionTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        connectionTimer.Tick += delegate { if (Visible && App.IsRunning) RefreshConnection(); };
        connectionTimer.Start();
    }

    public override void OnActivated() {
        if (results == null) RunChecks();
        else { RefreshRuntime(); RefreshConnection(); }
    }

    public override void OnSnapshot(UiState.Snapshot s) { RefreshRuntime(); }

    /// True while the background check thread has not delivered results yet.
    public bool IsBusy {
        get {
            return results == null || checkRunning;
        }
    }

    async void RunChecks() {
        if (checkRunning || connectionRunning) return;
        checkRunning = true;
        rerunBtn.Enabled = false;
        results = new List<CheckItem> { new CheckItem { Title = "检测中…", State = CheckState.Info } };
        resultView.SetItems(results);
        try {
            var checkedItems = await Task.Run(delegate { return EnvChecks.Run(App); });
            if (IsDisposed) return;
            results = checkedItems;
            RefreshRuntime();
            resultView.SetItems(results);
        } catch (Exception ex) {
            if (IsDisposed) return;
            results = new List<CheckItem> { new CheckItem { Title = "检测失败", State = CheckState.Fail, Lines = new List<string> { ex.Message } } };
            resultView.SetItems(results);
        } finally {
            checkRunning = false;
            if (!IsDisposed) rerunBtn.Enabled = true;
        }
    }

    async void RefreshConnection() {
        if (results == null || checkRunning || connectionRunning || IsDisposed) return;
        connectionRunning = true;
        rerunBtn.Enabled = false;
        try {
            var report = await Task.Run(delegate { return BleVoiceLink.ReadRemoteConnection(App.Config); });
            if (IsDisposed) return;
            for (int i = 0; i < results.Count; i++) {
                if (results[i].Id == "remote-pairing") results[i] = EnvChecks.PairingCheck(report);
                if (results[i].Id == "remote-connection") results[i] = EnvChecks.ConnectionCheck(report, EnvChecks.RuntimeState(App));
            }
            resultView.SetItems(results);
        } catch (Exception ex) {
            Log.Warn("[DIAG] connection refresh: " + ex.Message);
        } finally {
            connectionRunning = false;
            if (!IsDisposed) rerunBtn.Enabled = true;
        }
    }

    void RefreshRuntime() {
        if (results == null) return;
        for (int i = 0; i < results.Count; i++) if (results[i].RemoteReport != null) {
            var updated = EnvChecks.ConnectionCheck(results[i].RemoteReport, EnvChecks.RuntimeState(App));
            if (updated.State != results[i].State || string.Join("\n", updated.Lines) != string.Join("\n", results[i].Lines)) {
                results[i] = updated;
                resultView.SetItems(results);
            }
            break;
        }
    }

    protected override void Dispose(bool disposing) {
        if (disposing) { connectionTimer.Dispose(); titleFont.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs e) {
        base.OnResize(e);
        if (rerunBtn == null || resultView == null) return;
        rerunBtn.Location = new Point(Width - MacTheme.S(26) - rerunBtn.Width, MacTheme.S(20));
        resultView.Bounds = new Rectangle(0, MacTheme.S(82), Width, Math.Max(0, Height - MacTheme.S(82)));
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        Gfx.Text(g, "环境自检", titleFont, MacTheme.TextPrimary,
            new RectangleF(MacTheme.S(26), MacTheme.S(22), Width, MacTheme.S(28)), StringAlignment.Near);
        using (var f = MacTheme.Font(8.5f))
            Gfx.Text(g, "前五项就绪后可使用语音输入 · 连接状态每 3 秒刷新", f, MacTheme.TextTertiary,
                new RectangleF(MacTheme.S(26), MacTheme.S(56), Width - MacTheme.S(52), MacTheme.S(16)), StringAlignment.Near);
    }
}

// Scroll the result cards while keeping the heading and retry button in place.
class DiagResultsView : ScrollableControl {
    List<CheckItem> items;
    readonly Font lineFont = MacTheme.Font(9f);
    readonly Font cardTitleFont = MacTheme.Font(10.5f, FontStyle.Bold);
    const TextFormatFlags LineFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;

    public DiagResultsView() {
        AutoScroll = true;
        BackColor = MacTheme.ContentBg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    public void SetItems(List<CheckItem> value) { items = value; ResizeContent(); Invalidate(); }

    int LineHeight(string text, int width) {
        return Math.Max(MacTheme.S(17), TextRenderer.MeasureText(text, lineFont, new Size(Math.Max(1, width), int.MaxValue), LineFlags).Height);
    }

    int CardHeight(CheckItem item, int width) {
        int height = MacTheme.S(48);
        foreach (string line in item.Lines) height += LineHeight(line, width - MacTheme.S(70));
        return height;
    }

    void ResizeContent() {
        if (items == null) return;
        // Reserve scrollbar width when measuring, so wrapping is stable as it appears/disappears.
        int width = Math.Max(1, Width - SystemInformation.VerticalScrollBarWidth - MacTheme.S(52));
        int height = MacTheme.S(6);
        foreach (var item in items) height += CardHeight(item, width) + MacTheme.S(12);
        AutoScrollMinSize = new Size(0, height + MacTheme.S(8));
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); ResizeContent(); Invalidate(); }
    protected override void OnScroll(ScrollEventArgs e) { base.OnScroll(e); Invalidate(); }
    protected override void Dispose(bool disposing) {
        if (disposing) { lineFont.Dispose(); cardTitleFont.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        if (items == null) return;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        int x = MacTheme.S(26), y = MacTheme.S(6) + AutoScrollPosition.Y;
        int w = Math.Max(1, Width - SystemInformation.VerticalScrollBarWidth - x * 2);

        foreach (CheckItem item in items) {
            int h = CardHeight(item, w);
            var card = new Rectangle(x, y, w, h);
            using (var path = Gfx.RoundRect(card, MacTheme.S(10))) {
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                using (var pen = new Pen(MacTheme.CardBorder)) g.DrawPath(pen, path);
            }
            Color c = item.State == CheckState.Ok ? MacTheme.Green
                    : item.State == CheckState.Warn ? MacTheme.Yellow
                    : item.State == CheckState.Fail ? MacTheme.Red : MacTheme.TextTertiary;
            int gx = card.X + MacTheme.S(16), gy = card.Y + MacTheme.S(14), d = MacTheme.S(16);
            using (var b = new SolidBrush(c)) g.FillEllipse(b, gx, gy, d, d);
            using (var f = MacTheme.Font(7.5f, FontStyle.Bold))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var brush = new SolidBrush(Color.White)) {
                string glyph = item.State == CheckState.Ok ? "✓" : item.State == CheckState.Fail ? "✕" : "!";
                g.DrawString(glyph, f, brush, new RectangleF(gx, gy - MacTheme.Scale, d, d), sf);
            }
            Gfx.Text(g, item.Title, cardTitleFont, MacTheme.TextPrimary,
                new RectangleF(card.X + MacTheme.S(44), card.Y + MacTheme.S(12), card.Width - MacTheme.S(60), MacTheme.S(18)), StringAlignment.Near);
            int ly = card.Y + MacTheme.S(40);
            foreach (string ln in item.Lines) {
                int lineHeight = LineHeight(ln, card.Width - MacTheme.S(70));
                TextRenderer.DrawText(g, ln, lineFont,
                    new Rectangle(card.X + MacTheme.S(44), ly, card.Width - MacTheme.S(70), lineHeight), MacTheme.TextSecondary, LineFlags);
                ly += lineHeight;
            }
            y += h + MacTheme.S(12);
        }
    }
}

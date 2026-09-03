// DiagPage.cs - 环境自检 page + shared EnvChecks used by both the page and
// the --check CLI mode.
// 中文：环境自检页 —— 与命令行 --check 共用 EnvChecks 检查逻辑
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

enum CheckState { Ok, Warn, Fail, Info }

class CheckItem {
    public string Title;
    public CheckState State;
    public List<string> Lines = new List<string>();
}

static class EnvChecks {
    public static List<CheckItem> Run() {
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

        // 2. Paired remote
        var remote = new CheckItem { Title = "已配对的遥控器" };
        bool remoteFound = false;
        try {
            var names = BleVoiceLink.ListPairedBleNames();
            var cfg = Config.Load();
            foreach (string n in names) {
                foreach (string want in cfg.deviceNames)
                    if (n.Trim().Equals(want.Trim(), StringComparison.OrdinalIgnoreCase)) remoteFound = true;
            }
            if (names.Count == 0) remote.Lines.Add("系统蓝牙里还没有任何 LE 配对设备");
            else foreach (string n in names) remote.Lines.Add(n);
            if (remoteFound) remote.State = CheckState.Ok;
            else {
                remote.State = CheckState.Fail;
                remote.Lines.Add("未找到。长按遥控器 [主页+菜单] 进入配对模式，在系统蓝牙设置中添加。");
            }
        } catch (Exception ex) {
            remote.State = CheckState.Fail;
            remote.Lines.Add("枚举失败: " + ex.Message);
        }
        items.Add(remote);

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
        var wetype = new CheckItem { Title = "微信输入法（可选）" };
        bool wt = Process.GetProcessesByName("wetype").Length > 0 ||
                  Process.GetProcessesByName("wetype_server").Length > 0;
        wetype.State = wt ? CheckState.Ok : CheckState.Warn;
        wetype.Lines.Add(wt ? "正在运行" : "未检测到进程 — 可从 https://z.weixin.com/qqjm 安装，或改用 Win+H 语音键入");
        items.Add(wetype);

        return items;
    }
}

class DiagPage : MacPage {
    List<CheckItem> results;
    readonly object runGate = new object();
    MacButton rerunBtn;
    readonly Font titleFont;

    public DiagPage(App app) : base(app) {
        titleFont = MacTheme.Font(14.5f, FontStyle.Bold);
        rerunBtn = new MacButton("重新检测", false, false) { Width = MacTheme.S(110) };
        rerunBtn.Clicked += delegate { RunChecks(); };
        Controls.Add(rerunBtn);
    }

    public override void OnActivated() { if (results == null) RunChecks(); }

    /// True while the background check thread has not delivered results yet.
    public bool IsBusy {
        get {
            var r = results;
            return r == null || (r.Count == 1 && r[0].Title == "检测中…");
        }
    }

    void RunChecks() {
        if (!Monitor.TryEnter(runGate)) return;
        try {
            results = new List<CheckItem>();
            results.Add(new CheckItem { Title = "检测中…", State = CheckState.Info });
            Invalidate();
            var t = new Thread((ThreadStart)delegate {
                List<CheckItem> r = null;
                try { r = EnvChecks.Run(); } catch { }
                try {
                    BeginInvoke((MethodInvoker)delegate {
                        results = r ?? new List<CheckItem>();
                        Invalidate();
                    });
                } catch { }
            }) { IsBackground = true };
            t.Start();
        } finally { Monitor.Exit(runGate); }
    }

    protected override void OnResize(EventArgs e) {
        base.OnResize(e);
        rerunBtn.Location = new Point(Width - MacTheme.S(26) - rerunBtn.Width, MacTheme.S(20));
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        Gfx.Text(g, "环境自检", titleFont, MacTheme.TextPrimary,
            new RectangleF(MacTheme.S(26), MacTheme.S(22), Width, MacTheme.S(28)), StringAlignment.Near);
        Gfx.Text(g, "使用语音输入前，确保前四项全部就绪", MacTheme.Font(8.5f), MacTheme.TextTertiary,
            new RectangleF(MacTheme.S(26), MacTheme.S(56), Width - MacTheme.S(200), MacTheme.S(16)), StringAlignment.Near);

        if (results == null) return;
        int x = MacTheme.S(26), y = MacTheme.S(88);
        int w = Width - x * 2;
        using (var titleF = MacTheme.Font(10.5f, FontStyle.Bold))
        using (var lineF = MacTheme.Font(9f)) {
            foreach (CheckItem item in results) {
                int linesH = 0;
                foreach (string ln in item.Lines) linesH += MacTheme.S(17);
                int h = MacTheme.S(48) + linesH;
                var card = new Rectangle(x, y, w, h);
                using (var path = Gfx.RoundRect(card, MacTheme.S(10))) {
                    using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                    using (var pen = new Pen(MacTheme.CardBorder)) g.DrawPath(pen, path);
                }
                // state glyph
                Color c = item.State == CheckState.Ok ? MacTheme.Green
                        : item.State == CheckState.Warn ? MacTheme.Yellow
                        : item.State == CheckState.Fail ? MacTheme.Red : MacTheme.TextTertiary;
                int gx = card.X + MacTheme.S(16), gy = card.Y + MacTheme.S(14), d = MacTheme.S(16);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, gx, gy, d, d);
                using (var f = new Font(MacTheme.Family, MacTheme.F(7.5f), FontStyle.Bold)) {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    string glyph = item.State == CheckState.Ok ? "✓" : item.State == CheckState.Fail ? "✕" : "!";
                    g.DrawString(glyph, f, new SolidBrush(Color.White), new RectangleF(gx, gy - MacTheme.Scale, d, d), sf);
                }
                Gfx.Text(g, item.Title, titleF, MacTheme.TextPrimary,
                    new RectangleF(card.X + MacTheme.S(44), card.Y + MacTheme.S(12), card.Width - MacTheme.S(60), MacTheme.S(18)), StringAlignment.Near);
                int ly = card.Y + MacTheme.S(40);
                foreach (string ln in item.Lines) {
                    Gfx.Text(g, ln, lineF, MacTheme.TextSecondary,
                        new RectangleF(card.X + MacTheme.S(44), ly, card.Width - MacTheme.S(70), MacTheme.S(17)), StringAlignment.Near);
                    ly += MacTheme.S(17);
                }
                y += h + MacTheme.S(12);
            }
        }
    }
}

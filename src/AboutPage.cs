// AboutPage.cs - 关于: version, credits & licenses, config/log file shortcuts,
// live log tail.
// 中文：关于页 —— 版本、致谢、配置/日志入口、运行日志实时尾部
using System;
using System.Drawing;
using System.Windows.Forms;

class AboutPage : MacPage {
    readonly Font titleFont;
    TextBox logBox;
    Timer logTimer;
    MacButton openCfgBtn, openLogBtn;

    public AboutPage(App app) : base(app) {
        titleFont = MacTheme.Font(14.5f, FontStyle.Bold);

        openCfgBtn = new MacButton("打开 config.json", false, true) { Width = MacTheme.S(152) };
        openCfgBtn.Clicked += delegate { TryStart("notepad.exe", Config.ConfigPath); };
        openLogBtn = new MacButton("打开日志文件", false, true) { Width = MacTheme.S(126) };
        openLogBtn.Clicked += delegate { TryStart("explorer.exe", "/select," + LogPath()); };

        logBox = new TextBox {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None, BackColor = Color.White,
            Font = new Font("Consolas", 8.25f), WordWrap = false
        };
        Controls.Add(logBox);
        Controls.Add(openCfgBtn);
        Controls.Add(openLogBtn);

        logTimer = new Timer { Interval = 800 };
        logTimer.Tick += delegate { RefreshLog(); };
        logTimer.Start();
        RefreshLog();
    }

    static string LogPath() { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MiVoiceMic.log"); }

    static void TryStart(string exe, string args) {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("[UI] start: " + ex.Message); }
    }

    void RefreshLog() {
        try {
            var lines = Log.Tail(200);
            string text = string.Join(Environment.NewLine, lines.ToArray());
            if (logBox.Text != text) { logBox.Text = text; logBox.SelectionStart = text.Length; logBox.ScrollToCaret(); }
        } catch { }
    }

    // layout bands (96dpi logical): 70-202 identity, 214-310 credits, 322-374 files, 386+ log
    Rectangle IdCardRect { get { return new Rectangle(MacTheme.S(26), MacTheme.S(70), Width - MacTheme.S(52), MacTheme.S(132)); } }
    Rectangle CrCardRect { get { return new Rectangle(MacTheme.S(26), MacTheme.S(214), Width - MacTheme.S(52), MacTheme.S(96)); } }
    Rectangle FCardRect { get { return new Rectangle(MacTheme.S(26), MacTheme.S(322), Width - MacTheme.S(52), MacTheme.S(52)); } }
    Rectangle LogRect { get { return new Rectangle(MacTheme.S(26), MacTheme.S(386), Width - MacTheme.S(52), Height - MacTheme.S(386) - MacTheme.S(20)); } }

    protected override void OnResize(EventArgs e) {
        base.OnResize(e);
        openCfgBtn.Location = new Point(Width - MacTheme.S(26) - openCfgBtn.Width - MacTheme.S(12) - openLogBtn.Width, MacTheme.S(332));
        openLogBtn.Location = new Point(Width - MacTheme.S(26) - openLogBtn.Width, MacTheme.S(332));
        var logRect = LogRect;
        logBox.Bounds = new Rectangle(logRect.X + MacTheme.S(10), logRect.Y + MacTheme.S(36), logRect.Width - MacTheme.S(20), Math.Max(0, logRect.Height - MacTheme.S(46)));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        Gfx.Text(g, "关于", titleFont, MacTheme.TextPrimary,
            new RectangleF(MacTheme.S(26), MacTheme.S(22), Width, MacTheme.S(28)), StringAlignment.Near);

        int x = MacTheme.S(26), w = Width - x * 2;

        // identity card
        var idCard = IdCardRect;
        DrawCard(g, idCard);
        // logo
        var logo = new Rectangle(idCard.X + MacTheme.S(20), idCard.Y + (idCard.Height - MacTheme.S(56)) / 2, MacTheme.S(56), MacTheme.S(56));
        using (var path = Gfx.RoundRect(logo, MacTheme.S(14))) {
            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(logo, Color.FromArgb(0x4C, 0x8D, 0xFF), MacTheme.Accent, 45f))
                g.FillPath(lg, path);
        }
        using (var f = new Font(MacTheme.Family, MacTheme.F(20f), FontStyle.Bold)) {
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("麦", f, new SolidBrush(Color.White), logo, sf);
        }
        float tx = logo.Right + MacTheme.S(18);
        Gfx.Text(g, "MiVoiceMic", MacTheme.Font(12.5f, FontStyle.Bold), MacTheme.TextPrimary,
            new RectangleF(tx, idCard.Y + MacTheme.S(20), MacTheme.S(300), MacTheme.S(22)), StringAlignment.Near);
        Gfx.Text(g, "把小米蓝牙遥控器 2 Pro 变成 Windows 无线麦克风 — 按住语音键说话，松开上屏", MacTheme.Font(9f), MacTheme.TextSecondary,
            new RectangleF(tx, idCard.Y + MacTheme.S(56), idCard.Width - MacTheme.S(240), MacTheme.S(18)), StringAlignment.Near);
        Gfx.Text(g, "版本 1.0 · GPL-3.0 · 仅供学习交流，与小米/腾讯无关", MacTheme.Font(8.5f), MacTheme.TextTertiary,
            new RectangleF(tx, idCard.Y + MacTheme.S(86), idCard.Width - MacTheme.S(240), MacTheme.S(16)), StringAlignment.Near);

        // credits card
        var crCard = CrCardRect;
        DrawCard(g, crCard);
        using (var f = MacTheme.Font(10.5f, FontStyle.Bold))
            Gfx.Text(g, "致谢", f, MacTheme.TextPrimary,
                new RectangleF(crCard.X + MacTheme.S(18), crCard.Y + MacTheme.S(12), crCard.Width, MacTheme.S(16)), StringAlignment.Near);
        string[] credits = {
            "macOS 端原版  HD838A/remote-mic-app（无线麦）",
            "Windows 先行实现与 ATVV 逆向  QL-4/RemoteMapper",
            "虚拟声卡  VB-CABLE © VB-Audio",
        };
        int cy = crCard.Y + MacTheme.S(36);
        foreach (string c in credits) {
            Gfx.Text(g, c, MacTheme.Font(9f), MacTheme.TextSecondary,
                new RectangleF(crCard.X + MacTheme.S(18), cy, crCard.Width - MacTheme.S(36), MacTheme.S(15)), StringAlignment.Near);
            cy += MacTheme.S(17);
        }

        // files card
        var fCard = FCardRect;
        DrawCard(g, fCard);
        Gfx.Text(g, "配置  " + Config.ConfigPath, MacTheme.Font(8.5f), MacTheme.TextSecondary,
            new RectangleF(fCard.X + MacTheme.S(18), fCard.Y + MacTheme.S(8), fCard.Width - MacTheme.S(280), MacTheme.S(16)), StringAlignment.Near);
        Gfx.Text(g, "日志  " + LogPath(), MacTheme.Font(8.5f), MacTheme.TextSecondary,
            new RectangleF(fCard.X + MacTheme.S(18), fCard.Y + MacTheme.S(28), fCard.Width - MacTheme.S(280), MacTheme.S(16)), StringAlignment.Near);

        // log card frame
        var logRect = LogRect;
        DrawCard(g, logRect);
        using (var f = MacTheme.Font(10.5f, FontStyle.Bold))
            Gfx.Text(g, "运行日志（最近 200 行）", f, MacTheme.TextPrimary,
                new RectangleF(logRect.X + MacTheme.S(18), logRect.Y + MacTheme.S(10), MacTheme.S(300), MacTheme.S(16)), StringAlignment.Near);
    }

    static void DrawCard(Graphics g, Rectangle r) {
        using (var path = Gfx.RoundRect(r, MacTheme.S(10))) {
            using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
            using (var pen = new Pen(MacTheme.CardBorder)) g.DrawPath(pen, path);
        }
    }
}

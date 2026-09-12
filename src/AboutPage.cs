// AboutPage.cs - 关于: version, credits & licenses, config/log file shortcuts,
// live log tail.
// 中文：关于页 —— 版本、致谢、配置/日志入口、运行日志实时尾部
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;

class AboutPage : MacPage {
    readonly Font titleFont;
    TextBox logBox;
    System.Windows.Forms.Timer logTimer;
    MacButton openCfgBtn, openLogBtn;
    MacButton checkUpdateBtn, installUpdateBtn, cancelUpdateBtn;
    Label updateStatus;
    ProgressBar updateProgress;
    ToolTip updateTip;
    CancellationTokenSource updateCancellation;
    AppRelease availableRelease;
    bool checkedOnce, updateBusy;

    public AboutPage(App app) : base(app) {
        titleFont = MacTheme.Font(14.5f, FontStyle.Bold);

        checkUpdateBtn = new MacButton("检查更新", false, false) { Width = MacTheme.S(100) };
        checkUpdateBtn.Clicked += delegate { CheckForUpdates(); };
        installUpdateBtn = new MacButton("下载并更新", true, false) { Width = MacTheme.S(116), Visible = false };
        installUpdateBtn.Clicked += delegate { InstallUpdate(); };
        cancelUpdateBtn = new MacButton("取消", false, false) { Width = MacTheme.S(70), Visible = false };
        cancelUpdateBtn.Clicked += delegate { if (updateCancellation != null) updateCancellation.Cancel(); };
        updateStatus = new Label { Text = "打开此页时自动检查 GitHub 最新正式版本", AutoEllipsis = true,
            BackColor = Color.White, ForeColor = MacTheme.TextSecondary, Font = MacTheme.Font(9f) };
        updateProgress = new ProgressBar { Minimum = 0, Maximum = 100, Visible = false };
        updateTip = new ToolTip();
        Controls.Add(checkUpdateBtn);
        Controls.Add(installUpdateBtn);
        Controls.Add(cancelUpdateBtn);
        Controls.Add(updateStatus);
        Controls.Add(updateProgress);

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

        logTimer = new System.Windows.Forms.Timer { Interval = 800 };
        logTimer.Tick += delegate { RefreshLog(); };
        logTimer.Start();
        RefreshLog();
    }

    public override void OnActivated() {
        if (!checkedOnce && AppUpdater.Interactive) { checkedOnce = true; CheckForUpdates(); }
    }

    void UpdateStatus(string text, bool error) {
        if (IsDisposed) return;
        updateStatus.Text = text;
        updateStatus.ForeColor = error ? MacTheme.Red : MacTheme.TextSecondary;
        updateTip.SetToolTip(updateStatus, text);
    }

    void SetUpdateBusy(bool busy) {
        updateBusy = busy;
        checkUpdateBtn.Enabled = !busy;
        checkUpdateBtn.Caption = busy ? "请稍候…" : "检查更新";
        checkUpdateBtn.Invalidate();
        installUpdateBtn.Visible = !busy && availableRelease != null;
        cancelUpdateBtn.Visible = busy;
        Relayout();
    }

    async void CheckForUpdates() {
        if (updateBusy) return;
        availableRelease = null;
        var cancellation = new CancellationTokenSource();
        updateCancellation = cancellation;
        SetUpdateBusy(true);
        UpdateStatus("正在连接 GitHub，检查最新版本…", false);
        try {
            var release = await Task.Run(delegate { return AppUpdater.Check(cancellation.Token); });
            if (IsDisposed) return;
            cancellation.Token.ThrowIfCancellationRequested();
            if (release.IsNewer) {
                availableRelease = release;
                UpdateStatus("发现 " + release.Tag + " · 下载后自动重启，保留当前配置", false);
            } else {
                UpdateStatus("当前已是最新版本（本机 " + AppVersion.Number + "，GitHub " + release.Tag + "）", false);
            }
        } catch (Exception ex) {
            UpdateStatus(AppUpdater.FriendlyError(ex), !(ex is OperationCanceledException));
            Log.Warn("[UPDATE] check: " + ex.Message);
        } finally {
            updateCancellation = null;
            cancellation.Dispose();
            if (!IsDisposed) SetUpdateBusy(false);
        }
    }

    async void InstallUpdate() {
        if (updateBusy || availableRelease == null) return;
        var release = availableRelease;
        var cancellation = new CancellationTokenSource();
        updateCancellation = cancellation;
        SetUpdateBusy(true);
        updateProgress.Value = 0;
        updateProgress.Visible = true;
        UpdateStatus("正在下载 " + release.Tag + "…", false);
        string stage = null;
        bool helperStarted = false;
        try {
            // Progress<T> posts onto the UI context; network/disk work never blocks voice input.
            int lastPercent = -1;
            var progress = new Progress<int>(delegate(int percent) {
                if (IsDisposed || !updateBusy) return;
                updateProgress.Value = percent;
                UpdateStatus("正在下载 " + release.Tag + " · " + percent + "%", false);
            });
            stage = await Task.Run(delegate {
                return AppUpdater.Prepare(release, delegate(int percent) {
                    if (percent != lastPercent) { lastPercent = percent; ((IProgress<int>)progress).Report(percent); }
                }, cancellation.Token);
            });
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsDisposed) return;
            UpdateStatus("校验通过，正在准备更新…", false);
            cancelUpdateBtn.Visible = false;
            await Task.Run(delegate { AppUpdater.StartHelper(stage, cancellation.Token); });
            helperStarted = true;
            if (IsDisposed) { System.IO.File.WriteAllText(System.IO.Path.Combine(stage, "cancel"), "cancel"); return; }
            UpdateStatus("正在重启，更新后将自动重新连接遥控器…", false);
            Log.Info("[UPDATE] applying " + release.Tag);
            TrayIcon.ExitApplication();
        } catch (Exception ex) {
            UpdateStatus(AppUpdater.FriendlyError(ex), !(ex is OperationCanceledException));
            Log.Warn("[UPDATE] install: " + ex.Message);
        } finally {
            if (stage != null && !helperStarted)
                AppUpdater.CleanupStage(stage, System.IO.Path.GetDirectoryName(Application.ExecutablePath));
            updateCancellation = null;
            cancellation.Dispose();
            if (!IsDisposed) { updateProgress.Visible = false; SetUpdateBusy(false); }
        }
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            if (updateCancellation != null) updateCancellation.Cancel();
            if (logTimer != null) logTimer.Dispose();
            if (updateTip != null) updateTip.Dispose();
            titleFont.Dispose();
        }
        base.Dispose(disposing);
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

    // Separate update card keeps status/progress readable at the minimum window size.
    Rectangle IdCardRect { get { return new Rectangle(MacTheme.S(26), MacTheme.S(70), Width - MacTheme.S(52), MacTheme.S(132)); } }
    Rectangle UpdateCardRect { get { return new Rectangle(MacTheme.S(26), MacTheme.S(214), Width - MacTheme.S(52), MacTheme.S(96)); } }
    Rectangle CrCardRect { get { return new Rectangle(MacTheme.S(26), MacTheme.S(322), Width - MacTheme.S(52), MacTheme.S(96)); } }
    Rectangle FCardRect { get { return new Rectangle(MacTheme.S(26), MacTheme.S(430), Width - MacTheme.S(52), MacTheme.S(52)); } }
    Rectangle LogRect { get { return new Rectangle(MacTheme.S(26), MacTheme.S(494), Width - MacTheme.S(52), Height - MacTheme.S(494) - MacTheme.S(20)); } }

    protected override void OnResize(EventArgs e) {
        base.OnResize(e);
        if (openCfgBtn == null) return;
        var updateRect = UpdateCardRect;
        installUpdateBtn.Location = new Point(updateRect.Right - MacTheme.S(18) - installUpdateBtn.Width, updateRect.Y + MacTheme.S(14));
        cancelUpdateBtn.Location = new Point(updateRect.Right - MacTheme.S(18) - cancelUpdateBtn.Width, installUpdateBtn.Top);
        int checkRight = updateBusy ? cancelUpdateBtn.Left - MacTheme.S(10) :
            availableRelease != null ? installUpdateBtn.Left - MacTheme.S(10) : updateRect.Right - MacTheme.S(18);
        checkUpdateBtn.Location = new Point(checkRight - checkUpdateBtn.Width, installUpdateBtn.Top);
        updateStatus.Bounds = new Rectangle(updateRect.X + MacTheme.S(18), updateRect.Y + MacTheme.S(57), updateRect.Width - MacTheme.S(36), MacTheme.S(22));
        updateProgress.Bounds = new Rectangle(updateStatus.Left, updateRect.Bottom - MacTheme.S(10), updateStatus.Width, MacTheme.S(4));
        openCfgBtn.Location = new Point(Width - MacTheme.S(26) - openCfgBtn.Width - MacTheme.S(12) - openLogBtn.Width, MacTheme.S(440));
        openLogBtn.Location = new Point(Width - MacTheme.S(26) - openLogBtn.Width, MacTheme.S(440));
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
        Gfx.Text(g, "小米蓝牙遥控器 2 Pro · 按住语音键说话，松开上屏", MacTheme.Font(9f), MacTheme.TextSecondary,
            new RectangleF(tx, idCard.Y + MacTheme.S(56), idCard.Width - MacTheme.S(240), MacTheme.S(18)), StringAlignment.Near);
        Gfx.Text(g, "版本 " + AppVersion.Number + " · GPL-3.0 · 仅供学习交流，与小米/腾讯无关", MacTheme.Font(8.5f), MacTheme.TextTertiary,
            new RectangleF(tx, idCard.Y + MacTheme.S(86), idCard.Width - MacTheme.S(240), MacTheme.S(16)), StringAlignment.Near);

        var updateCard = UpdateCardRect;
        DrawCard(g, updateCard);
        using (var f = MacTheme.Font(10.5f, FontStyle.Bold))
            Gfx.Text(g, "软件更新", f, MacTheme.TextPrimary,
                new RectangleF(updateCard.X + MacTheme.S(18), updateCard.Y + MacTheme.S(20), MacTheme.S(160), MacTheme.S(22)), StringAlignment.Near);

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
        using (var f = MacTheme.Font(8.5f)) {
            TextRenderer.DrawText(g, "配置  " + Config.ConfigPath, f,
                new Rectangle(fCard.X + MacTheme.S(18), fCard.Y + MacTheme.S(8), Math.Max(0, openCfgBtn.Left - fCard.X - MacTheme.S(30)), MacTheme.S(16)),
                MacTheme.TextSecondary, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "日志  " + LogPath(), f,
                new Rectangle(fCard.X + MacTheme.S(18), fCard.Y + MacTheme.S(28), Math.Max(0, openCfgBtn.Left - fCard.X - MacTheme.S(30)), MacTheme.S(16)),
                MacTheme.TextSecondary, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

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

// KeyMapEditor.cs - modal dialog editing one key's click/hold mappings.
// Control visibility follows the SELECTED action kind (not whether a payload
// exists yet - otherwise the combo box would never appear to type into).
// 中文：按键映射编辑对话框 —— 可见性跟随所选动作类型，组合键为焦点式捕获
using System;
using System.Drawing;
using System.Windows.Forms;

class MacTextBox : Panel {
    public readonly TextBox Box = new TextBox();

    public MacTextBox(string text) {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = MacTheme.S(30);
        BackColor = Color.White;
        Box.BorderStyle = BorderStyle.None;
        Box.Font = MacTheme.Font(9.75f);
        Box.Text = text ?? "";
        Controls.Add(Box);
    }

    public string Text_ { get { return Box.Text; } set { Box.Text = value ?? ""; } }

    protected override void OnLayout(LayoutEventArgs e) {
        base.OnLayout(e);
        Box.Bounds = new Rectangle(MacTheme.S(10), MacTheme.S(6), Width - MacTheme.S(20), MacTheme.S(20));
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Gfx.RoundRect(r, MacTheme.S(7))) {
            using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
            using (var pen = new Pen(Color.FromArgb(208, 208, 214))) g.DrawPath(pen, path);
        }
    }
}

class KeyMapEditor : Form {
    readonly KeyMapEntry src;
    readonly bool mappingWasEnabled;
    public KeyMapAction ResultClick;
    public KeyMapAction ResultHold;
    public bool EnableMappingOnOk;

    MacSegmented gestureSeg, typeSeg, tapSeg;
    ComboCaptureBox comboBox;
    MacTextBox cmdBox, msBox;
    MacButton okBtn, cancelBtn, resetBtn;

    KeyMapAction Current { get { return gestureSeg.Selected == 0 ? ResultClick : ResultHold; } }
    KeyMapAction CurrentOrCreate {
        get {
            if (gestureSeg.Selected == 0) { if (ResultClick == null) ResultClick = new KeyMapAction(); return ResultClick; }
            if (ResultHold == null) { ResultHold = new KeyMapAction(); ResultHold.ms = 600; }
            return ResultHold;
        }
    }

    public KeyMapEditor(KeyMapEntry entry, bool mappingEnabled) {
        src = entry;
        mappingWasEnabled = mappingEnabled;
        ResultClick = entry.click != null ? entry.click.Clone() : new KeyMapAction();
        ResultHold = entry.hold != null ? entry.hold.Clone() : null;

        Text = "映射 — " + entry.name;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = MacTheme.ContentBg;
        int w = MacTheme.S(560), h = MacTheme.S(430);
        MinimumSize = new Size(w, h); MaximumSize = new Size(w, h); Size = new Size(w, h);
        ShowInTaskbar = false;
        MaximizeBox = false; MinimizeBox = false;
        DoubleBuffered = true;
        KeyPreview = true;
        Font = MacTheme.Font(9.5f);

        gestureSeg = new MacSegmented(new[] { "点按", "长按" }, 0);
        gestureSeg.Width = MacTheme.S(180);
        gestureSeg.Changed += delegate { SyncFromGesture(); RefreshUI(); };

        typeSeg = new MacSegmented(new[] { "不映射", "单键", "组合键", "任务视图", "打开应用", "运行命令" }, 0);
        typeSeg.Width = MacTheme.S(520);
        typeSeg.Changed += delegate { OnTypeChanged(); };

        comboBox = new ComboCaptureBox("") { Width = MacTheme.S(230) };
        comboBox.ValueChanged += delegate { CurrentOrCreate.keys = comboBox.Value; };

        tapSeg = new MacSegmented(new[] { "快速点按", "按住跟随" }, 1);
        tapSeg.Width = MacTheme.S(180);
        tapSeg.Changed += delegate { CurrentOrCreate.tap = tapSeg.Selected == 0; };

        cmdBox = new MacTextBox("") { Width = MacTheme.S(460) };
        msBox = new MacTextBox("600") { Width = MacTheme.S(80) };

        okBtn = new MacButton(mappingEnabled ? "好" : "好（并启用映射）", true, false) { Width = MacTheme.S(96) };
        okBtn.Clicked += delegate {
            CommitFields();
            ResultClick = ResultClick != null ? ResultClick.Sanitized() : new KeyMapAction();
            ResultHold = ResultHold != null ? ResultHold.Sanitized() : null;
            if (!mappingWasEnabled) EnableMappingOnOk = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        cancelBtn = new MacButton("取消", false, false) { Width = MacTheme.S(80) };
        cancelBtn.Clicked += delegate { DialogResult = DialogResult.Cancel; Close(); };
        resetBtn = new MacButton("清除该键映射", false, true) { Width = MacTheme.S(120) };
        resetBtn.Clicked += delegate {
            ResultClick = new KeyMapAction();
            ResultHold = null;
            SyncFromGesture();
            RefreshUI();
        };

        Controls.Add(gestureSeg); Controls.Add(typeSeg); Controls.Add(comboBox);
        Controls.Add(tapSeg); Controls.Add(cmdBox); Controls.Add(msBox);
        Controls.Add(okBtn); Controls.Add(cancelBtn); Controls.Add(resetBtn);

        SyncFromGesture();
        RefreshUI();
        LayoutX();
    }

    /// Simulates picking a gesture (also used by the screenshot harness).
    public void SelectGesture(int idx) {
        gestureSeg.Selected = idx;
        SyncFromGesture();
        RefreshUI();
        LayoutX();
    }

    /// Simulates picking an action type (also used by the screenshot harness).
    public void SelectType(int idx) {
        typeSeg.Selected = idx;
        OnTypeChanged();
        LayoutX();
    }

    void OnTypeChanged() {
        var a = CurrentOrCreate;
        MapActionKind k = KindFromIndex(typeSeg.Selected);
        a.kind = KeyMapAction.KindName(k);
        a.single = typeSeg.Selected == 1;                    // 单键 variant of combo
        if (k == MapActionKind.Combo && string.IsNullOrEmpty(a.keys)) a.tap = true;
        RefreshUI();
        LayoutX();
    }

    static MapActionKind KindFromIndex(int i) {
        switch (i) {
            case 1: return MapActionKind.Combo;              // 单键
            case 2: return MapActionKind.Combo;
            case 3: return MapActionKind.TaskView;
            case 4: return MapActionKind.Launch;
            case 5: return MapActionKind.Cmd;
            default: return MapActionKind.None;
        }
    }
    static int IndexFromKind(MapActionKind k, KeyMapAction a) {
        switch (k) {
            case MapActionKind.Combo: return a != null && a.single ? 1 : 2;
            case MapActionKind.TaskView: return 3;
            case MapActionKind.Launch: return 4;
            case MapActionKind.Cmd: return 5;
            default: return 0;
        }
    }

    void SyncFromGesture() {
        var a = Current;
        if (a == null || !a.HasPayload) typeSeg.Selected = 0;
        else typeSeg.Selected = IndexFromKind(a.Kind, a);
    }

    int ComboRowY { get { return typeSeg.Bottom + MacTheme.S(26); } }
    int HoldRowY { get { return ComboRowY + MacTheme.S(56); } }

    void RefreshUI() {
        // visibility follows the SELECTED kind - an empty combo must still show
        // the capture box, otherwise there is no way to enter one
        MapActionKind sel = KindFromIndex(typeSeg.Selected);
        bool single = typeSeg.Selected == 1;
        bool combo = sel == MapActionKind.Combo;
        bool cmd = sel == MapActionKind.Launch || sel == MapActionKind.Cmd;
        bool hold = gestureSeg.Selected == 1;

        comboBox.Visible = combo;
        tapSeg.Visible = combo && !single;                   // 单键 always taps
        cmdBox.Visible = cmd;
        msBox.Visible = hold;

        var a = Current;
        if (combo) {
            string keys = a != null ? a.keys : "";
            if (comboBox.Value != keys) comboBox.Value = keys;
            tapSeg.Selected = a != null && !a.tap ? 1 : 0;
        }
        if (cmd && a != null && cmdBox.Text_ != (a.command ?? "")) cmdBox.Text_ = a.command;
        if (hold) {
            uint ms = a != null && a.ms > 0 ? a.ms : 600;
            if (msBox.Text_.Trim() != ms.ToString()) msBox.Text_ = ms.ToString();
        }
        Invalidate();
    }

    void CommitFields() {
        var a = Current;
        if (a == null) return;
        MapActionKind sel = KindFromIndex(typeSeg.Selected);
        if (sel == MapActionKind.Combo) {
            a.keys = comboBox.Value;
            a.single = typeSeg.Selected == 1;
            if (a.single) a.tap = true;
        }
        if (sel == MapActionKind.Launch || sel == MapActionKind.Cmd) a.command = cmdBox.Text_;
        if (gestureSeg.Selected == 1) {
            uint ms;
            a.ms = uint.TryParse(msBox.Text_.Trim(), out ms) && ms >= 100 && ms <= 3000 ? ms : 600;
        }
    }

    void LayoutX() {
        int x = MacTheme.S(28);
        gestureSeg.Location = new Point(x, MacTheme.S(38));
        typeSeg.Location = new Point(x, MacTheme.S(94));

        comboBox.Location = new Point(x, ComboRowY);
        tapSeg.Location = new Point(x + MacTheme.S(250), ComboRowY);
        cmdBox.Location = new Point(x, ComboRowY);
        msBox.Location = new Point(x + MacTheme.S(130), HoldRowY);

        okBtn.Location = new Point(Width - MacTheme.S(28) - okBtn.Width, Height - MacTheme.S(56));
        cancelBtn.Location = new Point(okBtn.Left - MacTheme.S(12) - cancelBtn.Width, Height - MacTheme.S(56));
        resetBtn.Location = new Point(x, Height - MacTheme.S(54));
    }

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000;                    // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); if (gestureSeg != null) LayoutX(); }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
        if (keyData == Keys.Escape && !comboBox.Capturing) { DialogResult = DialogResult.Cancel; Close(); return true; }
        if (keyData == Keys.Enter && !comboBox.Capturing && !ContainsFocus) { okBtn.PerformClick(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var b = new SolidBrush(MacTheme.ContentBg)) g.FillRectangle(b, ClientRectangle);

        Gfx.Text(g, "手势", MacTheme.Font(8.75f), MacTheme.TextTertiary,
            new RectangleF(MacTheme.S(28), gestureSeg.Top - MacTheme.S(20), MacTheme.S(120), MacTheme.S(16)), StringAlignment.Near);
        Gfx.Text(g, "动作", MacTheme.Font(8.75f), MacTheme.TextTertiary,
            new RectangleF(MacTheme.S(28), typeSeg.Top - MacTheme.S(20), MacTheme.S(120), MacTheme.S(16)), StringAlignment.Near);

        MapActionKind sel = KindFromIndex(typeSeg.Selected);
        if (sel == MapActionKind.Combo) {
            Gfx.Text(g, typeSeg.Selected == 1
                    ? "点击捕获框后按下一个单键（如 F5、Enter）；Esc 取消"
                    : "点击捕获框后直接按下组合键（支持 Win 键）；Esc 取消",
                MacTheme.Font(8.75f), MacTheme.TextTertiary,
                new RectangleF(MacTheme.S(28), ComboRowY + comboBox.Height + MacTheme.S(6), Width - MacTheme.S(56), MacTheme.S(16)), StringAlignment.Near);
        } else if (sel == MapActionKind.Launch || sel == MapActionKind.Cmd) {
            Gfx.Text(g, sel == MapActionKind.Launch
                    ? "填写 exe 路径（可带参数），例如 notepad 或 \"C:\\\\tools\\\\app.exe\" -x"
                    : "填写要执行的命令（经 cmd /c 运行），例如 start ms-settings:sound",
                MacTheme.Font(8.75f), MacTheme.TextTertiary,
                new RectangleF(MacTheme.S(28), ComboRowY + cmdBox.Height + MacTheme.S(6), Width - MacTheme.S(56), MacTheme.S(16)), StringAlignment.Near);
        }
        if (gestureSeg.Selected == 1) {
            Gfx.Text(g, "长按阈值", MacTheme.Font(9.5f), MacTheme.TextPrimary,
                new RectangleF(MacTheme.S(28), HoldRowY + MacTheme.S(6), MacTheme.S(120), MacTheme.S(18)), StringAlignment.Near);
            Gfx.Text(g, "毫秒（100–3000）", MacTheme.Font(8.75f), MacTheme.TextTertiary,
                new RectangleF(MacTheme.S(290), HoldRowY + MacTheme.S(8), MacTheme.S(160), MacTheme.S(16)), StringAlignment.Near);
        }
        if (src.needsDriver)
            Gfx.Text(g, "提示针对此键本身（输入侧）：无驱动时 Windows 收不到它，所选动作不会触发；装驱动转为 " + src.vk + " 后自动生效",
                MacTheme.Font(8.5f), MacTheme.TextTertiary,
                new RectangleF(MacTheme.S(28), resetBtn.Top - MacTheme.S(26), Width - MacTheme.S(56), MacTheme.S(16)), StringAlignment.Near);
    }
}

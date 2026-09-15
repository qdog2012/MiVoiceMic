using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

sealed class AudioDeviceDialog : Form {
    readonly App app;
    readonly ComboBox renderBox, captureBox;
    readonly Label status;
    readonly Button refresh, apply, cancel;
    bool loading, applying;

    public AudioDeviceDialog(App app) {
        this.app = app;
        Text = "音频设备";
        Font = MacTheme.Font(9.5f);
        AutoScaleMode = AutoScaleMode.None;
        BackColor = MacTheme.ContentBg;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        ClientSize = new Size(MacTheme.S(600), MacTheme.S(410));

        AddLabel("把遥控器的声音送给输入法", 24, 20, 550, 28, MacTheme.TextPrimary, true);
        AddLabel("请选择同一条虚拟声卡的两端。设备名称以本机为准。", 24, 54, 550, 24, MacTheme.TextSecondary, false);
        AddLabel("播放端 · 程序把声音送到这里", 24, 94, 550, 24, MacTheme.TextPrimary, false);
        renderBox = AddPicker(124);
        AddLabel("通常选择 CABLE Input 或 CABLE In 16 Ch。", 24, 158, 550, 24, MacTheme.TextTertiary, false);
        AddLabel("录音端 · 输入法从这里收音", 24, 198, 550, 24, MacTheme.TextPrimary, false);
        captureBox = AddPicker(228);
        AddLabel("通常选择 CABLE Output；开启自动切麦时会切换到此设备。", 24, 262, 550, 24, MacTheme.TextTertiary, false);
        status = AddLabel("", 24, 300, 550, 44, MacTheme.TextSecondary, false);

        refresh = AddButton("刷新设备", 24, 356, 110);
        cancel = AddButton("取消", 346, 356, 100);
        apply = AddButton("应用并保存", 458, 356, 118);
        apply.BackColor = MacTheme.Accent;
        apply.ForeColor = Color.White;
        apply.EnabledChanged += delegate {
            apply.BackColor = apply.Enabled ? MacTheme.Accent : MacTheme.TrackOff;
            apply.ForeColor = apply.Enabled ? Color.White : MacTheme.TextTertiary;
        };
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
        AcceptButton = apply;
        refresh.Click += delegate { RefreshDevices(); };
        apply.Click += async delegate {
            applying = true;
            renderBox.Enabled = captureBox.Enabled = refresh.Enabled = apply.Enabled = cancel.Enabled = false;
            status.ForeColor = MacTheme.TextSecondary;
            status.Text = "正在打开所选设备…";
            try {
                string error = await app.SetAudioDevices(renderBox.SelectedItem as AudioDeviceInfo, captureBox.SelectedItem as AudioDeviceInfo);
                if (error == null) { DialogResult = DialogResult.OK; applying = false; Close(); return; }
                status.Text = error;
                status.ForeColor = MacTheme.Red;
            } catch (Exception ex) { status.Text = "应用失败：" + ex.Message; status.ForeColor = MacTheme.Red; }
            finally {
                applying = false;
                if (!IsDisposed) {
                    renderBox.Enabled = captureBox.Enabled = refresh.Enabled = cancel.Enabled = true;
                    apply.Enabled = ValidSelection();
                }
            }
        };
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (applying) e.Cancel = true; };
        RefreshDevices();
    }

    Label AddLabel(string text, int x, int y, int width, int height, Color color, bool bold) {
        var label = new Label { Text = text, ForeColor = color, AutoSize = false,
            Bounds = new Rectangle(MacTheme.S(x), MacTheme.S(y), MacTheme.S(width), MacTheme.S(height)),
            Font = bold ? MacTheme.Font(12, FontStyle.Bold) : Font };
        Controls.Add(label);
        return label;
    }

    ComboBox AddPicker(int y) {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = false,
            DropDownHeight = MacTheme.S(220), DropDownWidth = MacTheme.S(650),
            Bounds = new Rectangle(MacTheme.S(24), MacTheme.S(y), MacTheme.S(552), MacTheme.S(30)),
            Font = Font, AccessibleName = y == 124 ? "播放端" : "录音端" };
        box.SelectedIndexChanged += delegate { if (!loading) SelectionChanged(); };
        Controls.Add(box);
        return box;
    }

    Button AddButton(string text, int x, int y, int width) {
        var button = new Button { Text = text, FlatStyle = FlatStyle.Flat,
            Bounds = new Rectangle(MacTheme.S(x), MacTheme.S(y), MacTheme.S(width), MacTheme.S(32)) };
        button.FlatAppearance.BorderColor = MacTheme.CardBorder;
        Controls.Add(button);
        return button;
    }

    void RefreshDevices() {
        loading = true;
        try {
            var render = renderBox.SelectedItem as AudioDeviceInfo;
            var capture = captureBox.SelectedItem as AudioDeviceInfo;
            // Enumerate both before updating either picker; a failed refresh keeps the selection visible.
            var renders = AudioOut.ListRenderEndpoints();
            var captures = DeviceSwitcher.ListCaptureEndpoints();
            Populate(renderBox, renders, render == null ? app.Config.cableRenderName : render.Name,
                render == null ? app.Config.cableRenderId : render.Id);
            Populate(captureBox, captures, capture == null ? app.Config.cableCaptureName : capture.Name,
                capture == null ? app.Config.cableCaptureId : capture.Id);
            SelectionChanged();
        } catch (Exception ex) { status.Text = "读取设备失败：" + ex.Message; status.ForeColor = MacTheme.Red; apply.Enabled = false; }
        finally { loading = false; }
    }

    static void Populate(ComboBox box, List<AudioDeviceInfo> devices, string name, string id) {
        box.Items.Clear();
        var selected = AudioDevices.Resolve(devices, name, id);
        foreach (var device in devices) box.Items.Add(device);
        if (selected == null) {
            selected = new AudioDeviceInfo { Name = name, Id = id, Available = false };
            box.Items.Insert(0, selected);
        }
        box.SelectedItem = selected;
    }

    bool ValidSelection() {
        var render = renderBox.SelectedItem as AudioDeviceInfo;
        var capture = captureBox.SelectedItem as AudioDeviceInfo;
        return render != null && capture != null && render.Available && capture.Available;
    }

    void SelectionChanged() {
        bool valid = ValidSelection();
        apply.Enabled = valid;
        status.ForeColor = valid ? MacTheme.TextSecondary : MacTheme.Red;
        status.Text = valid ? "应用后立即生效并保存。测试音可用于观察录音端的电平。" :
            "原配置中的设备未找到或不唯一，请从列表中重新选择。";
    }
}

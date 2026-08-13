using WindexBar.Core.Forecasting;

namespace QuotaResetRadar.Windows;

public sealed class ClueDialog : Form
{
    private readonly TextBox _author = new() { Text = "@thsottiaux", Dock = DockStyle.Fill };
    private readonly TextBox _url = new() { PlaceholderText = "https://x.com/.../status/...", Dock = DockStyle.Fill };
    private readonly DateTimePicker _target = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd HH:mm",
        Value = DateTime.Now.AddDays(1),
        Dock = DockStyle.Fill
    };
    private readonly ComboBox _trust = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _note = new() { Multiline = true, Height = 70, Dock = DockStyle.Fill };

    public ClueDialog()
    {
        Text = "添加全员额度重置线索";
        Size = new Size(520, 430);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 10f);
        BackColor = Color.FromArgb(28, 27, 33);
        ForeColor = Color.White;

        _trust.Items.Add(new TrustItem("普通账号 · 50", 50));
        _trust.Items.Add(new TrustItem("长期观察者 · 70", 70));
        _trust.Items.Add(new TrustItem("官方或内部人士 · 90", 90));
        _trust.SelectedIndex = 2;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 11
        };
        AddField(root, "作者", _author);
        AddField(root, "帖子链接", _url);
        AddField(root, "帖子预计的全员重置时间（本地）", _target);
        AddField(root, "来源可信度", _trust);
        AddField(root, "线索内容", _note);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true
        };
        var add = new Button { Text = "添加", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(add);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons);
        Controls.Add(root);
        AcceptButton = add;
        CancelButton = cancel;
    }

    public QuotaCommunitySignalDraft Draft => new(
        _url.Text.Trim(),
        _author.Text.Trim(),
        _note.Text.Trim(),
        new DateTimeOffset(_target.Value),
        (_trust.SelectedItem as TrustItem)?.Reliability ?? 60);

    private static void AddField(TableLayoutPanel root, string label, Control control)
    {
        root.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 0, 3) });
        root.Controls.Add(control);
    }

    private sealed record TrustItem(string Label, int Reliability)
    {
        public override string ToString() => Label;
    }
}

using System.Runtime.InteropServices;
using WindexBar.Core.Forecasting;

namespace QuotaResetRadar.Windows;

public sealed class ClueDialog : Form
{
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _author = new() { Dock = DockStyle.Fill };
    private readonly TextBox _url = new() { PlaceholderText = "https://x.com/.../status/...", Dock = DockStyle.Fill };
    private readonly DateTimePicker _target = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd HH:mm",
        Value = DateTime.Now.AddDays(1),
        ShowCheckBox = true,
        Checked = true,
        Dock = DockStyle.Fill
    };
    private readonly ComboBox _trust = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _note = new() { Multiline = true, Height = 70, Dock = DockStyle.Fill };
    private readonly Label _recognition = new()
    {
        AutoSize = true,
        ForeColor = Color.FromArgb(177, 211, 190),
        Text = "复制搜索结果的链接或整段文字，然后点“从剪贴板识别”。"
    };

    public ClueDialog(ClueDialogPreset? preset = null, string? initialClipboardText = null)
    {
        Text = "添加全员额度重置线索";
        Size = new Size(560, 570);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 10f);
        BackColor = Color.FromArgb(28, 27, 33);
        ForeColor = Color.White;

        _kind.Items.AddRange(["X 线索", "OpenAI 官方", "OpenAI 状态", "GitHub", "Reddit 社区", "网页/新闻", "本地观察"]);
        _kind.SelectedItem = preset?.SourceKind ?? "X 线索";
        _author.Text = preset?.DefaultAuthor ?? "@thsottiaux";

        _trust.Items.Add(new TrustItem("未经验证社区信息 · 40", 40));
        _trust.Items.Add(new TrustItem("普通账号 · 50", 50));
        _trust.Items.Add(new TrustItem("长期观察者 · 70", 70));
        _trust.Items.Add(new TrustItem("可靠技术来源 · 85", 85));
        _trust.Items.Add(new TrustItem("官方或内部人士 · 90", 90));
        _trust.Items.Add(new TrustItem("官方状态或长期高命中 · 95", 95));
        SelectTrust(preset?.Reliability ?? 90);
        _note.Text = preset?.NotePrefix ?? string.Empty;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            AutoScroll = true
        };
        var import = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var paste = new Button { Text = "从剪贴板识别", AutoSize = true, Padding = new Padding(6, 3, 6, 3) };
        paste.Click += (_, _) => ImportClipboard();
        import.Controls.Add(paste);
        import.Controls.Add(_recognition);
        root.Controls.Add(import);
        AddField(root, "信息类别", _kind);
        AddField(root, "作者", _author);
        AddField(root, "帖子链接", _url);
        AddField(root, "预计全员重置时间（取消勾选表示未给日期）", _target);
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

        if (!string.IsNullOrWhiteSpace(initialClipboardText))
        {
            ApplyImport(initialClipboardText);
        }
    }

    public QuotaCommunitySignalDraft Draft => new(
        _url.Text.Trim(),
        _author.Text.Trim(),
        PrefixNote(),
        _target.Checked ? new DateTimeOffset(_target.Value) : null,
        (_trust.SelectedItem as TrustItem)?.Reliability ?? 60);

    private void ImportClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                _recognition.Text = "剪贴板里没有可识别的文字或链接。";
                return;
            }

            ApplyImport(Clipboard.GetText());
        }
        catch (Exception error) when (error is ExternalException or ThreadStateException)
        {
            _recognition.Text = $"读取剪贴板失败：{error.Message}";
        }
    }

    private void ApplyImport(string text)
    {
        var fallbackKind = _kind.SelectedItem?.ToString() ?? "网页/新闻";
        var fallbackTrust = (_trust.SelectedItem as TrustItem)?.Reliability ?? 50;
        var imported = QuotaSignalImportParser.Parse(text, DateTimeOffset.Now, fallbackKind, fallbackTrust);

        if (!string.IsNullOrWhiteSpace(imported.SourceUrl))
        {
            _url.Text = imported.SourceUrl;
        }
        if (!string.IsNullOrWhiteSpace(imported.Author))
        {
            _author.Text = imported.Author;
        }
        if (!string.IsNullOrWhiteSpace(imported.Note))
        {
            _note.Text = imported.Note;
        }

        _kind.SelectedItem = imported.SourceKind;
        SelectTrust(imported.Reliability);
        _target.Checked = imported.TargetAt is not null;
        if (imported.TargetAt is not null)
        {
            _target.Value = imported.TargetAt.Value.LocalDateTime;
        }

        var dateResult = imported.TargetAt is null
            ? "未识别到明确日期，请手动填写或取消日期勾选"
            : $"日期 {imported.TargetAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
        _recognition.Text = $"已识别：{imported.SourceKind} · {dateResult} · 可信度 {imported.Reliability}";
    }

    private string PrefixNote()
    {
        var kind = _kind.SelectedItem?.ToString() ?? "其他";
        var note = _note.Text.Trim();
        return note.StartsWith($"[{kind}]", StringComparison.Ordinal)
            ? note
            : string.IsNullOrWhiteSpace(note) ? $"[{kind}]" : $"[{kind}] {note}";
    }

    private void SelectTrust(int reliability)
    {
        var best = _trust.Items.Cast<TrustItem>()
            .OrderBy(item => Math.Abs(item.Reliability - reliability))
            .First();
        _trust.SelectedItem = best;
    }

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

public sealed record ClueDialogPreset(
    string SourceKind,
    string DefaultAuthor,
    int Reliability,
    string NotePrefix = "");

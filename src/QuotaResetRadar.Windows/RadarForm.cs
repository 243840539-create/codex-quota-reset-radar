using System.Diagnostics;
using WindexBar.Core.Forecasting;
using WindexBar.Core.Refresh;

namespace QuotaResetRadar.Windows;

public sealed class RadarForm : Form
{
    private const string XQuery = "(from:thsottiaux OR from:sama OR from:OpenAIDevs OR from:OpenAI) Codex (reset OR \"rate limit\" OR \"usage limit\" OR \"all plans\")";

    private readonly UsageStore _usageStore;
    private readonly Label _status = new();
    private readonly Label _forecast = new();
    private readonly Label _probabilities = new();
    private readonly Label _official = new();
    private readonly Label _evidence = new();
    private readonly ListBox _clues = new();
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 30_000 };

    public RadarForm(UsageStore usageStore)
    {
        _usageStore = usageStore;
        Text = "Codex 全员额度重置雷达";
        MinimumSize = new Size(720, 620);
        Size = new Size(860, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 23, 29);
        ForeColor = Color.FromArgb(239, 235, 248);
        Font = new Font("Microsoft YaHei UI", 10f);

        Controls.Add(BuildLayout());
        _usageStore.Changed += OnUsageChanged;
        _clock.Tick += (_, _) => UpdateView();
        _clock.Start();
        Shown += (_, _) =>
        {
            _usageStore.StartBackgroundRefresh();
            UpdateView();
        };
        FormClosed += (_, _) =>
        {
            _clock.Stop();
            _usageStore.Changed -= OnUsageChanged;
        };
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22),
            ColumnCount = 1,
            RowCount = 7,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Codex 全员额度重置雷达",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 20f, FontStyle.Bold),
            ForeColor = Color.FromArgb(211, 187, 255),
            Margin = new Padding(0, 0, 0, 4)
        };
        root.Controls.Add(title);

        _status.AutoSize = true;
        _status.ForeColor = Color.FromArgb(166, 159, 181);
        _status.Margin = new Padding(0, 0, 0, 16);
        root.Controls.Add(_status);

        root.Controls.Add(Card(_forecast, Color.FromArgb(211, 187, 255), 16f));
        root.Controls.Add(Card(_probabilities, Color.FromArgb(232, 220, 255), 12f));
        root.Controls.Add(Card(_official, Color.FromArgb(141, 222, 187), 11f));

        var clueGroup = new GroupBox
        {
            Text = "X 线索与历史验证",
            Dock = DockStyle.Fill,
            ForeColor = ForeColor,
            Padding = new Padding(12),
            Margin = new Padding(0, 12, 0, 10)
        };
        var clueLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        clueLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        clueLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _evidence.AutoSize = true;
        _evidence.ForeColor = Color.FromArgb(184, 178, 196);
        _evidence.Margin = new Padding(0, 0, 0, 8);
        clueLayout.Controls.Add(_evidence);
        _clues.Dock = DockStyle.Fill;
        _clues.BackColor = Color.FromArgb(34, 31, 41);
        _clues.ForeColor = ForeColor;
        _clues.BorderStyle = BorderStyle.FixedSingle;
        clueLayout.Controls.Add(_clues);
        clueGroup.Controls.Add(clueLayout);
        root.Controls.Add(clueGroup);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        buttons.Controls.Add(ActionButton("刷新", async (_, _) => await RefreshAsync()));
        buttons.Controls.Add(ActionButton("添加全员重置线索", (_, _) => AddClue()));
        buttons.Controls.Add(ActionButton("搜索 X", (_, _) => OpenXSearch()));
        buttons.Controls.Add(ActionButton("清空线索", (_, _) => ClearClues()));
        root.Controls.Add(buttons);

        return root;
    }

    private static Panel Card(Label label, Color color, float fontSize)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Color.FromArgb(33, 30, 40),
            Padding = new Padding(16, 13, 16, 13),
            Margin = new Padding(0, 0, 0, 10)
        };
        label.AutoSize = true;
        label.MaximumSize = new Size(780, 0);
        label.Font = new Font("Microsoft YaHei UI", fontSize, fontSize >= 16 ? FontStyle.Bold : FontStyle.Regular);
        label.ForeColor = color;
        panel.Controls.Add(label);
        return panel;
    }

    private static Button ActionButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(58, 47, 73),
            ForeColor = Color.White,
            Margin = new Padding(8, 0, 0, 0),
            Padding = new Padding(8, 4, 8, 4)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(118, 91, 153);
        button.Click += onClick;
        return button;
    }

    private void OnUsageChanged(object? sender, EventArgs args)
    {
        if (IsHandleCreated)
        {
            BeginInvoke(UpdateView);
        }
    }

    private void UpdateView()
    {
        var snapshot = _usageStore.Forecast;
        _status.Text = _usageStore.IsRefreshing
            ? "正在读取 Codex 用量……"
            : string.IsNullOrWhiteSpace(_usageStore.LastError)
                ? $"本地时间 {DateTimeOffset.Now:yyyy-MM-dd HH:mm} · 数据仅保存在本机"
                : $"Codex 读取暂不可用：{_usageStore.LastError}";

        _forecast.Text = snapshot.ExtraReset is null
            ? "全员额度重置预测\n证据不足，暂不报日期"
            : $"全员额度重置预测\n{snapshot.ExtraReset.WindowStartsAt.ToLocalTime():MM-dd HH:mm} — {snapshot.ExtraReset.WindowEndsAt.ToLocalTime():MM-dd HH:mm}\n总体概率 {snapshot.ExtraReset.Confidence}% · {Basis(snapshot.ExtraReset.Basis)}";

        _probabilities.Text = FormatProbabilities(snapshot.ExtraReset);
        _official.Text = snapshot.OfficialReset is null
            ? "个人周期参考：等待 Codex 返回精确重置时间（不参与全员概率）"
            : $"个人周期参考：{snapshot.OfficialReset.ResetsAt.ToLocalTime():MM-dd HH:mm} · 剩余 {snapshot.OfficialReset.RemainingPercent:0.#}%（不参与全员概率）";

        _evidence.Text = $"X 线索 {snapshot.Signals.Count} · 命中 {snapshot.ConfirmedSignalCount} · 待验证 {snapshot.PendingSignalCount} · 历史全员重置 {snapshot.Observations.Count}";
        _clues.BeginUpdate();
        _clues.Items.Clear();
        foreach (var clue in snapshot.Signals)
        {
            var date = clue.TargetAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未给日期";
            var status = clue.Status switch
            {
                QuotaSignalStatus.Confirmed => "命中",
                QuotaSignalStatus.Missed => "未命中",
                _ => "待验证"
            };
            _clues.Items.Add($"{clue.Author,-16} → {date} · 权重 {clue.Reliability}% · {status} · {clue.Note}");
        }

        if (_clues.Items.Count == 0)
        {
            _clues.Items.Add("暂无线索。点击“搜索 X”，把明确提到全员重置日期的帖子加入雷达。");
        }
        _clues.EndUpdate();
    }

    private static string FormatProbabilities(ExtraQuotaResetForecast? extra)
    {
        if (extra is null)
        {
            return "日期概率\n尚无可计算的概率分布";
        }

        var lines = extra.DateProbabilities
            .OrderByDescending(item => item.Probability)
            .ThenBy(item => item.Date)
            .Take(6)
            .Select(item => $"{item.Date:MM-dd}  {item.Probability,2}%  {new string('■', Math.Max(1, (int)Math.Round(item.Probability / 5d)))}")
            .ToList();
        lines.Insert(0, "日期概率");
        lines.Add($"近期暂无全员重置  {extra.NoExtraResetProbability}%");
        return string.Join(Environment.NewLine, lines);
    }

    private async Task RefreshAsync()
    {
        try
        {
            await _usageStore.RefreshAsync();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "刷新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void AddClue()
    {
        using var dialog = new ClueDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _usageStore.AddForecastSignal(dialog.Draft);
        UpdateView();
    }

    private void ClearClues()
    {
        if (MessageBox.Show(
                this,
                "清空手工加入的 X 线索？已观察到的全员重置历史会保留。",
                "确认清空",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _usageStore.ClearForecastSignals();
        UpdateView();
    }

    private static void OpenXSearch()
    {
        var url = $"https://x.com/search?q={Uri.EscapeDataString(XQuery)}&src=typed_query&f=live";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static string Basis(string basis) => basis switch
    {
        "community" => "X 线索",
        "history" => "历史节奏",
        "community+history" => "X 线索 + 历史",
        "history (signals disagree)" => "历史为主，线索冲突",
        "community (history disagrees)" => "X 为主，历史冲突",
        _ => basis
    };
}

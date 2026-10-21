using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using CodexUsage.Daily;
using ShapePath = System.Windows.Shapes.Path;

namespace CodexUsage.DailyPanel;

public sealed class DailyWindow : Window
{
    private static readonly Brush Ink = Brush("#20242C");
    private static readonly Brush Muted = Brush("#818895");
    private static readonly Brush Blue = Brush("#3279F5");
    private static readonly Brush Hairline = Brush("#EAEDF2");
    private readonly TextBlock _status = Label("Waiting", 10.5, Brush("#7C8794"));
    private readonly Border _statusPill;
    private readonly TextBlock _day = Label("Today", 14, Ink, FontWeights.SemiBold);
    private readonly TextBlock _zone = Label("Local time", 10.5);
    private readonly TextBlock _input = Label("—", 28, Ink, FontWeights.SemiBold);
    private readonly TextBlock _output = Label("—", 28, Blue, FontWeights.SemiBold);
    private readonly TextBlock _inputExact = Label("Exact count unavailable", 9.5);
    private readonly TextBlock _outputExact = Label("Exact count unavailable", 9.5, Brush("#7794BF"));
    private readonly TextBlock _total = Label("—", 25, Ink, FontWeights.SemiBold);
    private readonly TextBlock _totalExact = Label("Exact count unavailable", 10);
    private readonly TextBlock _tps = Label("—", 28, Ink, FontWeights.SemiBold);
    private readonly TextBlock _processing = Label("—", 18, Ink, FontWeights.SemiBold);
    private readonly TextBlock _weeklyRemaining = Label("No data", 12, Muted, FontWeights.SemiBold);
    private readonly TextBlock _weeklyReset = Label("等待日志中的周额度快照", 10.5);
    private readonly TextBlock _weeklyObserved = Label("", 10);
    private readonly Border _weeklyFill;
    private readonly Grid _weeklyTrack;
    private readonly TextBlock _chats = Label("— chats", 10.5);
    private readonly TextBlock _turns = Label("— turns", 10.5);
    private readonly TextBlock _cached = Label("—", 12.5, Ink, FontWeights.SemiBold);
    private readonly TextBlock _reasoning = Label("—", 12.5, Ink, FontWeights.SemiBold);
    private readonly TextBlock _uncached = Label("—", 12.5, Ink, FontWeights.SemiBold);
    private readonly TextBlock _active = Label("—", 12.5, Ink, FontWeights.SemiBold);
    private readonly TextBlock _timingScope = Label("Root sessions only · TPS estimate", 10);
    private readonly TextBlock _note = Label("Waiting for local usage logs", 10.5);
    private readonly TextBlock _updated = Label("Not updated yet", 10);
    private readonly TextBlock _copyLabel = Label("Copy stats", 12, Brushes.White, FontWeights.SemiBold);
    private readonly Button _copyButton;
    private readonly Button _refreshButton;
    private readonly DispatcherTimer _copyFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DailySnapshot? _snapshot;
    private bool _demo;
    private bool _busy;
    private double _weeklyPercent;

    public event Action? RefreshRequested;
    public event Action? FolderRequested;
    public event Action? ExitRequested;
    public string CopyText => _snapshot is { } snapshot ? DailyFormatting.CopyText(snapshot, _demo) : "";

    public DailyWindow()
    {
        Title = "Codex Usage · Daily";
        Width = 460;
        Height = Math.Min(640, Math.Max(360, SystemParameters.WorkArea.Height - 30));
        MinWidth = 360;
        MinHeight = Math.Min(360, SystemParameters.WorkArea.Height);
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 16);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        FontSize = 12;
        Foreground = Ink;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;

        var shell = new Border
        {
            Margin = new Thickness(10), Background = Brushes.White, CornerRadius = new CornerRadius(18),
            BorderBrush = Brush("#E2E6ED"), BorderThickness = new Thickness(1), Padding = new Thickness(21, 16, 21, 12),
            Effect = new DropShadowEffect { Color = Color.FromRgb(33, 44, 66), BlurRadius = 18, ShadowDepth = 3, Opacity = .14 }
        };
        var layout = new DockPanel { LastChildFill = true };
        shell.Child = layout;
        Content = shell;

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var histogram = new Border { Width = 31, Height = 31, CornerRadius = new CornerRadius(8), Background = Brush("#EAF2FF"), Child = Histogram() };
        heading.Children.Add(histogram);
        var title = Label("Codex Usage", 19, Ink, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(title, 1);
        heading.Children.Add(title);
        _statusPill = new Border { Child = _status, Background = Brush("#F0F3F6"), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_statusPill, 2);
        heading.Children.Add(_statusPill);
        var close = IconButton("M 4,4 L 12,12 M 12,4 L 4,12", "隐藏至托盘（Esc）", 23);
        close.Margin = new Thickness(7, 0, -7, 0);
        close.Background = Brushes.Transparent;
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 3);
        heading.Children.Add(close);
        header.Children.Add(heading);
        var dateRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        dateRow.ColumnDefinitions.Add(new ColumnDefinition());
        dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dateRow.Children.Add(_day);
        _zone.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_zone, 1);
        dateRow.Children.Add(_zone);
        header.Children.Add(dateRow);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(Divider());
        var actions = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 3; i++) actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copyContent = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        copyContent.Children.Add(Glyph("M 6,5 L 6,3 Q 6,2 7,2 L 12,2 Q 13,2 13,3 L 13,10 Q 13,11 12,11 L 10,11 M 4,5 L 9,5 Q 10,5 10,6 L 10,13 Q 10,14 9,14 L 4,14 Q 3,14 3,13 L 3,6 Q 3,5 4,5 Z", Brushes.White, 15));
        _copyLabel.Margin = new Thickness(7, 0, 0, 0);
        copyContent.Children.Add(_copyLabel);
        _copyButton = MakeButton(copyContent, "复制当前显示统计，包含精确 token 数和统计口径");
        _copyButton.Background = Blue;
        _copyButton.Foreground = Brushes.White;
        _copyButton.IsEnabled = false;
        _copyButton.Height = 35;
        _copyButton.Click += (_, _) => CopyStats();
        actions.Children.Add(_copyButton);
        _refreshButton = IconButton("M 12.8,6 A 5.1,5.1 0 1 0 12.8,10 M 12.8,2.5 L 12.8,6.5 L 8.8,6.5", "刷新统计", 35);
        _refreshButton.Click += (_, _) => RefreshRequested?.Invoke();
        _refreshButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_refreshButton, 1);
        actions.Children.Add(_refreshButton);
        var folder = IconButton("M 2,5 L 2,4 Q 2,3 3,3 L 6,3 L 7.5,5 L 13,5 Q 14,5 14,6 L 14,12 Q 14,13 13,13 L 3,13 Q 2,13 2,12 Z M 2,6 L 14,6", "打开本地 Codex 日志目录", 35);
        folder.Margin = new Thickness(7, 0, 0, 0);
        folder.Click += (_, _) => FolderRequested?.Invoke();
        Grid.SetColumn(folder, 2);
        actions.Children.Add(folder);
        var power = IconButton("M 8,1.8 L 8,7.5 M 4.4,3.8 A 5.5,5.5 0 1 0 11.6,3.8", "退出面板", 35);
        power.Margin = new Thickness(7, 0, 0, 0);
        power.Click += (_, _) => ExitRequested?.Invoke();
        Grid.SetColumn(power, 3);
        actions.Children.Add(power);
        footer.Children.Add(actions);
        _updated.HorizontalAlignment = HorizontalAlignment.Center;
        footer.Children.Add(_updated);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

        var body = new StackPanel();
        var tokenCards = new Grid();
        tokenCards.ColumnDefinitions.Add(new ColumnDefinition());
        tokenCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        tokenCards.ColumnDefinitions.Add(new ColumnDefinition());
        tokenCards.Children.Add(TokenCard("Total input", _input, _inputExact, "输入 token，已包含缓存输入", "#F5F6F8"));
        var outputCard = TokenCard("Total output", _output, _outputExact, "输出 token，已包含推理输出", "#EEF4FF");
        Grid.SetColumn(outputCard, 2);
        tokenCards.Children.Add(outputCard);
        body.Children.Add(tokenCards);

        var totalRow = new Grid();
        totalRow.ColumnDefinitions.Add(new ColumnDefinition());
        totalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        totalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        totalRow.Children.Add(Label("Total tokens", 11, Ink, FontWeights.SemiBold));
        Grid.SetColumn(_total, 1);
        totalRow.Children.Add(_total);
        _totalExact.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_totalExact, 2);
        totalRow.Children.Add(_totalExact);
        body.Children.Add(new Border
        {
            Child = totalRow, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 7, 0, 0),
            Background = Brush("#F5F6F8"), CornerRadius = new CornerRadius(10),
            ToolTip = "今日总 token = Total input + Total output；输入已包含缓存，输出已包含推理，不重复累加。"
        });

        var speedRow = new Grid { Margin = new Thickness(1, 5, 1, 6) };
        speedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        speedRow.ColumnDefinitions.Add(new ColumnDefinition());
        var speed = new StackPanel { ToolTip = "估算平均 TPS：对应完成回合的输出 token ÷ 扣除首字延迟与可识别工具等待后的秒数。重叠等待只扣一次；缺少可靠首字、工具配对、时长或对账的回合不计入。仍可能包含推理、网络和调度耗时。" };
        speed.Children.Add(Label("Average speed · est.", 11));
        var speedValue = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        speedValue.Children.Add(_tps);
        var unit = Label("tok/s", 13);
        unit.VerticalAlignment = VerticalAlignment.Bottom;
        unit.Margin = new Thickness(5, 0, 0, 5);
        speedValue.Children.Add(unit);
        speed.Children.Add(speedValue);
        speedRow.Children.Add(speed);
        var processing = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(17, 0, 0, 0), ToolTip = "与平均 TPS 使用同一组回合：整轮耗时扣除首字延迟与工具等待区间的并集。并发回合分别累计；这是日志估算值。" };
        processing.Children.Add(Label("Net processing time", 11));
        _processing.Margin = new Thickness(0, 8, 0, 0);
        processing.Children.Add(_processing);
        Grid.SetColumn(processing, 1);
        speedRow.Children.Add(processing);
        body.Children.Add(speedRow);

        var weeklyContent = new StackPanel();
        var weeklyHead = new Grid();
        weeklyHead.ColumnDefinitions.Add(new ColumnDefinition());
        weeklyHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        weeklyHead.Children.Add(Label("Weekly allowance", 12, Ink, FontWeights.SemiBold));
        Grid.SetColumn(_weeklyRemaining, 1);
        weeklyHead.Children.Add(_weeklyRemaining);
        weeklyContent.Children.Add(weeklyHead);
        _weeklyTrack = new Grid { Height = 4, Margin = new Thickness(0, 4, 0, 6) };
        _weeklyTrack.Children.Add(new Border { Background = Brush("#E2E9F5"), CornerRadius = new CornerRadius(3) });
        _weeklyFill = new Border { Background = Blue, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _weeklyTrack.Children.Add(_weeklyFill);
        _weeklyTrack.SizeChanged += (_, _) => UpdateWeeklyWidth();
        weeklyContent.Children.Add(_weeklyTrack);
        weeklyContent.Children.Add(_weeklyReset);
        _weeklyObserved.Margin = new Thickness(0, 2, 0, 0);
        weeklyContent.Children.Add(_weeklyObserved);
        body.Children.Add(new Border
        {
            Child = weeklyContent, Padding = new Thickness(13, 8, 13, 8), Background = Brush("#F7F9FD"),
            BorderBrush = Brush("#E9EDF5"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            ToolTip = "仅显示本地日志最新报告的 10080 分钟额度窗口。不是实时账号查询；超过日志报告的重置时间后不继续显示旧数值。"
        });

        var breakdownHeading = new Grid { Margin = new Thickness(0, 10, 0, 5) };
        breakdownHeading.ColumnDefinitions.Add(new ColumnDefinition());
        breakdownHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        breakdownHeading.Children.Add(Label("BREAKDOWN", 10, Muted, FontWeights.SemiBold));
        var counts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _chats.ToolTip = "当天有统计事件的根会话数量";
        _turns.ToolTip = "当天有统计事件的回合数量，包含运行中回合";
        counts.Children.Add(_chats);
        var countSeparator = Label("·", 10.5);
        countSeparator.Margin = new Thickness(7, 0, 7, 0);
        counts.Children.Add(countSeparator);
        counts.Children.Add(_turns);
        Grid.SetColumn(counts, 1);
        breakdownHeading.Children.Add(counts);
        body.Children.Add(breakdownHeading);
        var details = new StackPanel();
        details.Children.Add(DetailRow("Cache hit rate", _cached, "缓存输入 token ÷ 输入 token，按 token 加权；分母为零或任一分量未知时不计算"));
        details.Children.Add(DetailRow("Reasoning output", _reasoning, "已包含在 Total output 中，请勿再次相加"));
        details.Children.Add(DetailRow("Uncached input", _uncached, "Total input − Cached input；任一分量未知时保持未知"));
        details.Children.Add(DetailRow("Active time", _active, "有效回合时间区间的并集：重叠并发时间只计一次，与处理时长之和不同"));
        body.Children.Add(details);
        _timingScope.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(_timingScope);
        _note.Margin = new Thickness(0, 3, 0, 0);
        _note.Visibility = Visibility.Collapsed;
        _note.TextTrimming = TextTrimming.CharacterEllipsis;
        body.Children.Add(_note);
        layout.Children.Add(new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 2, 0), PanningMode = PanningMode.VerticalOnly
        });

        _copyFeedbackTimer.Tick += (_, _) => { _copyFeedbackTimer.Stop(); _copyLabel.Text = "Copy stats"; };
        Closed += (_, _) => _copyFeedbackTimer.Stop();
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { Close(); args.Handled = true; } };
    }

    public void Update(DailySnapshot snapshot, bool demo = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _demo = demo;
        _day.Text = (snapshot.Day == DateOnly.FromDateTime(DateTime.Now) ? "Today · " : "")
            + snapshot.Day.ToString("ddd, dd MMM", CultureInfo.InvariantCulture);
        _day.ToolTip = snapshot.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _zone.Text = DailyFormatting.ZoneLabel(snapshot.Day);
        _zone.ToolTip = TimeZoneInfo.Local.DisplayName + "\n按本机时区划分当天统计";
        SetTokens(_input, snapshot.InputTokens);
        SetTokens(_output, snapshot.OutputTokens);
        var total = DailyFormatting.TotalTokens(snapshot.InputTokens, snapshot.OutputTokens);
        SetTokens(_total, total);
        _totalExact.Text = total is >= 0 ? DailyFormatting.Exact(total) : "Exact count unavailable";
        _totalExact.ToolTip = DailyFormatting.Exact(total) + " tokens";
        _inputExact.Text = snapshot.InputTokens is >= 0 ? DailyFormatting.Exact(snapshot.InputTokens) + " tokens" : "Exact count unavailable";
        _outputExact.Text = snapshot.OutputTokens is >= 0 ? DailyFormatting.Exact(snapshot.OutputTokens) + " tokens" : "Exact count unavailable";
        _cached.Text = DailyFormatting.CacheRate(snapshot.InputTokens, snapshot.CachedInputTokens);
        _cached.ToolTip = "缓存输入 " + DailyFormatting.Exact(snapshot.CachedInputTokens) + " tokens / 输入 " + DailyFormatting.Exact(snapshot.InputTokens) + " tokens";
        SetTokens(_reasoning, snapshot.ReasoningTokens);
        SetTokens(_uncached, DailyFormatting.Uncached(snapshot.InputTokens, snapshot.CachedInputTokens));
        _reasoning.Text = snapshot.ReasoningTokens is { } reasoning ? reasoning.ToString("N0", CultureInfo.InvariantCulture) : "—";
        _uncached.Text = DailyFormatting.Uncached(snapshot.InputTokens, snapshot.CachedInputTokens) is { } uncached ? uncached.ToString("N0", CultureInfo.InvariantCulture) : "—";
        _tps.Text = DailyFormatting.Tps(snapshot.AverageTps);
        _processing.Text = DailyFormatting.Duration(snapshot.ProcessingSeconds);
        _processing.ToolTip = DailyFormatting.ExactDuration(snapshot.ProcessingSeconds);
        _timingScope.Text = $"Root sessions · TPS: {snapshot.TimedTurnCount:N0} included / {snapshot.ExcludedTimingTurnCount:N0} excluded";
        _timingScope.ToolTip = "仅统计本地根会话，不计子代理及云任务；输入包含缓存，输出包含推理。\n"
            + $"估算 TPS 纳入 {snapshot.TimedTurnCount:N0} 个完成回合，排除 {snapshot.ExcludedTimingTurnCount:N0} 个。\n"
            + "已扣首字与工具等待（区间并集）：" + DailyFormatting.ExactDuration(snapshot.ExcludedWaitSeconds)
            + "\nTTFT 为整轮首 token 延迟，未必等同可见首字；不代表逐次模型请求的全部等待。";
        _active.Text = DailyFormatting.Duration(snapshot.ActiveSeconds);
        _active.ToolTip = DailyFormatting.ExactDuration(snapshot.ActiveSeconds) + "\n有效回合区间并集，重叠时间不重复计入";
        _chats.Text = snapshot.ChatCount.ToString("N0", CultureInfo.InvariantCulture) + " chats";
        _turns.Text = snapshot.TurnCount.ToString("N0", CultureInfo.InvariantCulture) + " turns";
        UpdateWeekly(snapshot);
        _note.Text = snapshot.Status;
        _note.Visibility = snapshot.IsPartial ? Visibility.Visible : Visibility.Collapsed;
        _note.Foreground = snapshot.IsPartial ? Brush("#A16D1C") : Muted;
        _note.ToolTip = snapshot.Status + $"\n{snapshot.SourceCount:N0} 个本地日志来源 · {snapshot.ActiveTurns:N0} 个运行中回合";
        _updated.Text = "Updated " + snapshot.UpdatedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            + (demo ? " · Demo data" : " · Local logs");
        _updated.ToolTip = snapshot.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
            + "\n" + snapshot.Status + $"\n{snapshot.SourceCount:N0} 个本地日志来源 · {snapshot.ActiveTurns:N0} 个运行中回合";
        _copyButton.IsEnabled = true;
        UpdateStatus();
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        _refreshButton.IsEnabled = !busy;
        _refreshButton.ToolTip = busy ? "正在刷新本地统计…" : "刷新统计";
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var partial = _snapshot?.IsPartial == true;
        var empty = _snapshot is null || (_snapshot.SourceCount == 0 && _snapshot.InputTokens is null && _snapshot.OutputTokens is null);
        _status.Text = _busy ? "Refreshing" : _demo ? "Demo" : partial ? "Partial" : empty ? "No data"
            : _snapshot!.ActiveTurns > 0 ? _snapshot.ActiveTurns + " running" : "Local";
        _status.Foreground = _demo || partial ? Brush("#A16D1C") : _busy ? Blue : empty ? Muted : Brush("#278663");
        _statusPill.Background = _demo || partial ? Brush("#FFF5DE") : _busy ? Brush("#EAF2FF") : empty ? Brush("#F0F3F6") : Brush("#EAF7F0");
        _statusPill.ToolTip = _demo ? "演示数据，不代表真实使用量" : _busy ? "正在读取本地日志" : partial ? "部分数据缺失或不完整，请查看下方状态" : "统计来源：本地日志";
    }

    private void UpdateWeekly(DailySnapshot snapshot)
    {
        if (snapshot.Weekly is not { } weekly || !double.IsFinite(weekly.UsedPercent) || weekly.UsedPercent is < 0 or > 100
            || (weekly.ResetsAt is { } reset && reset <= snapshot.UpdatedAt))
        {
            _weeklyPercent = 0;
            _weeklyRemaining.Text = "No data";
            _weeklyRemaining.Foreground = Muted;
            _weeklyRemaining.ToolTip = "本地日志没有仍有效的周额度窗口";
            _weeklyReset.Text = "No valid weekly snapshot";
            _weeklyObserved.Text = "Local logs · 实时额度未查询";
        }
        else
        {
            _weeklyPercent = 100 - weekly.UsedPercent;
            _weeklyRemaining.Text = _weeklyPercent.ToString("0.#", CultureInfo.InvariantCulture) + "% left";
            _weeklyRemaining.Foreground = Blue;
            _weeklyRemaining.ToolTip = "日志报告已用 " + weekly.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
            _weeklyReset.Text = weekly.ResetsAt is { } next ? "Resets " + next.ToLocalTime().ToString("ddd, dd MMM · HH:mm", CultureInfo.InvariantCulture) : "Reset time unavailable";
            _weeklyObserved.Text = "Log snapshot · " + weekly.ObservedAt.ToLocalTime().ToString("dd MMM, HH:mm", CultureInfo.InvariantCulture);
        }
        UpdateWeeklyWidth();
    }

    private void UpdateWeeklyWidth() => _weeklyFill.Width = Math.Max(0, _weeklyTrack.ActualWidth * Math.Clamp(_weeklyPercent, 0, 100) / 100);

    private void CopyStats()
    {
        if (CopyText.Length == 0) return;
        try { Clipboard.SetText(CopyText); _copyLabel.Text = "Copied"; }
        catch (ExternalException) { _copyLabel.Text = "Copy failed"; }
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    private static void SetTokens(TextBlock target, long? value)
    {
        target.Text = DailyFormatting.Tokens(value);
        target.ToolTip = DailyFormatting.Exact(value) + " tokens";
    }

    private static Border TokenCard(string title, TextBlock value, TextBlock exact, string tooltip, string color)
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(title, 11));
        value.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(value);
        exact.Margin = new Thickness(0, 1, 0, 0);
        panel.Children.Add(exact);
        return new Border { Child = panel, Padding = new Thickness(14, 8, 14, 8), Background = Brush(color), CornerRadius = new CornerRadius(10), ToolTip = tooltip };
    }

    private static Grid DetailRow(string title, TextBlock value, string tooltip)
    {
        var row = new Grid { Height = 21, ToolTip = tooltip };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Label(title, 11));
        value.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    private static TextBlock Label(string value, double size = 12, Brush? color = null, FontWeight? weight = null) => new()
    {
        Text = value, FontSize = size, Foreground = color ?? Muted, FontWeight = weight ?? FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Border Divider() => new() { Height = 1, Background = Hairline };

    private static Button IconButton(string geometry, string tooltip, double size)
    {
        var button = MakeButton(Glyph(geometry, Brush("#687485"), 16), tooltip);
        button.Width = size;
        button.Height = size;
        button.Padding = new Thickness(0);
        return button;
    }

    private static Button MakeButton(object content, string tooltip)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
        presenter.SetBinding(FrameworkElement.MarginProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, .83));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, .5));
        template.Triggers.Add(disabled);
        var button = new Button
        {
            Content = content, ToolTip = tooltip, Cursor = Cursors.Hand, Background = Brush("#F2F4F7"),
            BorderThickness = new Thickness(0), Padding = new Thickness(9, 7, 9, 7), Template = template,
            FocusVisualStyle = null
        };
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private static ShapePath Glyph(string geometry, Brush color, double size) => new()
    {
        Data = Geometry.Parse(geometry), Stroke = color, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.Uniform,
        Width = size, Height = size, Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };

    private static Grid Histogram()
    {
        var grid = new Grid { Width = 18, Height = 17, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        for (var i = 0; i < 4; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var bar = new Border { Background = Blue, CornerRadius = new CornerRadius(1), Width = 3, Height = new[] { 6d, 11d, 8d, 16d }[i], VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetColumn(bar, i);
            grid.Children.Add(bar);
        }
        return grid;
    }

    private static Brush Brush(string color)
    {
        var value = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        value.Freeze();
        return value;
    }

}

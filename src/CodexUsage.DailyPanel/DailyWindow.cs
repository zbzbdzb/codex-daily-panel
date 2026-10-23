using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexUsage.Daily;
using Microsoft.Win32;

namespace CodexUsage.DailyPanel;

public sealed class DailyWindow : Window
{
    private readonly List<Action<DailyStrings>> _localized = new();
    private readonly TextBlock _quality = Label("", 12), _activity = Label("", 12), _day = Label("", 14), _zone = Label("", 12);
    private readonly TextBlock _total = Label("—", 32, true), _input = Label("—", 22, true), _output = Label("—", 22, true);
    private readonly TextBlock _totalExact = Label("", 12), _inputExact = Label("", 12), _outputExact = Label("", 12);
    private readonly TextBlock _tps = Label("—", 24, true), _processing = Label("—", 22, true);
    private readonly TextBlock _weekly = Label("", 16, true), _weeklyObserved = Label("", 12), _weeklyReset = Label("", 12);
    private readonly TextBlock _cache = Label("", 14), _reasoning = Label("", 14), _uncached = Label("", 14), _active = Label("", 14);
    private readonly TextBlock _counts = Label("", 12), _timing = Label("", 12), _note = Label("", 12), _updated = Label("", 11);
    private readonly TextBlock _copyLabel = Label("", 14, true);
    private readonly Button _copyButton, _refreshButton;
    private readonly ComboBox _themeChoice = new(), _languageChoice = new();
    private readonly DispatcherTimer _copyFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Expander _details;
    private DailyPresentationState _state = new();
    private PanelSettings _settings = new();
    private DailyStrings _strings = new(LanguagePreference.System);
    private DateTimeOffset _now = DateTimeOffset.Now;
    private TimeZoneInfo _zoneInfo = TimeZoneInfo.Local;
    private bool _demo, _updatingPreferences, _closed;

    public event Action? RefreshRequested;
    public event Action? FolderRequested;
    public event Action? ExitRequested;
    public event Action<PanelSettings>? SettingsChanged;
    public string CopyText => DailyPresentationFormatting.Copy(_state.Project(_now, _zoneInfo), _strings, _zoneInfo, _demo);
    public PanelSettings Settings => _settings;

    public DailyWindow()
    {
        Width = 460; Height = 640; MinWidth = 300; MinHeight = 280;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 14;
        UseLayoutRounding = true; SnapsToDevicePixels = true; ShowInTaskbar = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(ForegroundProperty, "Panel.Text");
        DailyTheme.Apply(this, _settings.Theme);
        var shell = new Border { Padding = new Thickness(16), Margin = new Thickness(8), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        shell.SetResourceReference(Border.BackgroundProperty, "Panel.Surface"); shell.SetResourceReference(Border.BorderBrushProperty, "Panel.Border");
        Content = shell;
        var layout = new DockPanel(); shell.Child = layout;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var titleRow = new DockPanel();
        var hide = ActionButton("×", "Hide", "HidePanel"); hide.Width = 32; hide.Click += (_, _) => Close();
        DockPanel.SetDock(hide, Dock.Right); titleRow.Children.Add(hide);
        var title = LocalLabel("Title", 18, true); title.VerticalAlignment = VerticalAlignment.Center; titleRow.Children.Add(title); header.Children.Add(titleRow);
        var date = new DockPanel { Margin = new Thickness(0, 8, 0, 4) }; DockPanel.SetDock(_zone, Dock.Right); date.Children.Add(_zone); date.Children.Add(_day); header.Children.Add(date);
        var status = new WrapPanel(); status.Children.Add(Badge(_quality)); status.Children.Add(Badge(_activity)); header.Children.Add(status);
        SetId(_quality, "DataQuality"); SetId(_activity, "UiActivity");
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);

        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 3; i++) actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _copyButton = ActionButton(_copyLabel, "CopyAccessible", "CopyStats"); _copyButton.Click += (_, _) => CopyStats(); actions.Children.Add(_copyButton);
        _refreshButton = ActionButton("↻", "Refresh", "RefreshStats"); _refreshButton.Click += (_, _) => RefreshRequested?.Invoke();
        var folder = ActionButton("▱", "Folder", "OpenLogs"); folder.Click += (_, _) => FolderRequested?.Invoke();
        var exit = ActionButton("⏻", "Exit", "ExitPanel"); exit.Click += (_, _) => ExitRequested?.Invoke();
        var iconButtons = new[] { _refreshButton, folder, exit };
        for (var i = 0; i < iconButtons.Length; i++) { iconButtons[i].Width = 36; iconButtons[i].Margin = new Thickness(8,0,0,0); Grid.SetColumn(iconButtons[i], i + 1); actions.Children.Add(iconButtons[i]); }
        footer.Children.Add(actions); _updated.Margin = new Thickness(0, 6, 0, 0); footer.Children.Add(_updated);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);

        var body = new StackPanel();
        var totalContent = new StackPanel(); totalContent.Children.Add(LocalLabel("Total", 12)); totalContent.Children.Add(_total); totalContent.Children.Add(_totalExact);
        body.Children.Add(Card(totalContent)); SetId(_total, "TotalTokens");
        var tokenGrid = TwoColumns(TokenBlock("Input", _input, _inputExact), TokenBlock("Output", _output, _outputExact));
        tokenGrid.Margin = new Thickness(0, 8, 0, 12); body.Children.Add(tokenGrid); SetId(_input,"TotalInput"); SetId(_output,"TotalOutput");
        body.Children.Add(TwoColumns(TokenBlock("Speed", _tps, null), TokenBlock("NetTime", _processing, null)));
        SetId(_tps,"AverageTps"); SetId(_processing,"ProcessingTime");
        var weekly = new StackPanel(); weekly.Children.Add(LocalLabel("Weekly", 12)); weekly.Children.Add(_weekly); weekly.Children.Add(_weeklyObserved); weekly.Children.Add(_weeklyReset); weekly.Children.Add(LocalLabel("NotLive", 11));
        var weeklyCard = Card(weekly); weeklyCard.Margin = new Thickness(0,12,0,8); body.Children.Add(weeklyCard);
        var detailContent = new StackPanel();
        detailContent.Children.Add(DetailRow("Cache", _cache)); detailContent.Children.Add(DetailRow("Reasoning", _reasoning));
        detailContent.Children.Add(DetailRow("Uncached", _uncached)); detailContent.Children.Add(DetailRow("ActiveTime", _active));
        detailContent.Children.Add(_counts); detailContent.Children.Add(_timing);
        foreach (var key in new[] { "Scope", "Subsets", "TpsExplanation", "TpsLimit" }) { var line = LocalLabel(key, 12); line.Margin = new Thickness(0,8,0,0); detailContent.Children.Add(line); }
        _details = new Expander { Content = detailContent, Margin = new Thickness(0,4,0,4) }; SetId(_details,"StatisticsDetails");
        _localized.Add(s => { _details.Header=s["Details"]; AutomationProperties.SetName(_details,s["Details"]); }); body.Children.Add(_details);
        _note.Margin = new Thickness(0,8,0,8); SetId(_note,"DataIssues"); body.Children.Add(_note);
        var preferences = new StackPanel();
        preferences.Children.Add(PreferenceRow("Theme", _themeChoice)); preferences.Children.Add(PreferenceRow("Language", _languageChoice));
        foreach (var value in Enum.GetValues<ThemePreference>()) _themeChoice.Items.Add(new ComboBoxItem { Tag = value });
        foreach (var value in Enum.GetValues<LanguagePreference>()) _languageChoice.Items.Add(new ComboBoxItem { Tag = value });
        SetId(_themeChoice,"ThemePreference"); SetId(_languageChoice,"LanguagePreference");
        var preferenceExpander = new Expander { Content=preferences, Margin=new Thickness(0,4,0,0) }; SetId(preferenceExpander,"Preferences");
        _localized.Add(s => { preferenceExpander.Header=s["Settings"]; AutomationProperties.SetName(preferenceExpander,s["Settings"]); });
        body.Children.Add(preferenceExpander);
        var scroll = new ScrollViewer { Content=body, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled, Padding=new Thickness(0,0,4,0), PanningMode=PanningMode.VerticalOnly };
        SetId(scroll,"StatisticsScroll"); layout.Children.Add(scroll);
        _themeChoice.SelectionChanged += PreferencesSelected; _languageChoice.SelectionChanged += PreferencesSelected;
        _copyFeedbackTimer.Tick += (_,_) => { _copyFeedbackTimer.Stop(); _copyLabel.Text=_strings["Copy"]; };
        KeyDown += (_,args) => { if(args.Key==Key.Escape) { Close(); args.Handled=true; } };
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        Closed += (_,_) => { _closed=true; _copyFeedbackTimer.Stop(); SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged; };
        Configure(_settings);
    }

    public void Configure(PanelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings=settings; _strings=new(settings.Language);
        DailyTheme.Apply(this,settings.Theme); Title=_strings["Title"];
        foreach(var localize in _localized) localize(_strings);
        _updatingPreferences=true;
        try
        {
            foreach(ComboBoxItem item in _themeChoice.Items) item.Content=_strings[((ThemePreference)item.Tag).ToString()];
            foreach(ComboBoxItem item in _languageChoice.Items) item.Content=_strings[(LanguagePreference)item.Tag switch { LanguagePreference.ZhCN=>"Chinese", LanguagePreference.EnUS=>"English", _=>"System" }];
            _themeChoice.SelectedIndex=(int)settings.Theme; _languageChoice.SelectedIndex=(int)settings.Language;
        }
        finally { _updatingPreferences=false; }
        _copyLabel.Text=_strings["Copy"]; RenderState();
    }
    public void Update(DailySnapshot snapshot, bool demo=false)
    {
        ArgumentNullException.ThrowIfNull(snapshot); _demo=demo;
        SetState(_state with { Snapshot=snapshot, RefreshFailed=false },DateTimeOffset.Now,TimeZoneInfo.Local);
    }
    public void SetState(DailyPresentationState state, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(zone);
        _state=state; _now=now; _zoneInfo=zone; RenderState();
    }
    public void SetBusy(bool busy) => SetState(_state with { IsBusy=busy },DateTimeOffset.Now,_zoneInfo);
    public void SetDemo(bool demo) { _demo=demo; RenderState(); }

    private void RenderState()
    {
        var view=_state.Project(_now,_zoneInfo); var value=view.Snapshot;
        var day=value?.Day ?? view.CurrentDay;
        _day.Text=(day==view.CurrentDay ? _strings["Today"]+" · " : "")+day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        _zone.Text=DailyPresentationFormatting.Zone(day,_zoneInfo,_strings);
        _quality.Text=DailyPresentationFormatting.Quality(view,_strings);
        _activity.Text=DailyPresentationFormatting.Activity(view,_strings,_demo);
        ((UIElement)_activity.Parent).Visibility=_activity.Text.Length==0 ? Visibility.Collapsed : Visibility.Visible;
        _quality.SetResourceReference(TextBlock.ForegroundProperty,value?.IsPartial==true || view.IsStale ? "Panel.Warning" : "Panel.Secondary");
        Tokens(_total,_totalExact,DailyFormatting.TotalTokens(value?.InputTokens,value?.OutputTokens));
        Tokens(_input,_inputExact,value?.InputTokens); Tokens(_output,_outputExact,value?.OutputTokens);
        _tps.Text=DailyFormatting.Tps(value?.AverageTps); _processing.Text=DailyFormatting.Duration(value?.ProcessingSeconds);
        _processing.ToolTip=DailyPresentationFormatting.Seconds(value?.ProcessingSeconds,_strings);
        _cache.Text=DailyFormatting.CacheRate(value?.InputTokens,value?.CachedInputTokens);
        _reasoning.Text=DailyPresentationFormatting.Exact(value?.ReasoningTokens,_strings);
        _uncached.Text=DailyPresentationFormatting.Exact(DailyFormatting.Uncached(value?.InputTokens,value?.CachedInputTokens),_strings);
        _active.Text=DailyFormatting.Duration(value?.ActiveSeconds);
        _counts.Text=$"{_strings["Chats"]}: {value?.ChatCount ?? 0} · {_strings["Turns"]}: {value?.TurnCount ?? 0} · {_strings["ActiveTurns"]}: {DailyPresentationFormatting.Exact(view.DisplayActiveTurns,_strings)}";
        _timing.Text=_strings.Format("Coverage",value?.TimedTurnCount ?? 0,value?.ExcludedTimingTurnCount ?? 0);
        if(view.DisplayWeekly is { } weekly && double.IsFinite(weekly.UsedPercent) && weekly.UsedPercent is >=0 and <=100)
        {
            _weekly.Text=_strings.Format("Remaining",(100-weekly.UsedPercent).ToString("0.#",CultureInfo.InvariantCulture));
            _weeklyObserved.Text=_strings.Format("Observed",DailyPresentationFormatting.Stamp(weekly.ObservedAt,_zoneInfo));
            _weeklyReset.Text=_strings.Format("Reset",weekly.ResetsAt is { } at ? DailyPresentationFormatting.Stamp(at,_zoneInfo) : _strings["Unavailable"]);
        }
        else { _weekly.Text=_strings["NoWeekly"]; _weeklyObserved.Text=""; _weeklyReset.Text=""; }
        _note.Text=DailyPresentationFormatting.Reasons(view,_strings); _note.Visibility=_note.Text.Length==0 ? Visibility.Collapsed : Visibility.Visible;
        _note.SetResourceReference(TextBlock.ForegroundProperty,"Panel.Warning");
        _updated.Text=value is null ? _strings["NotUpdated"] : _strings.Format("Updated",DailyPresentationFormatting.Stamp(value.UpdatedAt,_zoneInfo));
        // Manual intent remains available during a read; the controller coalesces it.
        _refreshButton.IsEnabled=!_state.IsExiting;
        _refreshButton.ToolTip=_state.IsBusy ? _strings["RefreshPending"] : _strings["Refresh"];
        _copyButton.IsEnabled=value is not null && !_state.IsExiting;
    }
    private void PreferencesSelected(object sender, SelectionChangedEventArgs args)
    {
        if(_updatingPreferences || _themeChoice.SelectedItem is not ComboBoxItem theme || _languageChoice.SelectedItem is not ComboBoxItem language) return;
        var settings=new PanelSettings(1,(ThemePreference)theme.Tag,(LanguagePreference)language.Tag);
        Configure(settings); SettingsChanged?.Invoke(settings);
    }
    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if(!_closed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => { if(!_closed && _settings.Theme==ThemePreference.System) DailyTheme.Apply(this,_settings.Theme); }));
    }
    public void CopyStats()
    {
        if(_state.Snapshot is null || _state.IsExiting) return;
        // The copy action may occur between timer ticks or after midnight while paused.
        _now=DateTimeOffset.Now;
        RenderState();
        try { Clipboard.SetText(CopyText); _copyLabel.Text=_strings["Copied"]; }
        catch(ExternalException) { _copyLabel.Text=_strings["CopyFailed"]; }
        _copyFeedbackTimer.Stop(); _copyFeedbackTimer.Start();
    }
    private void Tokens(TextBlock compact,TextBlock exact,long? value)
    {
        compact.Text=DailyFormatting.Tokens(value); exact.Text=DailyPresentationFormatting.Exact(value,_strings);
        compact.ToolTip=exact.Text;
    }
    private FrameworkElement PreferenceRow(string key,ComboBox choice)
    {
        choice.Margin=new Thickness(8,4,0,4); choice.MinWidth=140;
        _localized.Add(s => AutomationProperties.SetName(choice,s[key]));
        return TwoColumns(LocalLabel(key,12),choice);
    }
    private FrameworkElement DetailRow(string key,TextBlock value)
    {
        value.HorizontalAlignment=HorizontalAlignment.Right; var row=TwoColumns(LocalLabel(key,12),value); row.Margin=new Thickness(0,4,0,4); return row;
    }
    private TextBlock LocalLabel(string key,double size,bool primary=false)
    {
        var label=Label("",size,primary); _localized.Add(s=>label.Text=s[key]); return label;
    }
    private Button ActionButton(object content,string key,string id)
    {
        var button=new Button { Content=content, MinHeight=36, FontSize=16 }; SetId(button,id);
        _localized.Add(s=>{ button.ToolTip=s[key]; AutomationProperties.SetName(button,s[key]); }); return button;
    }
    private FrameworkElement TokenBlock(string key,TextBlock value,TextBlock? exact)
    {
        var panel=new StackPanel(); panel.Children.Add(LocalLabel(key,12)); panel.Children.Add(value); if(exact is not null) panel.Children.Add(exact); return panel;
    }
    private static TextBlock Label(string text,double size,bool primary=false)
    {
        var value=new TextBlock { Text=text, FontSize=size, TextWrapping=TextWrapping.Wrap, VerticalAlignment=VerticalAlignment.Center, FontWeight=primary ? FontWeights.SemiBold : FontWeights.Normal };
        value.SetResourceReference(TextBlock.ForegroundProperty,primary ? "Panel.Text" : "Panel.Secondary"); return value;
    }
    private static Border Badge(TextBlock label)
    {
        var border=new Border { Child=label, Padding=new Thickness(8,4,8,4), Margin=new Thickness(0,0,8,0), CornerRadius=new CornerRadius(6) };
        border.SetResourceReference(Border.BackgroundProperty,"Panel.Elevated"); return border;
    }
    private static Border Card(UIElement child)
    {
        var border=new Border { Child=child, Padding=new Thickness(12), CornerRadius=new CornerRadius(8) };
        border.SetResourceReference(Border.BackgroundProperty,"Panel.Elevated"); return border;
    }
    private static Grid TwoColumns(UIElement first,UIElement second)
    {
        var grid=new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(12) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(first); Grid.SetColumn(second,2); grid.Children.Add(second); return grid;
    }
    private static void SetId(DependencyObject target,string id) => AutomationProperties.SetAutomationId(target,id);
}

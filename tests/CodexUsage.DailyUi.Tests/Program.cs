using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static int _failures;
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (args.Length == 2 && args[0] == "--render-matrix")
        {
            var result = RenderMatrix.Run(args[1]);
            app.Shutdown();
            return result;
        }
        Test("reading does not hide partial quality", window =>
        {
            Configure(window, "Light", "EnUS");
            window.SetState(new() { Snapshot=Fixture() with { IsPartial=true }, IsBusy=true },Now,Zone);
            var texts = Texts(window);
            Require(texts.Contains("Partial") && texts.Contains("Refreshing"), "Expected independent Partial and Refreshing indicators");
        });
        Test("English unknown copy is consistently English", window =>
        {
            Configure(window, "Light", "EnUS");
            window.Update(Fixture() with { InputTokens = null, OutputTokens = null });
            Require(window.CopyText.Contains("Total tokens: Unavailable", StringComparison.Ordinal), "Unknown total needs localized explicit unavailable");
            Require(!window.CopyText.Any(ch => ch >= '\u4e00' && ch <= '\u9fff'), "English copy contains Chinese UI text");
        });
        Test("dark theme applies contrast-safe resources", window =>
        {
            Configure(window, "Dark", "EnUS");
            var surface = (SolidColorBrush?)window.TryFindResource("Panel.Surface");
            var text = (SolidColorBrush?)window.TryFindResource("Panel.Text");
            Require(surface is not null && text is not null, "Theme resources missing");
            Require(surface!.Color.R < 40 && surface.Color.G < 40 && surface.Color.B < 40, "Dark background must be dark");
            Require(Contrast(surface.Color, text!.Color) >= 7, "Primary text contrast below 7:1");
        });
        Test("footer actions expose stable automation IDs and names", window =>
        {
            var buttons = Descendants(window).OfType<Button>().ToArray();
            foreach (var id in new[] { "CopyStats", "RefreshStats", "OpenLogs", "ExitPanel" })
            {
                var matches = buttons.Where(b => AutomationProperties.GetAutomationId(b) == id).ToArray();
                Require(matches.Length == 1 && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(matches[0])), "Missing unique named action " + id);
            }
        });
        Test("manual refresh remains available while a read is in flight", window =>
        {
            window.SetState(new() { Snapshot=Fixture(), IsBusy=true },Now,Zone);
            var button=ById<Button>(window,"RefreshStats");
            Require(button.IsEnabled,"Busy UI must permit the coalesced manual request");
            var requests=0; window.RefreshRequested+=()=>requests++;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(requests==1,"Manual refresh was not routed");
        });
        Test("known zero differs from unknown in values and copy", window =>
        {
            Configure(window,"Light","EnUS");
            window.SetState(new() { Snapshot=Fixture() with { InputTokens=0, OutputTokens=0 } },Now,Zone);
            Require(ById<TextBlock>(window,"TotalTokens").Text=="0" && window.CopyText.Contains("Total tokens: 0"),"Known zero lost");
            window.SetState(new() { Snapshot=Fixture() with { InputTokens=null, OutputTokens=null } },Now,Zone);
            Require(ById<TextBlock>(window,"TotalTokens").Text=="—" && window.CopyText.Contains("Total tokens: Unavailable"),"Unknown must not become zero");
        });
        foreach (var language in new[] { LanguagePreference.EnUS, LanguagePreference.ZhCN })
        {
            var chinese = language == LanguagePreference.ZhCN;
            var title = chinese ? "Codex 用量" : "Codex Usage";
            var noData = chinese ? "暂无数据" : "No data";
            var refreshFailure = chinese
                ? "最近一次刷新失败；保留快照仍使用原日期与时间。"
                : "The last refresh failed; any retained snapshot keeps its original date and time.";

            TestCopyActions("first read shows unknown counts and can be copied", language, new() { IsBusy = true }, Now,
                window => RequireUnknownBreakdown(window, chinese),
                copy => Require(copy.TrimEnd() == string.Join(Environment.NewLine, title, noData),
                    "Waiting copy must contain the localized title and no-data text without snapshot fields"));

            TestCopyActions("first read failure can be copied with its reason", language, new() { RefreshFailed = true }, Now,
                window =>
                {
                    RequireUnknownBreakdown(window, chinese);
                    Require(ById<TextBlock>(window, "DataIssues").Text.Contains(refreshFailure, StringComparison.Ordinal),
                        "The first refresh failure reason is missing from the panel");
                },
                copy => Require(copy.TrimEnd() == string.Join(Environment.NewLine, title, noData, refreshFailure),
                    "First failure copy must keep the localized reason without inventing snapshot fields"));

            var zero = Fixture() with
            {
                InputTokens = 0, CachedInputTokens = 0, OutputTokens = 0, ReasoningTokens = 0,
                ChatCount = 0, TurnCount = 0, TimedTurnCount = 0, ExcludedTimingTurnCount = 0,
                ProcessingSeconds = null, ActiveSeconds = null, AverageTps = null
            };
            var zeroCounts = chinese ? "会话: 0 · 回合: 0 · 运行中回合: 0" : "Chats: 0 · Turns: 0 · Active turns: 0";
            var zeroCoverage = chinese ? "TPS：纳入 0 / 排除 0" : "TPS: 0 included / 0 excluded";
            TestCopyActions("known zero is numeric in the panel and in copy", language, new() { Snapshot = zero }, Now,
                window =>
                {
                    var texts = BreakdownTexts(window);
                    Require(texts.Contains(zeroCounts) && texts.Contains(zeroCoverage), "Known zero counts or coverage are missing");
                    Require(ById<TextBlock>(window, "TotalTokens").Text == "0", "Known zero must be shown as a numeric total");
                },
                copy =>
                {
                    RequireCopyLine(copy, chinese ? "总 token: 0" : "Total tokens: 0");
                    RequireCopyLine(copy, chinese ? "会话: 0" : "Chats: 0");
                    RequireCopyLine(copy, chinese ? "回合: 0" : "Turns: 0");
                    RequireCopyLine(copy, zeroCoverage);
                });

            var partial = Fixture() with
            {
                InputTokens = 1_234_567, OutputTokens = 890, UpdatedAt = Now.AddMinutes(-1),
                IsPartial = true, Issues = [new(DailyIssueCodes.CoverageLost)]
            };
            var partialReason = chinese
                ? "日志截断、替换或删除后，当日覆盖无法确认；仅显示可核验小计。"
                : "Daily coverage cannot be confirmed after truncation, replacement or deletion; showing the verifiable subtotal.";
            var partialLabel = chinese ? "统计不完整" : "Partial";
            var staleLabel = chinese ? "数据已过期" : "Stale";
            var staleDayReason = chinese ? "保留的快照属于 2026-10-23，并非今日数据。"
                : "The retained snapshot belongs to 2026-10-23, not the current day.";
            var today = chinese ? "今日" : "Today";
            var todayTrayTotal = chinese ? "今日总 token 1.24M" : "Daily total 1.24M tokens";
            var datedTrayTotal = chinese ? "2026-10-23 快照：1.24M token" : "Snapshot 2026-10-23: 1.24M tokens";
            var midnight = Now.AddHours(12);
            TestCopyActions("partial copy retains exact statistics and reevaluates the date", language, new() { Snapshot = partial }, midnight,
                window =>
                {
                    Require(ById<TextBlock>(window, "DataQuality").Text == partialLabel, "Partial quality is missing before the day changes");
                    Require(Texts(window).Contains($"{today} · 2026-10-23"), "Snapshot starts on the current day");
                    Require(Texts(window).Contains("1,235,457"), "Exact partial total is missing from the panel");
                },
                copy =>
                {
                    RequireCopyLine(copy, chinese ? "总输入: 1,234,567" : "Total input: 1,234,567");
                    RequireCopyLine(copy, chinese ? "总输出: 890" : "Total output: 890");
                    RequireCopyLine(copy, chinese ? "总 token: 1,235,457" : "Total tokens: 1,235,457");
                    RequireCopyLine(copy, chinese ? "快照日期: 2026-10-23 · 本地时间 · UTC+08:00" : "Snapshot date: 2026-10-23 · Local time · UTC+08:00");
                    RequireCopyLine(copy, chinese ? "更新于：2026-10-23 11:59:00 +08:00" : "Updated: 2026-10-23 11:59:00 +08:00");
                    RequireCopyLine(copy, chinese ? $"状态: {staleLabel} · {partialLabel}" : $"Status: {staleLabel} · {partialLabel}");
                    RequireCopyLine(copy, partialReason);
                    RequireCopyLine(copy, staleDayReason);
                },
                displayedAt: midnight.AddTicks(-1),
                trayCheck: (title + " · " + todayTrayTotal + " · " + partialLabel, (window, tooltip) =>
                {
                    Require(tooltip.Contains(datedTrayTotal, StringComparison.Ordinal)
                        && !tooltip.Contains(todayTrayTotal, StringComparison.Ordinal), "Tray copy left yesterday's subtotal labeled as today");
                    Require(tooltip.Contains($"{staleLabel} · {partialLabel}", StringComparison.Ordinal), "Tray quality differs from the copied quality");
                    Require(ById<TextBlock>(window, "DataQuality").Text == $"{staleLabel} · {partialLabel}"
                        && Texts(window).Contains("2026-10-23"), "Window did not update to the same dated snapshot");
                    Require(ById<TextBlock>(window, "DataIssues").Text.Contains(staleDayReason, StringComparison.Ordinal),
                        "The updated panel is missing the day-change reason");
                }));

            var sourceLimit = DailyIssue.With(DailyIssueCodes.SourceLimitReached, ("limit", "32"));
            var capped = Fixture() with
            {
                InputTokens = 3_200, OutputTokens = 800, SourceCount = 32, IsPartial = true, Issues = [sourceLimit]
            };
            var limitReason = chinese ? "最多读取 32 个日志来源，仅显示可核验小计。"
                : "At most 32 log sources are read. Showing the verifiable subtotal.";
            var unreadable = chinese ? "部分日志不可读取" : "Some logs cannot be read";
            TestCopyActions("source limit explains the partial subtotal in the panel and copy", language,
                new() { Snapshot = capped }, Now,
                window =>
                {
                    var visibleReasons = ById<TextBlock>(window, "DataIssues").Text;
                    Require(ById<TextBlock>(window, "DataQuality").Text == partialLabel
                        && ById<TextBlock>(window, "TotalTokens").Text == "4,000", "The panel lost the partial subtotal");
                    Require(visibleReasons.Contains(limitReason, StringComparison.Ordinal)
                        && !visibleReasons.Contains(unreadable, StringComparison.Ordinal), "The panel misclassified the source limit");
                },
                copy =>
                {
                    RequireCopyLine(copy, chinese ? "总 token: 4,000" : "Total tokens: 4,000");
                    RequireCopyLine(copy, chinese ? "日志来源: 32" : "Sources: 32");
                    RequireCopyLine(copy, limitReason);
                    Require(!copy.Contains(unreadable, StringComparison.Ordinal), "Copy reported a false read failure");
                });
        }
        TestCopyActions("copy actions do not write during exit", LanguagePreference.EnUS,
            new() { Snapshot = Fixture(), IsExiting = true }, Now, _ => { }, checkCopy: null);
        TestTrayCopyControllerExitGuard();
        TestRefreshAndExit();
        Test("partial paused and busy states coexist without a current running claim", window =>
        {
            Configure(window,"Light","EnUS");
            window.SetState(new() { Snapshot=Fixture() with { IsPartial=true,ActiveTurns=3,Issues=[DailyIssue.With(DailyIssueCodes.ObservedActive,("count","3"))] },IsPaused=true,IsBusy=true },Now,Zone);
            Require(ById<TextBlock>(window,"DataQuality").Text=="Partial","Quality hidden");
            Require(ById<TextBlock>(window,"UiActivity").Text=="Paused · Refreshing","Activity states must coexist");
            Require(window.CopyText.Contains("Active turns: Unavailable") && !window.CopyText.Contains("Observed 3 active"),"Stale running observation leaked");
        });
        Test("whole refresh failure retains exact snapshot time and flags stale", window =>
        {
            Configure(window,"Light","EnUS");
            window.SetState(new() { Snapshot=Fixture(),RefreshFailed=true,LastAttemptAt=Now.AddMinutes(2) },Now.AddMinutes(2),Zone);
            Require(ById<TextBlock>(window,"DataQuality").Text.Contains("Stale"),"Missing stale badge");
            Require(window.CopyText.Contains("Total tokens: 140") && window.CopyText.Contains("Updated: 2026-10-23 12:00:00 +08:00"),"Retained number or timestamp changed");
            Require(window.CopyText.Contains("last refresh failed"),"Failure reason missing");
        });
        Test("a new partial snapshot replaces a prior full total", window =>
        {
            Configure(window,"Light","EnUS");
            window.SetState(new() { Snapshot=Fixture() },Now,Zone);
            window.SetState(new() { Snapshot=Fixture() with {InputTokens=20,OutputTokens=8,IsPartial=true,Issues=[new(DailyIssueCodes.CoverageLost)]} },Now,Zone);
            Require(ById<TextBlock>(window,"TotalTokens").Text=="28" && window.CopyText.Contains("Total tokens: 28"),"Prior full total retained over current partial");
            Require(window.CopyText.Contains("truncation, replacement or deletion"),"Coverage reason lost");
        });
        Test("paused cross-day snapshot is explicitly dated", window =>
        {
            Configure(window,"Light","EnUS");
            window.SetState(new() { Snapshot=Fixture(),IsPaused=true },Now.AddDays(1),Zone);
            Require(!Texts(window).Any(x=>x.StartsWith("Today · 2026-10-23")),"Yesterday mislabeled today");
            Require(window.CopyText.Contains("Snapshot date: 2026-10-23") && window.CopyText.Contains("not the current day"),"Original date or day reason lost");
        });
        Test("weekly reset expires in UI and copy while paused", window =>
        {
            Configure(window,"Light","EnUS");
            var state=new DailyPresentationState {Snapshot=Fixture() with {Weekly=new(25,Now.AddMinutes(1),Now)},IsPaused=true};
            window.SetState(state,Now,Zone);
            Require(window.CopyText.Contains("75% remaining"),"Valid quota missing");
            window.SetState(state,Now.AddMinutes(1),Zone);
            Require(!window.CopyText.Contains("75% remaining") && window.CopyText.Contains("weekly log snapshot has expired"),"Expired quota retained");
            Require(Texts(window).Contains("No valid weekly snapshot"),"UI/copy expiry mismatch");
        });
        Test("language changes localize visible reasons and copy together", window =>
        {
            var state=new DailyPresentationState {Snapshot=Fixture() with {IsPartial=true,Issues=[new(DailyIssueCodes.SourceUnreadable)]}};
            window.SetState(state,Now,Zone);
            Configure(window,"Dark","ZhCN");
            Require(window.CopyText.Contains("总 token：") || window.CopyText.Contains("总 token: 140"),"Chinese count label missing");
            Require(ById<TextBlock>(window,"DataQuality").Text=="统计不完整" && window.CopyText.Contains("部分日志不可读取"),"Chinese quality mismatch");
            Configure(window,"Light","EnUS");
            Require(!window.CopyText.Any(ch=>ch>='\u4e00' && ch<='\u9fff'),"English copy contains built-in Chinese");
            Require(ById<TextBlock>(window,"DataIssues").Text.Contains("Some logs cannot be read"),"Visible issue not translated");
        });
        Test("all diagnostic codes have localized text rather than key fallbacks", window =>
        {
            var codes=typeof(DailyIssueCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Select(f=>(string)f.GetRawConstantValue()!).ToArray();
            window.SetState(new() {Snapshot=Fixture() with {IsPartial=true,Issues=codes.Select(c=>DailyIssue.With(c,("count","2"),("included","1"),("excluded","1"),("limitMiB","16"),("limit","32"),("day","2026-10-22"))).ToArray()}},Now,Zone);
            foreach(var language in new[]{"EnUS","ZhCN"})
            {
                Configure(window,"Light",language);
                Require(!window.CopyText.Contains("Diagnostic:") && !window.CopyText.Contains("诊断：") && !window.CopyText.Contains("{count}") && !window.CopyText.Contains("{limit}"),"Known code fallback or missing parameter");
            }
        });
        Test("unknown diagnostic remains visible without inventing semantics", window =>
        {
            Configure(window,"Light","EnUS");
            window.SetState(new(){Snapshot=Fixture() with {Issues=[new("future.example")]}},Now,Zone);
            Require(window.CopyText.Contains("Diagnostic: future.example"),"Unknown diagnostic silently discarded");
            Require(ById<TextBlock>(window,"DataQuality").Text=="Local","Unknown informational issue changed data quality");
        });
        Test("preference controls emit changes and support isolated persistence", window =>
        {
            var directory=Path.Combine(Path.GetTempPath(),"daily-ui-settings-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); var store=new PanelSettingsStore(directory);
            var changes=0; window.SettingsChanged+=s=>{store.Save(s);changes++;};
            ById<Expander>(window,"Preferences").IsExpanded=true; window.UpdateLayout();
            ById<ComboBox>(window,"ThemePreference").SelectedIndex=(int)ThemePreference.Dark;
            ById<ComboBox>(window,"LanguagePreference").SelectedIndex=(int)LanguagePreference.ZhCN;
            Require(changes==2 && store.Load().Settings==new PanelSettings(1,ThemePreference.Dark,LanguagePreference.ZhCN),"Preference selection or persistence mismatch");
            Require(((SolidColorBrush)window.FindResource("Panel.Surface")).Color==Color.FromRgb(24,24,24),"Selected theme not applied");
        });
        Test("settings errors remain separate from statistics quality", window =>
        {
            Configure(window,"Light","EnUS");
            window.SetState(new(){Snapshot=Fixture(),Issues=[new("settings.write_failed")]},Now,Zone);
            Require(ById<TextBlock>(window,"DataQuality").Text=="Local","Settings failure became partial statistics");
            Require(window.CopyText.Contains("Preferences could not be read or saved"),"Nonfatal preferences message missing");
        });
        Test("small work area keeps footer reachable and body scrollable", window =>
        {
            window.Width=360;window.Height=360;
            window.SetState(new(){Snapshot=Fixture() with {IsPartial=true,Issues=[new(DailyIssueCodes.CoverageLost)]}},Now,Zone);
            ById<Expander>(window,"StatisticsDetails").IsExpanded=true;
            ById<Expander>(window,"Preferences").IsExpanded=true;window.UpdateLayout();
            var scroll=ById<ScrollViewer>(window,"StatisticsScroll");
            Require(scroll.ScrollableHeight>0,"Long body cannot scroll");
            foreach(var id in new[]{"CopyStats","RefreshStats","OpenLogs","ExitPanel"})
            {
                var button=ById<Button>(window,id);var point=button.TranslatePoint(new Point(),window);
                Require(button.IsVisible && button.Focusable && point.Y>=0 && point.Y+button.ActualHeight<=window.ActualHeight,"Unreachable footer "+id);
            }
        });
        Test("system language mapping retains explicit user preference", window =>
        {
            Require(new DailyStrings(LanguagePreference.System,"zh-HK").Language==LanguagePreference.ZhCN,"Chinese system fallback");
            Require(new DailyStrings(LanguagePreference.System,"fr-FR").Language==LanguagePreference.EnUS,"Non-Chinese system fallback");
            Require(new DailyStrings(LanguagePreference.EnUS,"zh-CN").Language==LanguagePreference.EnUS,"Explicit preference overwritten");
        });
        Test("dark preference selection uses a dark rendered surface", window =>
        {
            Configure(window,"Dark","EnUS");
            ById<Expander>(window,"Preferences").IsExpanded=true;window.UpdateLayout();
            ById<ScrollViewer>(window,"StatisticsScroll").ScrollToEnd();window.UpdateLayout();
            var choice=ById<ComboBox>(window,"ThemePreference");
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(window);
            var point=choice.TranslatePoint(new Point(choice.ActualWidth*.65,choice.ActualHeight*.5),window);
            var pixel=new byte[4];bitmap.CopyPixels(new Int32Rect((int)point.X,(int)point.Y,1,1),pixel,4,0);
            Require(pixel[3]==255 && pixel[0]<80 && pixel[1]<80 && pixel[2]<80,"Native light ComboBox surface makes dark-theme text unreadable");
        });
        Test("themed preference popup exposes all choices on a dark surface", window =>
        {
            Configure(window,"Dark","EnUS");
            ById<Expander>(window,"Preferences").IsExpanded=true;window.UpdateLayout();
            ById<ScrollViewer>(window,"StatisticsScroll").ScrollToEnd();window.UpdateLayout();
            var choice=ById<ComboBox>(window,"ThemePreference");
            choice.IsDropDownOpen=true;
            window.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
            var popup=(Popup)choice.Template.FindName("PART_Popup",choice);
            var border=(Border)popup.Child;
            Require(popup.IsOpen && choice.Items.Count==3,"Theme choices inaccessible");
            Require(border.Background is SolidColorBrush b && b.Color==Color.FromRgb(24,24,24),"Popup background not themed");
            choice.IsDropDownOpen=false;
        });
        app.Shutdown();
        Console.WriteLine($"{_checks - _failures}/{_checks} UI behavior cases passed");
        return _failures == 0 ? 0 : 1;
    }

    internal static readonly DateTimeOffset Now=new(2026,10,23,12,0,0,TimeSpan.FromHours(8));
    internal static readonly TimeZoneInfo Zone=TimeZoneInfo.CreateCustomTimeZone("Synthetic UTC+08",TimeSpan.FromHours(8),"Synthetic UTC+08","Synthetic UTC+08");
    internal static DailySnapshot Fixture() => new(new DateOnly(2026, 10, 23),
        new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8)), 100, 60, 40, 10, 9, 10,
        40d / 9, 1, 1, 0, 1, false, "", null) { TimedTurnCount = 1 };

    private static void Configure(DailyWindow window, string theme, string language)
    {
        var asm = typeof(DailyWindow).Assembly;
        var settingType = asm.GetType("CodexUsage.DailyPanel.PanelSettings");
        Require(settingType is not null, "Theme/language preferences have no product surface yet");
        var themeType = asm.GetType("CodexUsage.DailyPanel.ThemePreference", true)!;
        var languageType = asm.GetType("CodexUsage.DailyPanel.LanguagePreference", true)!;
        var settings = Activator.CreateInstance(settingType!, 1, Enum.Parse(themeType, theme), Enum.Parse(languageType, language));
        var method = typeof(DailyWindow).GetMethod("Configure");
        Require(method is not null, "Window cannot apply preferences yet");
        method!.Invoke(window, new[] { settings });
    }
    private static void TestCopyActions(string name, LanguagePreference language, DailyPresentationState state,
        DateTimeOffset clickTime, Action<DailyWindow> checkView, Action<string>? checkCopy,
        DateTimeOffset? displayedAt = null, (string BeforeClick, Action<DailyWindow, string> Verify)? trayCheck = null)
    {
        var writes = new List<string>();
        var clockReads = 0;
        var window = new DailyWindow(() => { clockReads++; return clickTime; }, writes.Add);
        Test($"{name} ({language})", view =>
        {
            using var controller = CreateCopyTestController(view, language);
            var tray = GetControllerTray(controller);
            var trayCopy = GetTrayCopyItem(tray, language);
            var panelCopy = ById<Button>(view, "CopyStats");

            foreach (var (entry, click) in new (string, Action)[]
            {
                ("panel", () => panelCopy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))),
                ("tray", trayCopy.PerformClick)
            })
            {
                view.SetState(state, displayedAt ?? Now, Zone);
                if (entry == "panel") checkView(view);
                else
                {
                    view.Hide();
                    if (trayCheck is { } expected) tray.Text = expected.BeforeClick;
                }
                Require(panelCopy.IsEnabled == (checkCopy is not null), "Unexpected copy button availability");
                writes.Clear();
                clockReads = 0;
                click();
                Require(writes.Count == (checkCopy is null ? 0 : 1), $"Unexpected clipboard writes from {entry}");
                Require(clockReads == (checkCopy is null ? 0 : 1), $"Unexpected clock reads from {entry}");
                if (checkCopy is not null) checkCopy(writes.Single());
                if (entry == "tray" && trayCheck is { } check) check.Verify(view, tray.Text);
            }
        }, window);
    }

    private static void TestTrayCopyControllerExitGuard()
    {
        var writes = new List<string>();
        var clickTime = Now;
        Test("tray copy stops at controller exit before the window enters exit", view =>
        {
            using var controller = CreateCopyTestController(view, LanguagePreference.EnUS);
            var tray = GetControllerTray(controller);
            var trayCopy = GetTrayCopyItem(tray, LanguagePreference.EnUS);
            view.SetState(new() { Snapshot = Fixture() }, Now, Zone);
            trayCopy.PerformClick();
            Require(writes.Count == 1, "The menu item must copy before controller exit");
            writes.Clear();
            var tooltip = tray.Text;

            var exiting = typeof(DailyController).GetField("_exiting", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Controller exit flag not found");
            // Exercise the interval after the controller starts exiting and before it publishes that state.
            exiting.SetValue(controller, true);
            clickTime = Now.AddHours(12);
            Require(ById<Button>(view, "CopyStats").IsEnabled, "The window's independent exit guard must not mask the controller guard");
            trayCopy.PerformClick();
            Require(writes.Count == 0, "The menu wrote to the clipboard after controller exit started");
            Require(tray.Text == tooltip, "The menu changed the tray tooltip after controller exit started");
        }, new DailyWindow(() => clickTime, writes.Add));
    }

    private static DailyController CreateCopyTestController(DailyWindow window, LanguagePreference language)
    {
        window.Configure(new PanelSettings(1, ThemePreference.Light, language));
        // Use the ordinary menu wiring without starting the controller's monitor.
        var application = Application.Current ?? throw new InvalidOperationException("UI test application is not running");
        var home = Path.Combine(Path.GetTempPath(), "daily-ui-copy-" + Guid.NewGuid().ToString("N"));
        return new DailyController(application, window, home, demo: false);
    }

    private static Forms.NotifyIcon GetControllerTray(DailyController controller)
    {
        var trayField = typeof(DailyController).GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Controller tray icon not found");
        return (Forms.NotifyIcon)trayField.GetValue(controller)!;
    }

    private static Forms.ToolStripMenuItem GetTrayCopyItem(Forms.NotifyIcon tray, LanguagePreference language)
    {
        var menu = tray.ContextMenuStrip ?? throw new InvalidOperationException("Controller tray menu not found");
        return menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item =>
            item.Text == (language == LanguagePreference.ZhCN ? "复制统计" : "Copy stats"));
    }

    private static string[] BreakdownTexts(DailyWindow window)
    {
        ById<Expander>(window, "StatisticsDetails").IsExpanded = true;
        window.UpdateLayout();
        return Texts(window);
    }

    private static void RequireUnknownBreakdown(DailyWindow window, bool chinese)
    {
        var texts = BreakdownTexts(window);
        var counts = chinese ? "会话: 不可用 · 回合: 不可用 · 运行中回合: 不可用" : "Chats: Unavailable · Turns: Unavailable · Active turns: Unavailable";
        Require(texts.Contains(counts), "Unknown session and turn counts must not be displayed as zeros");
        Require(texts.Contains(chinese ? "TPS 覆盖：不可用" : "TPS coverage: Unavailable"), "Coverage should be unavailable until a snapshot exists");
    }

    private static void RequireCopyLine(string copy, string expected) =>
        Require(copy.Split(Environment.NewLine).Contains(expected), "Missing copy line: " + expected);

    private static void Test(string name, Action<DailyWindow> test, DailyWindow? window = null)
    {
        _checks++;
        window ??= new DailyWindow();
        window.ShowActivated = false;
        window.Left = -20000;
        window.Top = -20000;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); window.UpdateLayout();
            test(window);
            Console.WriteLine("PASS " + name);
        }
        catch (Exception e) { _failures++; Console.WriteLine("FAIL " + name + ": " + e.GetBaseException().Message); }
        finally { if (!closed) window.Close(); }
    }
    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    internal static T ById<T>(DependencyObject root,string id) where T:DependencyObject => Descendants(root).OfType<T>().Single(x=>AutomationProperties.GetAutomationId(x)==id);
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Descendants(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static string[] Texts(DailyWindow window) => Descendants(window).OfType<TextBlock>().Select(x => x.Text).ToArray();
    private static double Contrast(Color a, Color b)
    {
        static double C(byte value) { var v = value / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        static double L(Color value) => .2126 * C(value.R) + .7152 * C(value.G) + .0722 * C(value.B);
        return (Math.Max(L(a), L(b)) + .05) / (Math.Min(L(a), L(b)) + .05);
    }
}

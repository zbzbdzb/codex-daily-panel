using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexUsage.Core;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;
using Forms = System.Windows.Forms;

internal static class Program
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<object> Checks = [];
    private static bool Passed = true;
    private static string Output = "";
    private static Application App = null!;
    private static DailyWindow Window = null!;
    private static IDisposable? Controller;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || Directory.Exists(args[0])) return 2;
        Output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Output);
        App = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        App.DispatcherUnhandledException += (_, e) => { SaveError(e.Exception); Check("dispatcher_" + e.Exception.GetType().Name, false); e.Handled = true; App.Shutdown(1); };
        App.Startup += async (_, _) =>
        {
            try { await Run(); }
            catch (Exception e) { SaveError(e); Check("harness_" + e.GetType().Name, false); App.Shutdown(1); }
        };
        var exit = App.Run();
        Controller?.Dispose();
        var report = new { passed = Passed && exit == 0, exit, checks = Checks,
            completedAt = DateTimeOffset.Now,
            appSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(DailyWindow).Assembly.Location))),
            coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(DailySnapshot).Assembly.Location))) };
        File.WriteAllText(Path.Combine(Output, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        return Passed && exit == 0 ? 0 : 1;
    }

    private static async Task Run()
    {
        Window = new DailyWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        App.MainWindow = Window;
        Window.Configure(new PanelSettings(1, ThemePreference.Light, LanguagePreference.EnUS));
        var zone = TimeZoneInfo.CreateCustomTimeZone("GuiHarnessUtcPlus8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8));
        var demo = new DailySnapshot(DateOnly.FromDateTime(now.DateTime), now, 120989545, 117878016, 639259, 353523,
            18252, 18041, 35.02, 4, 38, 0, 4, false, "演示数据 · 不读取真实日志", new WeeklyAllowance(27, now.AddDays(7), now.AddMinutes(-2)))
            { TimedTurnCount = 36, ExcludedTimingTurnCount = 2, ExcludedWaitSeconds = 4800 };
        Window.Update(demo, true);
        Window.SetState(new DailyPresentationState { Snapshot = demo }, now, zone);
        Window.Show();
        await Idle();
        Check("window_460_by_640", Math.Abs(Window.ActualWidth - 460) < 1 && Math.Abs(Window.ActualHeight - 640) < 1);
        Check("popup_has_no_taskbar_entry", !Window.ShowInTaskbar);
        Check("popup_is_topmost", Window.Topmost);
        Check("exact_input_subtitle", Texts().Contains("120,989,545") && ById<TextBlock>("TotalInput").ToolTip?.ToString() == "120,989,545");
        Check("exact_output_subtitle", Texts().Contains("639,259") && ById<TextBlock>("TotalOutput").ToolTip?.ToString() == "639,259");
        Check("total_tokens_visible_and_exact", Texts().Contains("121.63M") && Window.CopyText.Contains("121,628,804"));
        Check("two_decimal_tps", Texts().Contains("35.02"));
        Check("processing_includes_seconds", Texts().Contains("5h 04m 12s"));
        Check("copy_scope_and_precision", Window.CopyText.Contains("120,989,545") && Window.CopyText.Contains("subagents and cloud tasks excluded") && Window.CopyText.Contains("tool wait"));
        Check("weekly_73_percent", Texts().Contains("73% remaining"));
        Check("normal_footer_visible", FooterFits());
        Check("icon_actions_have_accessible_names", new[] { "HidePanel", "CopyStats", "RefreshStats", "OpenLogs", "ExitPanel" }
            .All(id => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(ById<Button>(id)))));
        Check("normal_body_fits", ById<ScrollViewer>("StatisticsScroll").ScrollableHeight < 1);
        Render("demo-640.png");
        ById<Expander>("StatisticsDetails").IsExpanded = true;
        await Idle();
        Check("cache_rate_weighted", Texts().Contains("97.43%"));
        Check("exact_reasoning", Texts().Contains("353,523"));
        Check("expanded_details_scroll_without_hiding_footer", ById<ScrollViewer>("StatisticsScroll").ScrollableHeight > 0 && FooterFits());
        ById<Expander>("Preferences").IsExpanded = true;
        ById<ScrollViewer>("StatisticsScroll").ScrollToBottom();
        await Idle();
        Check("preferences_expanded_accessible_and_configured", ById<ComboBox>("ThemePreference").IsVisible
            && ById<ComboBox>("LanguagePreference").IsVisible
            && (ThemePreference)((ComboBoxItem)ById<ComboBox>("ThemePreference").SelectedItem).Tag == ThemePreference.Light
            && (LanguagePreference)((ComboBoxItem)ById<ComboBox>("LanguagePreference").SelectedItem).Tag == LanguagePreference.EnUS);
        Render("expanded-details-preferences.png");
        ById<Expander>("Preferences").IsExpanded = false;
        ById<Expander>("StatisticsDetails").IsExpanded = false;
        ById<ScrollViewer>("StatisticsScroll").ScrollToTop();

        // Hand-derived: 100 output tokens / (10 seconds - 2 seconds TTFT) = 12.5 TPS.
        // This catches a displayed/exported fallback to the old full-turn denominator.
        var finished = now.AddMinutes(-1);
        var tokens = new TokenVector(200, 100, 100, 0, 300);
        var adjusted = DailyAggregator.Summarize(new StatsEvent[] {
            new MetadataEvent("timing-ui", finished.AddMinutes(-1), false, null),
            new StartEvent("timing-ui", "turn", finished.AddSeconds(-10)),
            new UsageEvent("timing-ui", "turn", "turn", "request", finished.AddSeconds(-1), 1, tokens, tokens, null),
            new CompletionEvent("timing-ui", "turn", finished, 10000, 2000)
        }, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(finished, zone).DateTime), zone, now);
        Window.SetDemo(false);
        Window.SetState(new DailyPresentationState { Snapshot = adjusted }, now, zone);
        await Idle();
        Check("adjusted_timing_reaches_visible_tps_and_duration", Texts().Contains("12.50") && Texts().Contains("8.0s")
            && Window.CopyText.Contains("12.50 tok/s") && Window.CopyText.Contains("8.00 seconds"));

        var empty = demo with { InputTokens=null, CachedInputTokens=null, OutputTokens=null, ReasoningTokens=null, AverageTps=null,
            ProcessingSeconds=null, ActiveSeconds=null, Weekly=null, ChatCount=0, TurnCount=0, SourceCount=0, Status="暂无今日可核验 token 记录",
            TimedTurnCount=0, ExcludedTimingTurnCount=0, ExcludedWaitSeconds=null, Issues=[new DailyIssue(DailyIssueCodes.NoTokens)] };
        Window.SetState(new DailyPresentationState { Snapshot = empty }, now, zone);
        await Idle();
        Check("unknown_does_not_become_zero", ById<TextBlock>("TotalTokens").Text == "—" && !Texts().Contains("639.3K") && Window.CopyText.Contains("Total tokens: Unavailable", StringComparison.Ordinal));
        Check("empty_badge", Texts().Contains("No data"));
        Render("empty.png");

        var legacyPartial = demo with { IsPartial=true, Status="部分统计暂不可用；这是用于检查长状态提示和可读性的测试。", ActiveTurns=12 };
        Window.SetState(new DailyPresentationState { Snapshot = legacyPartial, IsBusy = true }, now, zone);
        await Idle();
        var manualRequests = 0;
        void Requested() => manualRequests++;
        Window.RefreshRequested += Requested;
        if (ById<Button>("RefreshStats").IsEnabled) ById<Button>("RefreshStats").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Window.RefreshRequested -= Requested;
        Check("busy_manual_refresh_remains_actionable", ById<Button>("RefreshStats").IsEnabled
            && ById<TextBlock>("UiActivity").Text.Contains("Refreshing", StringComparison.Ordinal) && manualRequests == 1);
        Window.SetState(new DailyPresentationState { Snapshot = legacyPartial }, now, zone);
        await Idle();
        Check("partial_badge_and_legacy_notice_compatibility", ById<TextBlock>("DataQuality").Text.Contains("Partial", StringComparison.Ordinal)
            && ById<TextBlock>("DataIssues").Text.Contains("部分统计暂不可用", StringComparison.Ordinal)
            && Window.CopyText.Contains("部分统计暂不可用", StringComparison.Ordinal));
        Render("partial.png");
        var structuredPartial = legacyPartial with { Issues=[new DailyIssue(DailyIssueCodes.SourceUnreadable)] };
        Window.SetState(new DailyPresentationState { Snapshot = structuredPartial }, now, zone);
        await Idle();
        Check("structured_partial_is_localized_without_guessing_status", ById<TextBlock>("DataQuality").Text == "Partial"
            && ById<TextBlock>("DataIssues").Text.Contains("log", StringComparison.OrdinalIgnoreCase)
            && !ById<TextBlock>("DataIssues").Text.Contains("部分统计暂不可用", StringComparison.Ordinal)
            && Window.CopyText.Contains("verifiable subtotal", StringComparison.Ordinal));
        ById<Expander>("StatisticsDetails").IsExpanded = true;
        Window.Height = 420;
        await Idle();
        Check("small_work_area_footer_visible", FooterFits());
        var scroller = ById<ScrollViewer>("StatisticsScroll");
        Check("small_work_area_scrolls", scroller.ScrollableHeight > 0);
        scroller.ScrollToBottom();
        await Idle();
        Check("small_scroller_reaches_details", scroller.VerticalOffset > 0);
        Render("small-420.png");
        Window.Height = 640;

        var type = typeof(DailyWindow).Assembly.GetType("CodexUsage.DailyPanel.DailyController", true)!;
        Controller = (IDisposable)Activator.CreateInstance(type, Flags, null, [App, Window, Path.Combine(Output, "absent-demo-home"), true], null)!;
        type.GetMethod("Start", Flags)!.Invoke(Controller, null);
        await Idle();
        var tray = (Forms.NotifyIcon)type.GetField("_tray", Flags)!.GetValue(Controller)!;
        var menu = (Forms.ContextMenuStrip)type.GetField("_menu", Flags)!.GetValue(Controller)!;
        Check("tray_visible", tray.Visible);
        Check("startup_is_tray_only", !Window.IsVisible && tray.Visible);
        Check("tray_summary", tray.Text.Contains("35.0") && tray.Text.Contains("Daily total 121.63M tokens", StringComparison.Ordinal) && !tray.Text.Contains("output", StringComparison.OrdinalIgnoreCase));
        Window.Close();
        await Idle();
        Check("close_hides_but_retains_tray", !Window.IsVisible && tray.Visible);
        // Inject a synthetic offscreen work area. This tests placement through
        // the controller/native bounds but is not physical-monitor acceptance.
        var syntheticArea = new System.Drawing.Rectangle(-12000, -12000, 3840, 2088);
        var trayPoint = new System.Drawing.Point(syntheticArea.Right - 24, syntheticArea.Bottom - 2);
        var anchorType=typeof(DailyWindow).Assembly.GetType("CodexUsage.DailyPanel.TrayPopupAnchor",true)!;
        var anchorFixture=Activator.CreateInstance(anchorType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [trayPoint, syntheticArea, 144u], null)!;
        type.GetField("_lastTrayPressAnchor",Flags)!.SetValue(Controller,anchorFixture);
        ((Forms.ToolStripMenuItem)menu.Items[0]).PerformClick();
        await Idle();
        Check("tray_open_restores_window", Window.IsVisible);
        var actualAnchor=type.GetField("_lastPopupAnchor",Flags)!.GetValue(Controller)!;
        Check("context_menu_reuses_actual_tray_anchor", (System.Drawing.Point)actualAnchor.GetType().GetProperty("Cursor")!.GetValue(actualAnchor)! == trayPoint);
        Check("popup_stays_inside_synthetic_work_area_geometry_only", InsideArea(syntheticArea));
        type.GetMethod("TogglePopup", Flags)!.Invoke(Controller, null);
        await Idle();
        Check("tray_second_click_hides_popup", !Window.IsVisible && tray.Visible);
        type.GetMethod("TogglePopup", Flags)!.Invoke(Controller, null);
        await Idle();
        Check("tray_next_click_reopens_popup", Window.IsVisible);
        var outside = new Window { Width=30, Height=30, Left=-10000, Top=-10000, ShowInTaskbar=false, WindowStyle=WindowStyle.None, Topmost=true };
        outside.Show();
        outside.Activate();
        await Idle();
        Check("outside_activation_hides_popup", !Window.IsVisible && tray.Visible);
        outside.Close();
        var pause = menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == "Pause automatic refresh");
        var scheduler = (RefreshCoordinator)type.GetField("_scheduler", Flags)!.GetValue(Controller)!;
        pause.PerformClick();
        Check("pause_sets_state", pause.Checked && (bool)type.GetField("_paused", Flags)!.GetValue(Controller)! && scheduler.IsPaused
            && Window.CopyText.Contains("Paused", StringComparison.Ordinal));
        pause.PerformClick();
        Check("resume_clears_state", !pause.Checked && !(bool)type.GetField("_paused", Flags)!.GetValue(Controller)! && !scheduler.IsPaused);
        ((Forms.ToolStripMenuItem)menu.Items[2]).PerformClick();
        await Idle();
        Check("manual_refresh_completed", ((Task)type.GetField("_refresh", Flags)!.GetValue(Controller)!).IsCompletedSuccessfully);
        Check("demo_creates_no_log_directory", !Directory.Exists(Path.Combine(Output, "absent-demo-home")));
        ((Forms.ToolStripMenuItem)menu.Items[menu.Items.Count-1]).PerformClick();
        Check("exit_removes_tray", !tray.Visible);
    }

    private static void Check(string name, bool passed) { Checks.Add(new { name, passed }); Passed &= passed; Console.WriteLine((passed ? "PASS " : "FAIL ") + name); }
    private static void SaveError(Exception error) => File.AppendAllText(Path.Combine(Output, "exceptions.log"), error + Environment.NewLine);
    private static Task Idle() => Window.Dispatcher.InvokeAsync(() => Window.UpdateLayout(), DispatcherPriority.ContextIdle).Task;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i=0; i<VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root,i))) yield return child;
    }
    private static string[] Texts() => Descendants(Window).OfType<TextBlock>().Where(text=>text.IsVisible).Select(text=>text.Text).ToArray();
    private static T ById<T>(string id) where T : DependencyObject => Descendants(Window).OfType<T>().Single(value => AutomationProperties.GetAutomationId(value) == id);
    private static bool FooterFits() => new[] { "CopyStats", "RefreshStats", "OpenLogs", "ExitPanel" }.Select(ById<Button>)
        .All(button => { var point=button.TranslatePoint(new Point(),Window); return button.IsVisible && button.ActualHeight > 0 && point.Y>=0 && point.Y+button.ActualHeight<=Window.ActualHeight; });
    private static bool InsideArea(System.Drawing.Rectangle area)
    {
        GetWindowRect(new WindowInteropHelper(Window).Handle,out var rectangle);
        return rectangle.Left>=area.Left && rectangle.Top>=area.Top && rectangle.Right<=area.Right && rectangle.Bottom<=area.Bottom;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window,out NativeRect rect);
    private static void Render(string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(Window.ActualWidth*1.5),(int)Math.Ceiling(Window.ActualHeight*1.5),144,144,PixelFormats.Pbgra32);
        bitmap.Render(Window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Output,name)); encoder.Save(file);
    }
}

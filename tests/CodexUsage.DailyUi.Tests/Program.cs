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

internal static class Program
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
            window.SetState(new() {Snapshot=Fixture() with {IsPartial=true,Issues=codes.Select(c=>DailyIssue.With(c,("count","2"),("included","1"),("excluded","1"),("limitMiB","16"),("day","2026-10-22"))).ToArray()}},Now,Zone);
            foreach(var language in new[]{"EnUS","ZhCN"})
            {
                Configure(window,"Light",language);
                Require(!window.CopyText.Contains("Diagnostic:") && !window.CopyText.Contains("诊断：") && !window.CopyText.Contains("{count}"),"Known code fallback or missing parameter");
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
    private static void Test(string name, Action<DailyWindow> test)
    {
        _checks++;
        var window = new DailyWindow { ShowActivated = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); window.UpdateLayout();
            test(window);
            Console.WriteLine("PASS " + name);
        }
        catch (Exception e) { _failures++; Console.WriteLine("FAIL " + name + ": " + e.GetBaseException().Message); }
        finally { window.Close(); }
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

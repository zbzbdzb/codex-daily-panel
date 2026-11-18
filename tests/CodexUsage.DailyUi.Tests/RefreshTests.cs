using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void TestRefreshAndExit()
    {
        Test("resume and coalesced refreshes settle after a failed redraw", window =>
        {
            var firstRead = new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            var redrawFailures = 0;
            var failResumeRedraw = false;
            using var controller = CreateRefreshTestController(window,
                _ => ++reads == 1 ? firstRead.Task : Task.FromResult(Fixture() with { OutputTokens = 80 + reads }),
                state =>
                {
                    if (state.IsBusy && failResumeRedraw)
                    {
                        failResumeRedraw = false;
                        redrawFailures++;
                        throw new InvalidOperationException("Synthetic busy redraw failure");
                    }
                    window.SetState(state, Now, Zone);
                });
            try
            {
                var pause = RefreshMenuItem(controller, "Pause automatic refresh");
                pause.PerformClick();
                Require(ById<TextBlock>(window, "UiActivity").Text == "Paused", "Initial pause was not presented");

                failResumeRedraw = true;
                pause.PerformClick();
                PumpUntil(() => reads == 1, "Resume did not reach the reader after a failed redraw");
                Require(redrawFailures == 1, "The controlled resume redraw did not fail");
                Require(!firstRead.Task.IsCompleted, "The source read must still be in flight for this assertion");
                Require(!ControllerField<DailyPresentationState>(controller, "_state").RefreshFailed,
                    "The failed redraw marked the read as failed before the reader returned");

                var refresh = ById<Button>(window, "RefreshStats");
                for (var i = 0; i < 3; i++) refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                pause.PerformClick();
                pause.PerformClick();
                var scheduler = ControllerField<RefreshCoordinator>(controller, "_scheduler");
                Require(scheduler.IsBusy && scheduler.HasPendingRefresh && reads == 1, "Manual and resume requests must queue without overlapping the read");

                firstRead.SetResult(Fixture());
                WaitForRefresh(controller);
                Require(reads == 2 && !scheduler.IsBusy && !scheduler.HasPendingRefresh,
                    "Manual and resume intents must settle as exactly one follow-up");
                RequireCopyLine(window.CopyText, "Total tokens: 182");
                Require(ById<TextBlock>(window, "DataQuality").Text == "Local", "A display failure was treated as a read failure");

                RefreshMenuItem(controller, "Refresh statistics").PerformClick();
                WaitForRefresh(controller);
                Require(reads == 3 && !scheduler.IsBusy, "A later explicit refresh must be admitted and settle");
                RequireCopyLine(window.CopyText, "Total tokens: 183");
            }
            finally { firstRead.TrySetCanceled(); }
        });

        Test("only a failure from the read boundary stales the original snapshot", window =>
        {
            var reads = 0;
            using var controller = CreateRefreshTestController(window, _ => ++reads == 2
                ? Task.FromException<DailySnapshot>(new IOException("Synthetic source read failure"))
                : Task.FromResult(Fixture()),
                state => window.SetState(state, Now, Zone));
            var refresh = ById<Button>(window, "RefreshStats");
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForRefresh(controller);
            Require(ById<TextBlock>(window, "DataQuality").Text == "Local", "The first result should be current");

            controller.HandleOperationFailure(new InvalidOperationException("Synthetic unrelated operation failure"));
            Require(!ControllerField<DailyPresentationState>(controller, "_state").RefreshFailed,
                "An unrelated operation cannot mark the last read as failed");

            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForRefresh(controller);
            Require(reads == 2 && ById<TextBlock>(window, "DataQuality").Text == "Stale", "The failed read should stale the retained result");
            RequireCopyLine(window.CopyText, "Snapshot date: 2026-10-23 · Local time · UTC+08:00");
            RequireCopyLine(window.CopyText, "Updated: 2026-10-23 12:00:00 +08:00");
            RequireCopyLine(window.CopyText, "Total tokens: 140");
            RequireCopyLine(window.CopyText, "The last refresh failed; any retained snapshot keeps its original date and time.");
        });

        Test("exit cancels and joins the read even when the exit redraw fails", window =>
        {
            var read = new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readToken = CancellationToken.None;
            var exitRedraws = 0;
            var closed = false;
            var shutdown = false;
            window.Closed += (_, _) => closed = true;
            using var controller = CreateRefreshTestController(window,
                token => { readToken = token; return read.Task; },
                state =>
                {
                    if (state.IsExiting)
                    {
                        exitRedraws++;
                        throw new InvalidOperationException("Synthetic exit redraw failure");
                    }
                    window.SetState(state, Now, Zone);
                },
                () => shutdown = true);
            try
            {
                controller.Start();
                PumpUntil(() => readToken.CanBeCanceled, "The initial refresh did not enter the reader");
                var scheduler = ControllerField<RefreshCoordinator>(controller, "_scheduler");
                var timer = ControllerField<DispatcherTimer>(controller, "_timer");
                var tray = GetControllerTray(controller);
                Require(timer.IsEnabled && tray.Visible, "The timer and tray must be active before exit");
                RefreshMenuItem(controller, "Refresh statistics").PerformClick();
                Require(scheduler.HasPendingRefresh, "The in-flight refresh must have pending work to discard");

                window.Dispatcher.Invoke(RefreshMenuItem(controller, "Exit panel").PerformClick, DispatcherPriority.Normal);
                Require(exitRedraws == 1, "The controlled exit redraw was not exercised");
                Require(!timer.IsEnabled && !tray.Visible && readToken.IsCancellationRequested, "Exit must stop callbacks and cancel the in-flight read");
                Require(scheduler.IsExiting && scheduler.IsBusy && !scheduler.HasPendingRefresh, "Exit must discard the pending request and retain the in-flight obligation");
                Require(!closed && !shutdown, "Exit must wait for the actual read to settle");

                read.SetCanceled(readToken);
                PumpUntil(() => shutdown, "Cleanup did not request shutdown after the read ended");
                Require(closed && !scheduler.IsBusy, "The window and scheduler must finish closing after the read");
            }
            finally { read.TrySetCanceled(); }
        });
    }

    private static DailyController CreateRefreshTestController(DailyWindow window,
        Func<CancellationToken, Task<DailySnapshot>> read, Action<DailyPresentationState> render, Action? shutdown = null)
    {
        window.Configure(new PanelSettings(1, ThemePreference.Light, LanguagePreference.EnUS));
        var app = Application.Current ?? throw new InvalidOperationException("UI test application is not running");
        var home = Path.Combine(Path.GetTempPath(), "daily-ui-refresh-" + Guid.NewGuid().ToString("N"));
        return new DailyController(app, window, home, demo: false, settingsStore: null, settingsResult: null,
            readSnapshot: read, renderState: (state, _, _) => render(state), shutdown: shutdown);
    }

    private static T ControllerField<T>(DailyController controller, string name) where T : class =>
        typeof(DailyController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(controller) as T
            ?? throw new InvalidOperationException("Missing controller field " + name);

    private static Forms.ToolStripMenuItem RefreshMenuItem(DailyController controller, string text) =>
        GetControllerTray(controller).ContextMenuStrip!.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == text);

    private static void WaitForRefresh(DailyController controller)
    {
        var refresh = ControllerField<Task>(controller, "_refresh");
        PumpUntil(() => refresh.IsCompleted, "The refresh task did not settle");
        refresh.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> condition, string failure)
    {
        if (condition()) return;
        var elapsed = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || elapsed.Elapsed >= TimeSpan.FromSeconds(3)) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Require(condition(), failure);
    }
}

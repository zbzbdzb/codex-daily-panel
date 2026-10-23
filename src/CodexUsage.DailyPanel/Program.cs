using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexUsage.Daily;

namespace CodexUsage.DailyPanel;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!DailyOptions.TryParse(args, out var parsed, out var error))
        {
            WriteConsole(error ?? "Invalid arguments.", isError: true);
            return 2;
        }
        var options = parsed!;
        if (options.ShowHelp) { WriteConsole(DailyOptions.HelpText); return 0; }
        if (options.ShowVersion)
        {
            WriteConsole("CodexDaily " + (typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"));
            return 0;
        }
        var demo = options.Demo;
        var render = options.RenderPath;
        var snapshot = options.SnapshotPath;
        var home = options.Home;
        var strings = new DailyStrings(LanguagePreference.System);
        try
        {
            if (snapshot is not null)
            {
                if (demo)
                {
                    File.WriteAllText(snapshot, JsonSerializer.Serialize(DemoSnapshot(), new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }
                using var monitor = new SessionLogMonitor(home, TimeZoneInfo.Local);
                var result = monitor.RefreshAsync(DateTimeOffset.UtcNow).GetAwaiter().GetResult();
                for (var round = 0; monitor.IsLoading && round < 64; round++)
                    result = monitor.RefreshAsync(DateTimeOffset.UtcNow).GetAwaiter().GetResult();
                File.WriteAllText(snapshot, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return monitor.IsLoading ? 3 : 0;
            }
            using var instance = demo ? null : SingleInstanceCoordinator.Acquire(home);
            if (instance is { IsPrimary: false })
            {
                if (instance.TryActivateAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult()) return 0;
                WriteConsole("The existing CodexDaily instance could not be activated. Try again after it has finished exiting.", isError: true);
                return 1;
            }
            var settingsStore = new PanelSettingsStore(options.SettingsDirectory ?? PanelSettingsStore.DefaultDirectory);
            var settingsResult = settingsStore.Load();
            strings = new DailyStrings(settingsResult.Settings.Language);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new DailyWindow();
            window.Configure(settingsResult.Settings);
            application.MainWindow = window;
            if (render is not null)
            {
                window.Update(DemoSnapshot(), true);
                window.ShowInTaskbar = false;
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * 1.5), (int)Math.Ceiling(window.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                image.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(render)) encoder.Save(stream);
                window.Close();
                application.Shutdown();
                return 0;
            }
            using var controller = new DailyController(application, window, home, demo, settingsStore, settingsResult);
            application.DispatcherUnhandledException += (_, e) =>
            {
                e.Handled = true;
                controller.HandleOperationFailure(e.Exception);
                var currentStrings = new DailyStrings(window.Settings.Language);
                MessageBox.Show(currentStrings["OperationFailed"], currentStrings["Title"], MessageBoxButton.OK, MessageBoxImage.Information);
            };
            controller.Start();
            instance?.StartListening(controller.RequestShow);
            return application.Run();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (snapshot is not null)
            {
                // A missing/locked/unwritable output must not throw a second exception
                // while trying to report the original operation failure.
                try { File.WriteAllText(snapshot, JsonSerializer.Serialize(new { error = exception.GetType().Name })); }
                catch (Exception writeError) when (writeError is not OutOfMemoryException) { }
                WriteConsole("Snapshot operation failed: " + exception.GetType().Name, isError: true);
            }
            else if (render is not null) WriteConsole("Render operation failed: " + exception.GetType().Name, isError: true);
            else MessageBox.Show(strings["StartupFailed"], strings["Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }

    private static void WriteConsole(string message, bool isError = false)
    {
        try
        {
            // WinExe can still serve CLI callers. Keep redirected handles intact;
            // attach to a parent console only when no console is already attached.
            if (OperatingSystem.IsWindows()) AttachConsole(unchecked((uint)-1));
            (isError ? Console.Error : Console.Out).WriteLine(message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    internal static DailySnapshot DemoSnapshot() => new(
        DateOnly.FromDateTime(DateTime.Now), DateTimeOffset.Now,
        120989545, 117878016, 639259, 353523,
        18252, 18041, 35.02, 4, 38, 0, 4, false, "演示数据 · 不读取真实日志",
        new WeeklyAllowance(27, DateTimeOffset.Now.AddDays(7), DateTimeOffset.Now.AddMinutes(-2)))
        { TimedTurnCount = 36, ExcludedTimingTurnCount = 2, ExcludedWaitSeconds = 4800 };
}

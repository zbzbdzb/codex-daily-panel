using System;
using System.Globalization;
using System.IO;
using System.Linq;
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
        var demo = false;
        string? render = null;
        string? snapshot = null;
        string? home = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--demo") demo = true;
            else if (args[i] == "--render" && i + 1 < args.Length) { render = Path.GetFullPath(args[++i]); demo = true; }
            else if (args[i] == "--snapshot" && i + 1 < args.Length) snapshot = Path.GetFullPath(args[++i]);
            else if (args[i] == "--codex-home" && i + 1 < args.Length) home = Path.GetFullPath(args[++i]);
            else return 2;
        }
        if (render is not null && snapshot is not null) return 2;
        home ??= Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
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
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new DailyWindow();
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
            using var controller = new DailyController(application, window, home, demo);
            application.DispatcherUnhandledException += (_, e) =>
            {
                e.Handled = true;
                window.SetBusy(false);
                MessageBox.Show("这次操作未能完成。请稍后刷新或重新启动面板。", "Codex Usage", MessageBoxButton.OK, MessageBoxImage.Information);
            };
            controller.Start();
            return application.Run();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (snapshot is not null) File.WriteAllText(snapshot, JsonSerializer.Serialize(new { error = exception.GetType().Name }));
            else MessageBox.Show("无法启动面板。请确认已安装 .NET 9 Windows Desktop Runtime，并检查日志目录是否可读。", "Codex Usage", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }

    internal static DailySnapshot DemoSnapshot() => new(
        DateOnly.FromDateTime(DateTime.Now), DateTimeOffset.Now,
        120989545, 117878016, 639259, 353523,
        18252, 18041, 35.02, 4, 38, 0, 4, false, "演示数据 · 不读取真实日志",
        new WeeklyAllowance(27, DateTimeOffset.Now.AddDays(7), DateTimeOffset.Now.AddMinutes(-2)))
        { TimedTurnCount = 36, ExcludedTimingTurnCount = 2, ExcludedWaitSeconds = 4800 };
}

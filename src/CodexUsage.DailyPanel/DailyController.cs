using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexUsage.Daily;
using Forms = System.Windows.Forms;

namespace CodexUsage.DailyPanel;

internal sealed class DailyController : IDisposable
{
    private readonly Application _app;
    private readonly DailyWindow _window;
    private readonly SessionLogMonitor? _monitor;
    private readonly string _home;
    private readonly bool _demo;
    private readonly Forms.NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _refresh = Task.CompletedTask;
    private bool _busy;
    private bool _exiting;
    private bool _paused;
    private bool _openingPopup;
    private readonly TrayPopupInteraction _popupInteraction = new();
    private TrayPopupAnchor? _lastPopupAnchor;
    private TrayPopupAnchor? _lastTrayPressAnchor;

    public DailyController(Application app, DailyWindow window, string home, bool demo)
    {
        _app = app;
        _window = window;
        _home = Path.GetFullPath(home);
        _demo = demo;
        _monitor = demo ? null : new SessionLogMonitor(_home, TimeZoneInfo.Local);
        _icon = CreateIcon();
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("打开今日统计", null, (_, _) => _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowPopup)));
        _menu.Items.Add("复制统计", null, (_, _) => Copy());
        _menu.Items.Add("立即刷新", null, (_, _) => BeginRefresh());
        var pause = new Forms.ToolStripMenuItem("暂停自动刷新") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => { _paused = pause.Checked; if (!_paused) BeginRefresh(); };
        _menu.Items.Add(pause);
        _menu.Items.Add("打开日志目录", null, (_, _) => OpenFolder());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出", null, async (_, _) => await ExitAsync());
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "Codex Usage · 正在读取今日统计", ContextMenuStrip = _menu, Visible = true };
        _tray.MouseDown += (_, e) =>
        {
            if (e.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right)
                _lastTrayPressAnchor = TrayPopupPlacement.CaptureAnchor();
        };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) TogglePopup(); };
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => { if (!_paused) BeginRefresh(); };
        _window.RefreshRequested += BeginRefresh;
        _window.FolderRequested += OpenFolder;
        _window.ExitRequested += async () => await ExitAsync();
        _window.Closing += HideOnClose;
        _window.Deactivated += (_, _) => HideWhenDeactivated();
    }

    public void Start() { _window.Hide(); BeginRefresh(); _timer.Start(); }

    private void BeginRefresh()
    {
        if (_exiting || _busy) return;
        _refresh = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _busy = true;
        _window.SetBusy(true);
        try
        {
            var snapshot = _demo ? Program.DemoSnapshot()
                : await Task.Run(() => _monitor!.RefreshAsync(DateTimeOffset.UtcNow, _lifetime.Token), _lifetime.Token);
            if (_exiting) return;
            _window.Update(snapshot, _demo);
            _tray.Text = TrayText(snapshot);
            _timer.Interval = TimeSpan.FromSeconds(_monitor?.IsLoading == true ? 1 : 10);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!_exiting)
            {
                _window.Update(new(DateOnly.FromDateTime(DateTime.Now), DateTimeOffset.Now, null, null, null, null,
                    null, null, null, 0, 0, 0, 0, true, "日志读取暂时不可用，请稍后刷新", null));
                _tray.Text = "Codex Usage · 日志读取暂时不可用";
            }
        }
        finally { _busy = false; if (!_exiting) _window.SetBusy(false); }
    }

    private static string TrayText(DailySnapshot snapshot)
    {
        var total = DailyFormatting.Tokens(DailyFormatting.TotalTokens(snapshot.InputTokens, snapshot.OutputTokens));
        var speed = snapshot.AverageTps is { } tps ? $"≈{tps:0.0} t/s" : "TPS 暂无";
        return $"Codex Usage · 今日总 token {total}{(snapshot.IsPartial ? "（部分）" : "")} · {speed}";
    }

    private static long TimestampMs => (long)(Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));

    private void TogglePopup()
    {
        if (_exiting) return;
        if (_popupInteraction.ShouldOpen(_window.IsVisible, TimestampMs)) ShowPopup();
        else _window.Hide();
    }

    private void ShowPopup()
    {
        if (_exiting) return;
        _openingPopup = true;
        try
        {
            // A context-menu action runs after the cursor has left the icon. Keep
            // the actual tray press location for placement and toggle suppression.
            var anchor = _lastTrayPressAnchor ?? TrayPopupPlacement.CaptureAnchor();
            _lastPopupAnchor = anchor;
            var scale = anchor.Dpi / 96d;
            var maximumWidth = Math.Max(1, anchor.WorkingArea.Width / scale - 16);
            var maximumHeight = Math.Max(1, anchor.WorkingArea.Height / scale - 16);
            _window.MinWidth = Math.Min(360, maximumWidth);
            _window.MinHeight = Math.Min(360, maximumHeight);
            _window.MaxHeight = maximumHeight;
            _window.Width = Math.Min(460, maximumWidth);
            _window.Height = Math.Min(640, maximumHeight);
            _window.WindowState = WindowState.Normal;
            _window.Opacity = 0;
            _window.Show();
            _window.UpdateLayout();
            var bounds = TrayPopupGeometry.Place(anchor.Cursor, anchor.WorkingArea,
                TrayPopupGeometry.ScaleFromDip(_window.Width, _window.Height, anchor.Dpi));
            TrayPopupPlacement.MoveWindow(new WindowInteropHelper(_window).Handle, bounds);
            _window.UpdateLayout();
            _window.Opacity = 1;
            _window.Activate();
        }
        finally
        {
            _window.Opacity = 1;
            // The tray/context-menu mouse message must finish returning focus before
            // normal outside-click dismissal resumes.
            _window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => _openingPopup = false));
        }
    }

    private void HideWhenDeactivated()
    {
        if (_exiting || _openingPopup || !_window.IsVisible) return;
        var cursor = Forms.Cursor.Position;
        var nearTray = _lastPopupAnchor is { } anchor
            && Math.Abs(cursor.X - anchor.Cursor.X) <= 24 * anchor.Dpi / 96d
            && Math.Abs(cursor.Y - anchor.Cursor.Y) <= 24 * anchor.Dpi / 96d;
        _popupInteraction.Dismissed(nearTray && (Forms.Control.MouseButtons & Forms.MouseButtons.Left) != 0, TimestampMs);
        _window.Hide();
    }

    private void Copy()
    {
        try { Clipboard.SetText(_window.CopyText); }
        catch (ExternalException) { _tray.ShowBalloonTip(1500, "Codex Usage", "剪贴板正忙，请稍后重试。", Forms.ToolTipIcon.Info); }
    }

    private void OpenFolder()
    {
        if (_demo) { _tray.ShowBalloonTip(1500, "演示模式", "演示模式不访问真实日志。", Forms.ToolTipIcon.Info); return; }
        var target = Path.Combine(_home, "sessions");
        if (!Directory.Exists(target)) target = _home;
        if (!Directory.Exists(target)) { _tray.ShowBalloonTip(1500, "Codex Usage", "尚未找到 Codex 日志目录。", Forms.ToolTipIcon.Info); return; }
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Win32Exception) { _tray.ShowBalloonTip(1500, "Codex Usage", "无法打开日志目录。", Forms.ToolTipIcon.Info); }
    }

    private void HideOnClose(object? sender, CancelEventArgs e) { if (!_exiting) { e.Cancel = true; _window.Hide(); } }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        _tray.Visible = false;
        _lifetime.Cancel();
        try { await _refresh; }
        catch (Exception) { /* An earlier failed refresh must not prevent exit. */ }
        finally { _window.Close(); _app.Shutdown(); }
    }

    public void Dispose()
    {
        _exiting = true;
        _timer.Stop();
        _lifetime.Cancel();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _icon.Dispose();
        _monitor?.Dispose();
        _lifetime.Dispose();
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(0, 132, 255));
            graphics.FillRectangle(brush, 4, 16, 5, 11);
            graphics.FillRectangle(brush, 12, 6, 5, 21);
            graphics.FillRectangle(brush, 20, 11, 5, 16);
            graphics.FillRectangle(brush, 3, 29, 25, 2);
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}

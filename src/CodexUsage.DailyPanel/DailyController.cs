using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexUsage.Daily;
using Microsoft.Win32;
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
    private readonly RefreshCoordinator _scheduler = new();
    private readonly PanelSettingsStore? _settingsStore;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private DailyPresentationState _state = new();
    private DailyStrings _strings;
    private PanelSettings _settings;
    private bool _disposed;
    private bool _exiting;
    private bool _paused;
    private bool _openingPopup;
    private readonly TrayPopupInteraction _popupInteraction = new();
    private TrayPopupAnchor? _lastPopupAnchor;
    private TrayPopupAnchor? _lastTrayPressAnchor;

    public DailyController(Application app, DailyWindow window, string home, bool demo)
        : this(app, window, home, demo, null, null) { }

    public DailyController(Application app, DailyWindow window, string home, bool demo,
        PanelSettingsStore? settingsStore, PanelSettingsResult? settingsResult)
    {
        _app = app;
        _window = window;
        _home = Path.GetFullPath(home);
        _demo = demo;
        _settingsStore = settingsStore;
        _settings = settingsResult?.Settings ?? window.Settings;
        _strings = new(_settings.Language);
        if (settingsResult?.Issue is { } issue) _state = _state with { Issues = [new DailyIssue(issue.Code)] };
        _window.Configure(_settings);
        _window.SetDemo(demo);
        _monitor = demo ? null : new SessionLogMonitor(_home, TimeZoneInfo.Local);
        _icon = CreateIcon();
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(_strings["Open"], null, (_, _) => _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowPopup)));
        _menu.Items.Add(_strings["Copy"], null, (_, _) => Copy());
        _menu.Items.Add(_strings["Refresh"], null, (_, _) => BeginRefresh());
        _pauseItem = new Forms.ToolStripMenuItem(_strings["Pause"]) { CheckOnClick = true };
        _pauseItem.CheckedChanged += (_, _) =>
        {
            _paused = _pauseItem.Checked;
            var start = _scheduler.SetPaused(_paused);
            RefreshView();
            if (start) _refresh = RefreshAsync();
        };
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(_strings["Folder"], null, (_, _) => OpenFolder());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(_strings["Exit"], null, async (_, _) => await ExitAsync());
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = _strings["Title"] + " · " + _strings["Waiting"], ContextMenuStrip = _menu, Visible = true };
        _tray.MouseDown += (_, e) =>
        {
            if (e.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right)
                _lastTrayPressAnchor = TrayPopupPlacement.CaptureAnchor();
        };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) TogglePopup(); };
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => { RefreshView(); RequestRefresh(RefreshReason.Automatic); };
        _window.RefreshRequested += BeginRefresh;
        _window.FolderRequested += OpenFolder;
        _window.ExitRequested += async () => await ExitAsync();
        _window.Closing += HideOnClose;
        _window.Deactivated += (_, _) => HideWhenDeactivated();
        _window.SettingsChanged += ChangeSettings;
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        StyleMenu();
    }

    public void Start() { _window.Hide(); RequestRefresh(RefreshReason.Initial); _timer.Start(); }

    private void BeginRefresh()
    {
        RequestRefresh(RefreshReason.Manual);
    }

    private void RequestRefresh(RefreshReason reason)
    {
        if (_scheduler.Request(reason)) _refresh = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        do
        {
            _state = _state with { LastAttemptAt = DateTimeOffset.Now };
            RefreshView();
            try
            {
                var snapshot = _demo ? Program.DemoSnapshot()
                    : await Task.Run(() => _monitor!.RefreshAsync(DateTimeOffset.UtcNow, _lifetime.Token), _lifetime.Token);
                if (!_exiting) _state = _state with { Snapshot = snapshot, RefreshFailed = false };
                _timer.Interval = TimeSpan.FromSeconds(_monitor?.IsLoading == true ? 1 : 10);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (!_exiting) _state = _state with { RefreshFailed = true };
            }
        }
        while (_scheduler.Complete());
        if (!_exiting) RefreshView();
    }

    private void RefreshView()
    {
        if (_disposed) return;
        _state = _state with { IsPaused = _scheduler.IsPaused, IsBusy = _scheduler.IsBusy, IsExiting = _exiting };
        var now = DateTimeOffset.Now;
        _window.SetState(_state, now, TimeZoneInfo.Local);
        var view = _state.Project(now, TimeZoneInfo.Local);
        var total = DailyFormatting.Tokens(DailyFormatting.TotalTokens(view.Snapshot?.InputTokens, view.Snapshot?.OutputTokens));
        var label = view.IsStale && view.Snapshot is { } old
            ? _strings.Format("TraySnapshot", old.Day.ToString("yyyy-MM-dd"), total) : _strings.Format("TrayTotal", total);
        var speed = view.Snapshot?.AverageTps is { } tps && double.IsFinite(tps) ? FormattableString.Invariant($"≈{tps:0.0} t/s") : "—";
        var activity = DailyPresentationFormatting.Activity(view, _strings, _demo);
        var quality = DailyPresentationFormatting.Quality(view, _strings);
        var text = $"{_strings["Title"]} · {label} · {speed} · {activity} {quality}";
        _tray.Text = text.Length <= 127 ? text : text[..126] + "…";
    }

    public void RequestShow()
    {
        if (_exiting || _window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(new Action(ShowPopup));
    }

    public void HandleOperationFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (_exiting) return;
        _state = _state with { RefreshFailed = true, LastAttemptAt = DateTimeOffset.Now };
        RefreshView();
    }

    private void ChangeSettings(PanelSettings settings)
    {
        var result = _settingsStore?.Save(settings) ?? new PanelSettingsResult(settings);
        _settings = result.Settings; _strings = new(_settings.Language);
        _state = _state with { Issues = result.Issue is { } issue ? [new DailyIssue(issue.Code)] : [] };
        _window.Configure(_settings);
        var keys = new[] { "Open", "Copy", "Refresh", "Pause", "Folder" };
        for (var i = 0; i < keys.Length; i++) _menu.Items[i].Text = _strings[keys[i]];
        _menu.Items[^1].Text = _strings["Exit"];
        StyleMenu(); RefreshView();
    }

    private void StyleMenu()
    {
        var dark = DailyTheme.IsDark(_settings.Theme);
        var background = dark ? Color.FromArgb(40,40,40) : Color.White;
        var foreground = dark ? Color.FromArgb(223,223,223) : Color.FromArgb(26,28,31);
        _menu.BackColor=background; _menu.ForeColor=foreground;
        _menu.Renderer = new Forms.ToolStripProfessionalRenderer(new MenuColors(dark));
        foreach (Forms.ToolStripItem item in _menu.Items) { item.BackColor=background; item.ForeColor=foreground; }
    }

    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (!_disposed && !_window.Dispatcher.HasShutdownStarted)
            _window.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) StyleMenu(); }));
    }

    private sealed class MenuColors(bool dark) : Forms.ProfessionalColorTable
    {
        private Color Surface => dark ? Color.FromArgb(40,40,40) : Color.White;
        private Color Border => dark ? Color.FromArgb(65,65,65) : Color.FromArgb(223,223,223);
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuItemSelected => dark ? Color.FromArgb(65,65,65) : Color.FromArgb(237,237,237);
        public override Color MenuItemBorder => Border;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Border;
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
        RefreshView();
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
        RefreshView();
        _window.CopyStats();
    }

    private void OpenFolder()
    {
        if (_demo) { _tray.ShowBalloonTip(1500, _strings["Title"], _strings["DemoFolder"], Forms.ToolTipIcon.Info); return; }
        var target = Path.Combine(_home, "sessions");
        if (!Directory.Exists(target)) target = _home;
        if (!Directory.Exists(target)) { _tray.ShowBalloonTip(1500, _strings["Title"], _strings["FolderMissing"], Forms.ToolTipIcon.Info); return; }
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        { _tray.ShowBalloonTip(1500, _strings["Title"], _strings["FolderFailed"], Forms.ToolTipIcon.Info); }
    }

    private void HideOnClose(object? sender, CancelEventArgs e) { if (!_exiting) { e.Cancel = true; _window.Hide(); } }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _scheduler.Exit();
        RefreshView();
        _timer.Stop();
        _tray.Visible = false;
        _lifetime.Cancel();
        try { await _refresh; }
        catch (Exception) { /* An earlier failed refresh must not prevent exit. */ }
        finally { _window.Close(); _app.Shutdown(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _exiting = true;
        _scheduler.Exit();
        SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        _window.SettingsChanged -= ChangeSettings;
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

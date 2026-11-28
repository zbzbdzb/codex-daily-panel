namespace CodexUsage.DailyPanel;

public enum RefreshReason { Initial, Automatic, Manual, Resume }

/// <summary>
/// UI-thread-owned scheduling state. A true return transfers one refresh to the
/// caller; the caller must settle it with Complete, including after an exception.
/// This class neither runs tasks nor mutates the last successful data snapshot.
/// </summary>
public sealed class RefreshCoordinator
{
    private bool _pendingManual;
    private bool _pendingResume;
    public bool IsPaused { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsExiting { get; private set; }
    public bool HasPendingRefresh => _pendingManual || _pendingResume;

    public bool Request(RefreshReason reason)
    {
        if (IsExiting || IsPaused && reason == RefreshReason.Automatic) return false;
        if (IsBusy)
        {
            if (reason == RefreshReason.Manual) _pendingManual = true;
            if (reason == RefreshReason.Resume) _pendingResume = true;
            return false;
        }
        IsBusy = true;
        return true;
    }

    public bool SetPaused(bool paused)
    {
        if (IsExiting || paused == IsPaused) return false;
        IsPaused = paused;
        if (paused) _pendingResume = false;
        return !paused && Request(RefreshReason.Resume);
    }

    public bool Complete()
    {
        IsBusy = false;
        if (IsExiting || !HasPendingRefresh) return false;
        _pendingManual = _pendingResume = false;
        IsBusy = true;
        return true;
    }

    public void Exit()
    {
        IsExiting = true;
        _pendingManual = _pendingResume = false;
    }
}

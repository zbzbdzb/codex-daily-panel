namespace CodexUsage.DailyPanel;

internal sealed class TrayPopupInteraction
{
    private long? _trayDismissalMs;

    /// <summary>Consumes at most the pending tray click whose mouse press dismissed the popup.</summary>
    internal bool ShouldOpen(bool currentlyVisible, long timestampMs)
    {
        var dismissedAt = _trayDismissalMs;
        _trayDismissalMs = null;
        if (currentlyVisible) return false;
        return dismissedAt is not { } prior || timestampMs < prior || (ulong)timestampMs - (ulong)prior > 500;
    }

    internal void Dismissed(bool wasTrayMousePress, long timestampMs) =>
        _trayDismissalMs = wasTrayMousePress ? timestampMs : null;
}

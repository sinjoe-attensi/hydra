using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

// X11's primary selection, kept for a clipboard that has none: the newest of the selections relayed here and the
// user's text copies. The version is the clipboard's change counter, which only grows and says whether a copy was made
// since Hydra last touched the clipboard; every one is read under the gate, so no write can land between it and what it
// decides. Hydra's own writes go through Clipboard, so this sees each one: until it ends, what is on the clipboard is no
// copy of the user's, and the version it leaves is never taken for one
public sealed class EmulatedPrimarySelection : IPrimarySelection
{
    private readonly ISelectionlessClipboard _clipboard;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private string? _current;
    private long _seen = -1;
    private int _writing;

    private static readonly SelectionRead ReadFailed = new SelectionRead.Unreadable("the primary selection could not be read");
    private static readonly SelectionWrite KeepFailed = new SelectionWrite.NotKept("the primary selection could not be kept");

    public EmulatedPrimarySelection(ISelectionlessClipboard clipboard, ILogger log)
    {
        _clipboard = clipboard;
        _log = log;
        Clipboard = new WritingThrough(this, clipboard);
    }

    // the clipboard, every write through which this sees
    public IClipboard Clipboard { get; }

    // unreadable only when the clipboard failed outside ReadTextCopy, as its version can; the failure is logged
    public SelectionRead Read() => Guarded("read the primary selection", ReadFailed, () =>
    {
        Catch(_clipboard.Counter());
        return _current is { } current ? new SelectionRead.Text(current) : new SelectionRead.None();
    });

    // a copy made before the selection arrived is older than it
    public SelectionWrite Write(string text) => Guarded("keep the primary selection", KeepFailed, () =>
    {
        RelayAt(text, _clipboard.Counter());
        return new SelectionWrite.Kept();
    });

    // Hydra's own write of the clipboard, with the selection beside it unless null: until it ends, what is on the
    // clipboard is no copy of the user's. The clipboard reports the version it leaves as soon as it knows it, even a
    // failed or partial write's; a failure is logged, not thrown. The gate is never held across the write, so a platform
    // may take its own clipboard lock inside it
    internal ClipboardWrite WriteClipboard(ClipboardContents contents, string? selection)
    {
        if (!Begin(selection)) return new ClipboardWrite.NotReplaced("the clipboard's version could not be read");
        long? left = null;
        try
        {
            return _clipboard.Write(contents, v => left = v);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write clipboard");
            return new ClipboardWrite.NotReplaced(ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (left is { } v) _seen = Math.Max(_seen, v); // a selection relayed during the write may have moved it on
                _writing--;
            }
        }
    }

    // a copy made since Hydra last touched the clipboard is looked for before the write begins, and is older than the
    // selection beside it
    private bool Begin(string? selection) => Guarded("begin writing clipboard", false, () =>
    {
        RelayAt(selection, _clipboard.Counter());
        _writing++;
        return true;
    });

    // the version is seen once its copy was read, or a selection arrived that outranks it
    private void RelayAt(string? incoming, long at)
    {
        var read = Catch(at);
        if (incoming != null) _current = incoming;
        if (read || incoming != null) _seen = at;
    }

    // a text copy made since Hydra last touched the clipboard becomes the primary, one answer per version; false when
    // the clipboard could not be read
    private bool Catch(long at)
    {
        if (_writing > 0 || at == _seen) return true;
        SelectionRead copy;
        try
        {
            copy = _clipboard.ReadTextCopy();
        }
        catch (Exception ex)
        {
            copy = new SelectionRead.Unreadable(ex.Message, ex);
        }
        if (copy is SelectionRead.Unreadable unreadable)
        {
            if (_log.IsEnabled(LogLevel.Debug))
                _log.LogDebug(unreadable.Cause, "Clipboard unreadable for the primary selection ({Reason}); trying again on the next read",
                    unreadable.Reason);
            return false;
        }
        _seen = at;
        if (copy is SelectionRead.Text { Value: var text } && text != ClipboardUtils.PlaceholderText) _current = text;
        return true;
    }

    // gated, and logged rather than thrown when the clipboard fails
    private T Guarded<T>(string what, T failed, Func<T> body)
    {
        try
        {
            lock (_gate) return body();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to {What}", what);
            return failed;
        }
    }

    // the clipboard, its writes made through the selection
    private sealed class WritingThrough(EmulatedPrimarySelection selection, ISelectionlessClipboard clipboard) : IClipboard, IWritesBesideSelection
    {
        public ClipboardRead Read() => clipboard.Read();

        public ClipboardWrite Write(ClipboardContents contents) => selection.WriteClipboard(contents, null);

        public ClipboardWrite Write(ClipboardContents contents, string? beside) => selection.WriteClipboard(contents, beside);

        public bool HoldsFiles() => clipboard.HoldsFiles();

        public ClipboardVersion? Version => clipboard.Version;

        public bool StillHolds() => clipboard.StillHolds();
    }
}

// the version Hydra's last write left a clipboard at, as that write reported it (a failed write's too), and whether the
// clipboard is still there; for a platform whose write reports it (ISelectionlessClipboard)
internal sealed class LastWrite(Func<long> counter, ILogger log)
{
    private readonly Lock _lock = new();
    private long _at = -1;

    // null before any write reported one
    public long? At
    {
        get
        {
            lock (_lock) return _at >= 0 ? _at : null;
        }
    }

    // whether the clipboard is still what Hydra's last write left, failed or not; never before any write reported a version
    public bool Holds()
    {
        try
        {
            return At is { } at && counter() == at;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to {What}", "check clipboard ownership");
            return false;
        }
    }

    // Hydra's write of asked: it reports the version it leaves the clipboard at to leftAt as soon as it knows it, and
    // returns what it wrote, null when it did not replace the clipboard. The version is kept once the write ends, even a
    // failed or partial write's; At is that version, else the counter right after the write
    public ClipboardWrite Write(ClipboardContents asked, Func<Action<long>, ClipboardContents?> write, Action<long> leftAt, Func<long?> after)
    {
        long? left = null;
        ClipboardContents? written;
        try
        {
            written = write(v =>
            {
                left = v;
                leftAt(v);
            });
        }
        finally
        {
            if (left is { } v)
                lock (_lock)
                    _at = v;
        }
        return written is { } took ? ClipboardUtils.Written(asked, took, At ?? after()) : new ClipboardWrite.NotReplaced("the clipboard was not replaced");
    }
}

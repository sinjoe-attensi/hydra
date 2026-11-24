namespace Hydra.Platform;

// a headless master's clipboard: the last copy a slave sent, for the next one. Only Hydra writes it, so the counter
// moves only with those writes
public sealed class MemoryClipboardSync : IClipboardSync
{
    private readonly Lock _lock = new();
    private ClipboardSnapshot _contents = ClipboardSnapshot.Empty;
    private string? _primary;
    private long _changes;

    public ClipboardRead Read()
    {
        lock (_lock)
            return new ClipboardRead.Contents(new ClipboardContents(_contents.Text, _contents.Html, _contents.Rtf, _contents.ImagePng, _contents.Concealed));
    }

    public SelectionRead ReadPrimary()
    {
        lock (_lock)
            return _primary is { } primary ? new SelectionRead.Text(primary) : new SelectionRead.None();
    }

    public bool SetPrimaryText(string text)
    {
        lock (_lock)
            _primary = text;
        return true;
    }

    public long? ChangeCount()
    {
        lock (_lock)
            return _changes;
    }

    public ClipboardSnapshot SetClipboard(ClipboardSnapshot contents)
    {
        lock (_lock)
        {
            // the selection is kept apart, and stays through a write without one, as a clear
            if (contents.PrimaryText != null) _primary = contents.PrimaryText;
            _contents = contents with { PrimaryText = null };
            _changes++;
        }
        return contents;
    }
}

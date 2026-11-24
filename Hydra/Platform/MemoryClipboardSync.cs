namespace Hydra.Platform;

// a headless master's clipboard: the last copy a slave sent, for the next one, and a selection of its own. Only Hydra
// writes it, so the counter moves only with those writes, never the selection's
public sealed class MemoryClipboardSync : IClipboardPlatform, IClipboard
{
    private readonly Lock _lock = new();
    private ClipboardContents _contents = ClipboardContents.Empty;
    private string? _primary;
    private long _changes;

    public MemoryClipboardSync() => PrimarySelection = new DelegatedSelection(ReadPrimary, WritePrimary);

    public IClipboard Clipboard => this;
    public IPrimarySelection PrimarySelection { get; }
    IPrimarySelection? IClipboardPlatform.PrimarySelection => PrimarySelection;

    public ClipboardRead Read()
    {
        lock (_lock)
            return new ClipboardRead.Contents(_contents);
    }

    private SelectionRead ReadPrimary()
    {
        lock (_lock)
            return _primary is { } primary ? new SelectionRead.Text(primary) : new SelectionRead.None();
    }

    private SelectionWrite WritePrimary(string text)
    {
        lock (_lock)
            _primary = text;
        return new SelectionWrite.Kept();
    }

    public long? ChangeCount()
    {
        lock (_lock)
            return _changes;
    }

    // a clear keeps the placeholder, which a read finds as it stands
    public ClipboardWrite Write(ClipboardContents contents)
    {
        lock (_lock)
        {
            _contents = ClipboardUtils.Writing(contents);
            return ClipboardUtils.Written(_contents, ++_changes);
        }
    }
}

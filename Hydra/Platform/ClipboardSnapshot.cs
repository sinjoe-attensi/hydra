namespace Hydra.Platform;

// a host's clipboard with the selection beside it, as one read found them or a peer sent them: the unit the handshake
// hashes, sends and compares, since one message carries both. Everywhere else the two are apart
public sealed record ClipboardSnapshot(ClipboardContents Clipboard, string? Selection)
{
    public static readonly ClipboardSnapshot Empty = new(ClipboardContents.Empty, null);

    // nothing for the clipboard, and no selection
    public bool IsEmpty => Clipboard.IsEmpty && string.IsNullOrEmpty(Selection);
}

// what applying a peer's copy did here; null for nothing
public abstract record Applied
{
    private Applied() { }

    // the clipboard's write, where one replaced it
    public abstract ClipboardWrite.Written? Written { get; }

    // the clipboard replaced by the copy as the write took it, never Empty; Selection: the one asked beside it
    public sealed record Copy(ClipboardWrite.Written Write, string? Selection) : Applied
    {
        public override ClipboardWrite.Written Written => Write;
    }

    // the selection alone, the clipboard left as it was
    public sealed record SelectionAlone(string Text) : Applied
    {
        public override ClipboardWrite.Written? Written => null;
    }

    // Hydra's empty clipboard in place of the copy: the placeholder written in place of a concealed copy whose markers
    // were refused, or a clear asked for. Known: the clipboard and selection as the handshake knows them since
    public sealed record Emptied(ClipboardWrite.Written Write, ClipboardSnapshot Known) : Applied
    {
        public override ClipboardWrite.Written Written => Write;
    }

    // a write that left no format of the copy: not replaced, or every format refused; Kept: the selection set after it
    public sealed record Failed(ClipboardWrite Write, SelectionAlone? Kept) : Applied
    {
        public override ClipboardWrite.Written? Written => Write as ClipboardWrite.Written;
    }
}

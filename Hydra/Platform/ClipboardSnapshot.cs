namespace Hydra.Platform;

// a host's clipboard with the selection beside it, as one read found them or a peer sent them: the unit the handshake
// hashes, sends and compares, since one message carries both. Everywhere else the two are apart. A null Selection is none
// on a read (a selection that could not be read too), and on an apply leaves this host's selection as it is: the
// selection to set, if any
public sealed record ClipboardSnapshot(ClipboardContents Clipboard, string? Selection)
{
    public static readonly ClipboardSnapshot Empty = new(ClipboardContents.Empty, null);

    // nothing for the clipboard, and no selection to change
    public bool IsEmpty => Clipboard.IsEmpty && string.IsNullOrEmpty(Selection);
}

// what applying a peer's copy did here; null for nothing
public abstract record Applied
{
    private Applied() { }

    // the clipboard's write, where one replaced it: Hydra's own write then, whatever of the copy it left
    public abstract ClipboardWrite.Written? Written { get; }

    // the copy, as the write took it (never Empty); Selection: the one asked beside it
    public sealed record Copy(ClipboardWrite.Written Written, string? Selection) : Applied
    {
        public override ClipboardWrite.Written Written { get; } = Written;
    }

    // the selection alone, the clipboard left as it was
    public sealed record SelectionAlone(string Text) : Applied
    {
        public override ClipboardWrite.Written? Written => null;
    }

    // the placeholder alone left in place of the copy: a secret copy whose markers were refused (the placeholder written
    // in its place), an empty clipboard. Kept: the selection set after it
    public sealed record MarkersRefused(ClipboardWrite.Written Written, SelectionAlone? Kept) : Applied
    {
        public override ClipboardWrite.Written Written { get; } = Written;
    }

    // the placeholder alone, asked for: a peer's copy that is the placeholder, or the master's clear of a secret copy;
    // Selection: the one asked beside it
    public sealed record Cleared(ClipboardWrite.Written Written, string? Selection) : Applied
    {
        public override ClipboardWrite.Written Written { get; } = Written;
    }

    // no format of the copy left: a write that did not replace the clipboard, or one that replaced it but had every format
    // refused, which is still Hydra's write (Written). Kept: the selection set after it
    public sealed record Failed(ClipboardWrite Write, SelectionAlone? Kept) : Applied
    {
        public override ClipboardWrite.Written? Written => Write as ClipboardWrite.Written;
    }
}

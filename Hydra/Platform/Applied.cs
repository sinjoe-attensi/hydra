namespace Hydra.Platform;

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
    // in its place), an empty clipboard. Kept: the selection set after it, where this host kept it
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

    // no format of the copy left: a write that did not replace the clipboard (Written null), or one that replaced it but had
    // every format refused (Written set: Hydra replaced the clipboard with nothing of the copy, which is still its write).
    // Kept: the selection set after it, where this host kept it
    public sealed record Failed(ClipboardWrite Write, SelectionAlone? Kept) : Applied
    {
        public override ClipboardWrite.Written? Written => Write as ClipboardWrite.Written;
    }
}

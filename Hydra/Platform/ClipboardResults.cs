namespace Hydra.Platform;

// the clipboard's own formats, as one read found them. Html is portable raw HTML (no Windows CF_HTML wrapper); Rtf is
// raw RTF bytes. Secret: a password manager's copy, kept out of history and cloud sync wherever it is written
public sealed record ClipboardContents(string? Text, string? Html, byte[]? Rtf, byte[]? Png, bool Secret)
{
    public static readonly ClipboardContents Empty = new(null, null, null, null, false);

    // no format at all
    public bool IsEmpty => string.IsNullOrEmpty(Text) && Html == null && Rtf == null && Png == null;
}

// the OS clipboard's change counter at one point; it only grows
public readonly record struct ClipboardVersion(long Value);

// one read of the clipboard, before any echo fallback: what it holds, copied files (read as nothing else), or a
// clipboard that could not be read, which says nothing of what it holds and is never empty. Contents may be a clear's
// placeholder as it stands (ClipboardUtils.IsPlaceholder), which ReadWithFallback takes as empty
public abstract record ClipboardRead
{
    private ClipboardRead() { }

    public sealed record Contents(ClipboardContents Value) : ClipboardRead;

    public sealed record Files : ClipboardRead;

    public sealed record Unreadable(string Reason, Exception? Cause = null) : ClipboardRead;
}

// one read of the primary selection: its text, none, or a selection that could not be read, which is never none
public abstract record SelectionRead
{
    private SelectionRead() { }

    public sealed record Text(string Value) : SelectionRead;

    public sealed record None : SelectionRead;

    public sealed record Unreadable(string Reason, Exception? Cause = null) : SelectionRead;
}

// one write of the clipboard: what it took, at the version the clipboard is at once the write landed (as the platform
// reports it, else read right after; null where the platform keeps none); a clipboard it did not replace; or one that
// could not be read where the write needed to. A write that did not replace the clipboard may still have moved the
// version, which the platform alone tracks, for StillHolds: NotReplaced carries none
public abstract record ClipboardWrite
{
    private ClipboardWrite() { }

    // Taken: the contents less any format the clipboard refused, so no format at all when it refused every one; Empty for
    // a clear. A secret copy whose markers were refused: the placeholder that went in its place (ClipboardUtils.IsPlaceholder),
    // less its text when that was refused too. That placeholder crosses the contract, unlike the design's, which keeps it
    // inside platforms: as Empty, "placeholder taken" could not be told from "placeholder refused", which Hydra handles
    // apart (Applied.MarkersRefused, Applied.Failed)
    public sealed record Written(ClipboardContents Taken, ClipboardVersion? At) : ClipboardWrite;

    public sealed record NotReplaced(string Reason) : ClipboardWrite;

    public sealed record Unreadable(string Reason, Exception? Cause = null) : ClipboardWrite;
}

// one write of the primary selection: kept, or not
public abstract record SelectionWrite
{
    private SelectionWrite() { }

    public sealed record Kept : SelectionWrite;

    public sealed record NotKept(string Reason) : SelectionWrite;
}

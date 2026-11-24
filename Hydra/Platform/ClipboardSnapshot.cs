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

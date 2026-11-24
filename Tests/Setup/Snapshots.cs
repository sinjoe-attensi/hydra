using Hydra.Platform;

namespace Tests.Setup;

// a clipboard with the selection beside it, built in the order and with the names the handshake tests were written in
public static class Snapshots
{
    public static ClipboardSnapshot Snapshot(string? text, string? selection, byte[]? image, string? Html = null, byte[]? Rtf = null, bool Concealed = false) =>
        new(new ClipboardContents(text, Html, Rtf, image, Concealed), selection);

    // the text of the copy written, null for anything else applied
    public static string? CopyText(this Applied? applied) => (applied as Applied.Copy)?.Write.Taken.Text;
}

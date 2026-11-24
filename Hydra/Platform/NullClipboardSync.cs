namespace Hydra.Platform;

// no clipboard and no selection: reads find nothing and writes replace nothing
public class NullClipboardSync : IClipboardPlatform, IClipboard
{
    public IClipboard Clipboard => this;
    public IPrimarySelection? PrimarySelection => null;

    public ClipboardRead Read() => new ClipboardRead.Contents(ClipboardContents.Empty);
    public ClipboardWrite Write(ClipboardContents contents) => new ClipboardWrite.NotReplaced("no clipboard is synced here");
}

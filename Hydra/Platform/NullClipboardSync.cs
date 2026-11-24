namespace Hydra.Platform;

public class NullClipboardSync : IClipboardSync
{
    public ClipboardRead Read() => new ClipboardRead.Contents(ClipboardContents.Empty);
    public ClipboardSnapshot? SetClipboard(ClipboardSnapshot contents) => null;
}

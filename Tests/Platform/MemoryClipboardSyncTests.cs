using Hydra.Platform;
using Hydra.Relay;
using Tests.Setup;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Platform;

[TestFixture]
public class MemoryClipboardSyncTests
{
    [Test]
    public void Clear_ReadsAsEmpty_KeepsTheSelection_AndMovesTheCounter()
    {
        IClipboardPlatform clipboard = new MemoryClipboardSync();
        clipboard.KeepSelection("selected");
        clipboard.Clipboard.Write(new ClipboardContents("copied", null, null, null, false));
        var afterCopy = clipboard.Clipboard.Count();
        var cleared = clipboard.Clipboard.Write(ClipboardContents.Empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.InstanceOf<ClipboardWrite.Written>());
            Assert.That(ClipboardUtils.ReadWithFallback(clipboard, null, ClipboardChanges.Of(clipboard.Clipboard).Last, NullLogger.Instance, "test")?.Clipboard.IsEmpty, Is.True);
            Assert.That(clipboard.ReadSelection().TextOrNone(), Is.EqualTo("selected"));
            Assert.That(clipboard.Clipboard.Count(), Is.EqualTo(afterCopy + 1));
        }
    }

    // the selection is apart from the clipboard: keeping it never moves the clipboard's version
    [Test]
    public void Selection_NeverMovesTheVersion()
    {
        IClipboardPlatform clipboard = new MemoryClipboardSync();
        var written = clipboard.Clipboard.Write(new ClipboardContents("copied", null, null, null, false));
        var before = clipboard.Clipboard.Version;

        clipboard.KeepSelection("selected");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo(new ClipboardWrite.Written(new ClipboardContents("copied", null, null, null, false), before)));
            Assert.That(clipboard.Clipboard.Version, Is.EqualTo(before));
        }
    }
}

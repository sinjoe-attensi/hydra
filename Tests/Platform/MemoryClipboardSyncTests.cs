using Hydra.Platform;
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
        var afterCopy = clipboard.Clipboard.ChangeCount();
        var cleared = clipboard.Clipboard.Write(ClipboardContents.Empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.InstanceOf<ClipboardWrite.Written>());
            Assert.That(ClipboardUtils.ReadWithFallback(clipboard, null, NullLogger.Instance, "test")?.HasClipboard, Is.False);
            Assert.That(clipboard.ReadSelection().TextOrNone(), Is.EqualTo("selected"));
            Assert.That(clipboard.Clipboard.ChangeCount(), Is.EqualTo(afterCopy + 1));
        }
    }
}

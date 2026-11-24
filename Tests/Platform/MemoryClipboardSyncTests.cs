using Hydra.Platform;
using Hydra.Relay;
using Tests.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Platform;

[TestFixture]
public class MemoryClipboardSyncTests
{
    [Test]
    public void Clear_ReadsAsEmpty_KeepsTheSelection_AndMovesTheCounter()
    {
        IClipboardPlatform clipboard = new MemoryClipboardSync();
        clipboard.SetSelection("selected");
        clipboard.Clipboard.Write(new ClipboardContents("copied", null, null, null, false));
        var afterCopy = clipboard.Clipboard.Count();
        var cleared = clipboard.Clipboard.Write(ClipboardContents.Empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.InstanceOf<ClipboardWrite.Written>());
            Assert.That(ClipboardUtils.ReadWithFallback(clipboard, null, NullLogger.Instance, "test")?.Clipboard.IsEmpty, Is.True);
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

        clipboard.SetSelection("selected");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo(new ClipboardWrite.Written(new ClipboardContents("copied", null, null, null, false), before)));
            Assert.That(clipboard.Clipboard.Version, Is.EqualTo(before));
        }
    }

    // a platform with a selection of its own composes into a view of it, never the platform itself, so a container that
    // hands out both disposes the platform once, as X11's must be
    [Test]
    public void Compose_IsAViewTheContainerDoesNotDisposeAgain()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Disposing>();
        services.AddSingleton<IClipboardPlatform>(sp => ClipboardPlatform.Compose(sp.GetRequiredService<Disposing>(), NullLogger.Instance));
        var provider = services.BuildServiceProvider();
        var platform = provider.GetRequiredService<Disposing>();
        var composed = provider.GetRequiredService<IClipboardPlatform>();

        provider.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(composed, Is.Not.SameAs(platform));
            Assert.That(composed.Clipboard, Is.SameAs(platform.Clipboard));
            Assert.That(composed.PrimarySelection, Is.SameAs(platform.PrimarySelection));
            Assert.That(platform.Disposed, Is.EqualTo(1));
        }
    }

    private sealed class Disposing : IClipboardPlatform, IDisposable
    {
        private readonly MemoryClipboardSync _memory = new();
        public IClipboard Clipboard => _memory.Clipboard;
        public IPrimarySelection? PrimarySelection => _memory.PrimarySelection;
        public int Disposed { get; private set; }
        public void Dispose() => Disposed++;
    }
}

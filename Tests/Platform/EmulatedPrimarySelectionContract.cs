using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;
using static Tests.Setup.Snapshots;

namespace Tests.Platform;

// The primary selection Hydra emulates for a platform without one, against its real clipboard: a second instance of the
// platform, composed as Hydra composes it, stands in for another app, since to the first it is one.
public abstract class EmulatedPrimarySelectionContract
{
    protected static readonly byte[] Png = TestImages.Png;

    // the platform as Hydra uses it, with the emulated selection, and the platform itself
    protected IClipboardPlatform Hydra { get; private set; } = null!;
    protected IClipboardPlatform Raw { get; private set; } = null!;
    private IClipboardPlatform _otherRaw = null!;
    private IClipboardPlatform _otherApp = null!;

    protected abstract IClipboardPlatform Create();

    [SetUp]
    public void CreateBoth()
    {
        Raw = Create();
        Hydra = ClipboardPlatform.Compose(Raw, NullLogger.Instance);
        _otherRaw = Create();
        _otherApp = ClipboardPlatform.Compose(_otherRaw, NullLogger.Instance);
    }

    [TearDown]
    public void DisposeBoth()
    {
        (Raw as IDisposable)?.Dispose();
        (_otherRaw as IDisposable)?.Dispose();
    }

    // Hydra's write as ClipboardUtils.ApplyPreservingFiles makes it: the selection beside it set first
    protected static ClipboardWrite Write(IClipboardPlatform platform, ClipboardSnapshot contents)
    {
        if (contents.Selection is { } selection) platform.KeepSelection(selection);
        return platform.Clipboard.Write(contents.Clipboard);
    }

    protected ClipboardWrite Write(ClipboardSnapshot contents) => Write(Hydra, contents);

    // what the write took; fails the test when it did not replace the clipboard
    protected static ClipboardContents Taken(ClipboardWrite write) =>
        write is ClipboardWrite.Written { Taken: var taken } ? taken : throw new AssertionException($"not written: {write}");

    protected bool Clear() => Hydra.Clipboard.Write(ClipboardContents.Empty) is ClipboardWrite.Written;

    // Hydra's write with a relayed selection beside it, then whatever the test has another app do
    protected void HydraWritesThen(Action otherApp)
    {
        Write(Snapshot("written", "relayed selection", null));
        otherApp();
    }

    protected void OtherAppCopies(string? text, byte[]? png = null) => Write(_otherApp, Snapshot(text, null, png));

    [Test]
    public void Version_MovesOnAWriteAndOnAnotherAppsCopy_NotOnAPaste()
    {
        var start = Hydra.Clipboard.Count();
        Write(Snapshot("written", null, null));
        var afterWrite = Hydra.Clipboard.Count();
        _otherApp.Clipboard.Read().AsContents();
        var afterPaste = Hydra.Clipboard.Count();
        OtherAppCopies("written");
        var afterCopy = Hydra.Clipboard.Count();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(start, Is.Not.Null);
            Assert.That(afterWrite, Is.Not.EqualTo(start));
            Assert.That(afterPaste, Is.EqualTo(afterWrite));
            Assert.That(afterCopy, Is.Not.EqualTo(afterWrite), "an identical copy moves it too");
        }
    }

    [Test]
    public void WrittenAt_IsTheVersionRightAfterTheWrite_AndAForeignCopyMovesPastIt()
    {
        var written = Hydra.Written(Snapshot("written", null, null));
        var afterWrite = Hydra.Clipboard.Version;
        OtherAppCopies("written");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written.At, Is.Not.Null.And.EqualTo(afterWrite));
            Assert.That(Hydra.Clipboard.Count(), Is.GreaterThan(written.Count() ?? long.MaxValue));
        }
    }

    [Test]
    public void WrittenAt_OfAClear_IsTheVersionAfterIt()
    {
        Write(Snapshot("written", null, null));

        Assert.That(Hydra.Cleared().At, Is.EqualTo(Hydra.Clipboard.Version));
    }

    // the selection is apart from the clipboard: keeping it never moves the clipboard's version
    [Test]
    public void Version_DoesNotMoveOnASelectionAlone()
    {
        Write(Snapshot("written", null, null));
        var before = Hydra.Clipboard.Version;

        Hydra.KeepSelection("relayed selection");

        Assert.That(Hydra.Clipboard.Version, Is.EqualTo(before));
    }

    [Test]
    public void OwnWrite_ReadsBackAsAnEcho_AndStillHoldsIt()
    {
        var written = Hydra.Written(Snapshot("written", "relayed selection", null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Hydra.Clipboard.Read().AsContents().Text, Is.Null);
            Assert.That(Hydra.Clipboard.StillHolds(written), Is.True);
        }
    }

    [Test]
    public void SameTextCopiedAgain_IsNoEcho_AndNoLongerHoldsTheWrite()
    {
        var written = Hydra.Written(Snapshot("written", "relayed selection", null));
        OtherAppCopies("written");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Hydra.Clipboard.Read().AsContents().Text, Is.EqualTo("written"));
            Assert.That(Hydra.Clipboard.StillHolds(written), Is.False);
        }
    }

    [Test]
    public void BeforeAnyWrite_HoldsNoWrite()
    {
        OtherAppCopies("copied");

        Assert.That(Hydra.Clipboard.StillHolds(null), Is.False);
    }


    [Test]
    public void OwnWrite_LeavesTheRelayedSelection()
    {
        HydraWritesThen(() => _otherApp.Clipboard.Read().AsContents()); // another app pasting it is no copy

        Assert.That(Hydra.ReadSelection().TextOrNone(), Is.EqualTo("relayed selection"));
    }

    [Test]
    public void TextCopyAfterwards_IsThePrimary()
    {
        HydraWritesThen(() => OtherAppCopies("copied"));

        Assert.That(Hydra.ReadSelection().TextOrNone(), Is.EqualTo("copied"));
    }

    [Test]
    public void SameTextCopiedAgain_IsThePrimary()
    {
        HydraWritesThen(() => OtherAppCopies("written"));

        Assert.That(Hydra.ReadSelection().TextOrNone(), Is.EqualTo("written"));
    }

    [Test]
    public void ImageCopyAfterwards_LeavesTheRelayedSelection()
    {
        HydraWritesThen(() => OtherAppCopies("text beside the image", Png));

        Assert.That(Hydra.ReadSelection().TextOrNone(), Is.EqualTo("relayed selection"));
    }

    [Test]
    public void Clear_LeavesThePlaceholderForOtherAppsAndNothingForHydra()
    {
        Write(Snapshot("written", "relayed selection", Png));

        var cleared = Hydra.Cleared();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_otherApp.Clipboard.Read().AsContents().Text, Is.EqualTo(ClipboardUtils.PlaceholderText));
            Assert.That(_otherApp.Clipboard.Read().AsContents().Png, Is.Null);
            Assert.That(Hydra.Clipboard.StillHolds(cleared), Is.True);
            Assert.That(ClipboardUtils.ReadWithFallback(Hydra, null, cleared, NullLogger.Instance, "test")!.Clipboard.IsEmpty, Is.True);
            Assert.That(_otherApp.ReadSelection().TextOrNone(), Is.Not.EqualTo(ClipboardUtils.PlaceholderText), "the placeholder is never a text copy");
            Assert.That(Hydra.ReadSelection().TextOrNone(), Is.EqualTo("relayed selection"));
        }
    }

    // its no-history markers read like a password manager's, which it is not: Hydra's read finds no copy there
    [Test]
    public void Clear_IsNeverConcealed()
    {
        Write(Snapshot("hunter2", null, null, Concealed: true));

        Clear();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ClipboardUtils.ReadWithFallback(_otherApp, null, ClipboardChanges.Of(_otherApp.Clipboard).Last, NullLogger.Instance, "test"), Is.EqualTo(ClipboardSnapshot.Empty));
            Assert.That(ClipboardUtils.ReadWithFallback(Hydra, null, ClipboardChanges.Of(Hydra.Clipboard).Last, NullLogger.Instance, "test")!.Clipboard.Secret, Is.False);
        }
    }

    [Test]
    public void Write_ReportsEveryFormatItTook()
    {
        var contents = Snapshot("written", "relayed selection", null, Html: "<b>written</b>");

        Assert.That(Taken(Write(contents)), Is.EqualTo(new ClipboardContents("written", "<b>written</b>", null, null, false)));
    }

    [Test]
    public void SelectionOnly_LeavesTheClipboardAndOutranksAnEarlierCopy()
    {
        OtherAppCopies("copied first");
        ClipboardUtils.ApplyPreservingFiles(Hydra, Snapshot(null, "selected later", null), NullLogger.Instance, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Hydra.Clipboard.Read().AsContents().Text, Is.EqualTo("copied first"));
            Assert.That(Hydra.ReadSelection().TextOrNone(), Is.EqualTo("selected later"));
        }
    }

    [Test]
    public void ConcealedWrite_ReadsAsConcealedInAnotherApp()
    {
        Write(Snapshot("hunter2", null, null, Concealed: true));
        var read = ClipboardUtils.ReadWithFallback(_otherApp, null, ClipboardChanges.Of(_otherApp.Clipboard).Last, NullLogger.Instance, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_otherApp.Clipboard.Read().AsContents().Secret, Is.True);
            Assert.That(read?.Clipboard.Text, Is.EqualTo("hunter2"));
            Assert.That(read?.Clipboard.Secret, Is.True);
        }
    }

    [Test]
    public void PlainWrite_ReadsAsNotConcealedInAnotherApp()
    {
        Write(Snapshot("plain", null, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_otherApp.Clipboard.Read().AsContents().Secret, Is.False);
            Assert.That(ClipboardUtils.ReadWithFallback(_otherApp, null, ClipboardChanges.Of(_otherApp.Clipboard).Last, NullLogger.Instance, "test")!.Clipboard.Secret, Is.False);
        }
    }
}

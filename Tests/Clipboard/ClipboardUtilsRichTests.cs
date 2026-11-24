using System.Runtime.CompilerServices;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;
using static Tests.Setup.Snapshots;

namespace Tests.Clipboard;

// ClipboardUtils: hashing, the echo filter, trimming, and reading and writing every format
[TestFixture]
public class ClipboardUtilsRichTests
{
    private static readonly NullLogger Log = NullLogger.Instance;
    private static readonly byte[] Image = [9, 9];
    private static byte[] Oversized() => new byte[ClipboardUtils.MaxClipboardBytes + 1];
    private static string OversizedText() => new('a', (int)ClipboardUtils.MaxClipboardBytes + 1);

    // -- EchoFilter --

    [Test]
    public void EchoFilter_ForgetsWhatALaterWriteReplaced()
    {
        // a write replaces every format, so an image Hydra wrote before a text is no echo once the user copies it again
        var echo = new ClipboardEchoFilter(() => true);
        echo.Track(new ClipboardContents(null, null, null, [9, 9], false));
        echo.Track(new ClipboardContents("written", null, null, null, false));

        Assert.That(echo.FilterImage([9, 9]), Is.Not.Null);
    }

    [Test]
    public void EchoFilter_PassesWhatItWroteOnceTheClipboardIsNoLongerItsWrite()
    {
        var owned = new StrongBox<bool>(true);
        var echo = new ClipboardEchoFilter(() => owned.Value);
        echo.Track(new ClipboardContents("written", "<b>written</b>", [7], [9, 9], false));
        Assert.That(echo.FilterText("written"), Is.Null, "an echo while the clipboard is still the write");
        owned.Value = false; // the user copied the same contents again

        using (Assert.EnterMultipleScope())
        {
            Assert.That(echo.FilterText("written"), Is.EqualTo("written"));
            Assert.That(echo.FilterImage([9, 9]), Is.Not.Null);
            Assert.That(echo.FilterHtml("<b>written</b>"), Is.EqualTo("<b>written</b>"));
            Assert.That(echo.FilterRtf([7]), Is.EqualTo(new byte[] { 7 }));
        }
    }

    // -- hashes: rich fields participate in ClipboardHash, the selection beside them does not --

    [Test]
    public void StableHash_IsTheSameOnEveryHost() =>
        // XXH64's published vector for "abc": a per-process seed (System.HashCode) would never match a peer's
        Assert.That(ClipboardUtils.StableHash("abc"), Is.EqualTo(0x44BC2CF5AD770999UL));

    [Test]
    public void ClipboardHash_IgnoresTheSelection() =>
        Assert.That(ClipboardUtils.ClipboardHash(Snapshot("t", "a", null).Clipboard),
            Is.EqualTo(ClipboardUtils.ClipboardHash(Snapshot("t", "b", null).Clipboard)));

    [Test]
    public void ClipboardHash_DiffersWhenHtmlDiffers()
    {
        var a = Snapshot("t", null, null, Html: "<b>a</b>");
        var b = Snapshot("t", null, null, Html: "<b>b</b>");
        Assert.That(ClipboardUtils.ClipboardHash(a.Clipboard), Is.Not.EqualTo(ClipboardUtils.ClipboardHash(b.Clipboard)));
    }

    [Test]
    public void ClipboardHash_DiffersWhenRtfDiffers()
    {
        var a = Snapshot("t", null, null, Rtf: [1, 2, 3]);
        var b = Snapshot("t", null, null, Rtf: [1, 2, 4]);
        Assert.That(ClipboardUtils.ClipboardHash(a.Clipboard), Is.Not.EqualTo(ClipboardUtils.ClipboardHash(b.Clipboard)));
    }

    [Test]
    public void ClipboardHash_StableForSameContent()
    {
        var a = Snapshot("t", "p", [9], Html: "<i>x</i>", Rtf: [7]);
        var b = Snapshot("t", "p", [9], Html: "<i>x</i>", Rtf: [7]);
        Assert.That(ClipboardUtils.ClipboardHash(a.Clipboard), Is.EqualTo(ClipboardUtils.ClipboardHash(b.Clipboard)));
    }

    // -- TrimToFit: plain text is the last thing dropped --

    [Test]
    public void TrimToFit_DropsOversizedHtml_KeepsText()
    {
        var result = ClipboardUtils.TrimToFit(new ClipboardContents("keep me", OversizedText(), null, null, false), null, Log, "test");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Clipboard.Text, Is.EqualTo("keep me"));
            Assert.That(result.Clipboard.Html, Is.Null);
        }
    }

    [Test]
    public void TrimToFit_DropsOversizedRtf_KeepsText()
    {
        var result = ClipboardUtils.TrimToFit(new ClipboardContents("keep me", null, Oversized(), null, false), null, Log, "test");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Clipboard.Text, Is.EqualTo("keep me"));
            Assert.That(result.Clipboard.Rtf, Is.Null);
        }
    }

    [Test]
    public void TrimToFit_KeepsAllWhenUnderBudget()
    {
        var result = ClipboardUtils.TrimToFit(new ClipboardContents("t", "<b>x</b>", [2], [1], false), "p", Log, "test");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Clipboard.Text, Is.EqualTo("t"));
            Assert.That(result.Clipboard.Html, Is.EqualTo("<b>x</b>"));
            Assert.That(result.Clipboard.Rtf, Is.EqualTo(new byte[] { 2 }));
        }
    }

    // -- ValidateFields: oversized rich fields are dropped, valid ones pass --

    [Test]
    public void ValidateFields_DropsOversizedHtmlAndRtf_KeepsValid()
    {
        var result = ClipboardWire.From(new ClipboardPushMessage("t", Html: OversizedText(), Rtf: Oversized()), Log, "test from host");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Clipboard.Text, Is.EqualTo("t"));
            Assert.That(result.Clipboard.Html, Is.Null);
            Assert.That(result.Clipboard.Rtf, Is.Null);
        }
    }

    [Test]
    public void ValidateFields_KeepsValidRich()
    {
        var result = ClipboardWire.From(new ClipboardPushMessage("t", Html: "<b>x</b>", Rtf: [3]), Log, "test from host");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Clipboard.Html, Is.EqualTo("<b>x</b>"));
            Assert.That(result.Clipboard.Rtf, Is.EqualTo(new byte[] { 3 }));
        }
    }

    // -- ReadWithFallback: rich reps travel with text; image copy still wins --

    [Test]
    public void ReadWithFallback_CarriesHtmlAndRtf()
    {
        var sync = new FakeClipboardSync();
        sync.SetClipboard(Snapshot("plain", null, null, Html: "<b>rich</b>", Rtf: [5, 6]));
        var result = ClipboardUtils.ReadWithFallback(sync, null, Log, "read")!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Clipboard.Text, Is.EqualTo("plain"));
            Assert.That(result.Clipboard.Html, Is.EqualTo("<b>rich</b>"));
            Assert.That(result.Clipboard.Rtf, Is.EqualTo(new byte[] { 5, 6 }));
        }
    }

    [Test]
    public void ReadWithFallback_ImageCopy_DropsRichAndText()
    {
        var sync = new FakeClipboardSync();
        // image present alongside html: an image copy wins outright, rich/text are not carried
        sync.SetClipboard(Snapshot("t", null, Image, Html: "<b>x</b>"));
        var result = ClipboardUtils.ReadWithFallback(sync, null, Log, "read")!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Clipboard.Png, Is.EqualTo(Image));
            Assert.That(result.Clipboard.Html, Is.Null);
            Assert.That(result.Clipboard.Text, Is.Null);
        }
    }

    [Test]
    public void ReadWithFallback_UsesFallbackWhenEverythingEchoSuppressed()
    {
        var sync = new FakeClipboardSync(); // Read reads nothing
        var fallback = Snapshot("ft", null, null, Html: "<i>fh</i>", Rtf: [1]).Clipboard;
        var result = ClipboardUtils.ReadWithFallback(sync, fallback, Log, "read")!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Clipboard.Text, Is.EqualTo("ft"));
            Assert.That(result.Clipboard.Html, Is.EqualTo("<i>fh</i>"));
            Assert.That(result.Clipboard.Rtf, Is.EqualTo(new byte[] { 1 }));
        }
    }

    [Test]
    public void ReadWithFallback_FileClipboard_DoesNotResurrectFallback()
    {
        var sync = new FakeClipboardSync { HasFileClipboardValue = true };
        var fallback = Snapshot("stale", null, null).Clipboard;

        var result = ClipboardUtils.ReadWithFallback(sync, fallback, Log, "read")!;

        Assert.That(result, Is.EqualTo(Snapshot(null, null, null)));
    }

    [Test]
    public void ReadWithFallback_ExternalOwner_DoesNotResurrectFallback()
    {
        var sync = new FakeClipboardSync { StillHoldsValue = false };
        var fallback = Snapshot("stale", null, null).Clipboard;

        var result = ClipboardUtils.ReadWithFallback(sync, fallback, Log, "read")!;

        Assert.That(result, Is.EqualTo(Snapshot(null, null, null)));
    }

    [Test]
    public void ReadWithFallback_CarriesConcealed()
    {
        var sync = new FakeClipboardSync { ConcealedValue = true };
        sync.SetupText("hunter2");

        var result = ClipboardUtils.ReadWithFallback(sync, null, Log, "read")!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Clipboard.Text, Is.EqualTo("hunter2"));
            Assert.That(result.Clipboard.Secret, Is.True);
        }
    }

    [Test]
    public void ReadWithFallback_PlainCopy_IsNotConcealed()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("plain");

        Assert.That(ClipboardUtils.ReadWithFallback(sync, null, Log, "read")!.Clipboard.Secret, Is.False);
    }

    [Test]
    public void ReadWithFallback_EchoFallback_KeepsTheFallbacksConcealed()
    {
        var sync = new FakeClipboardSync(); // Read reads nothing: Hydra's own write, echo-suppressed
        var fallback = Snapshot("hunter2", null, null, Concealed: true).Clipboard;

        var result = ClipboardUtils.ReadWithFallback(sync, fallback, Log, "read")!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Clipboard.Text, Is.EqualTo("hunter2"));
            Assert.That(result.Clipboard.Secret, Is.True);
        }
    }

    [Test]
    public void ReadWithFallback_ExternalOwner_DoesNotTakeTheFallbacksConcealed()
    {
        var sync = new FakeClipboardSync { StillHoldsValue = false };
        var fallback = Snapshot("hunter2", null, null, Concealed: true).Clipboard;

        Assert.That(ClipboardUtils.ReadWithFallback(sync, fallback, Log, "read")!.Clipboard.Secret, Is.False);
    }

    // -- primary selection: a selection of its own, beside the clipboard --

    [Test]
    public void ReadWithFallback_ImageCopy_KeepsThePrimary()
    {
        var sync = new FakeClipboardSync();
        sync.SetClipboard(Snapshot("alt text", "selection", Image));

        var result = ClipboardUtils.ReadWithFallback(sync, null, Log, "read")!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Clipboard.Text, Is.Null);
            Assert.That(result.Selection, Is.EqualTo("selection"));
            Assert.That(result.Clipboard.Png, Is.EqualTo(Image));
        }
    }

    [Test]
    public void ReadWithFallback_EchoedClipboard_KeepsTheCurrentPrimary()
    {
        // the clipboard comes from the fallback, the selection never does
        var sync = new FakeClipboardSync();
        sync.SetPrimaryText("current selection");
        var fallback = Snapshot(null, "older selection", Image).Clipboard;

        var result = ClipboardUtils.ReadWithFallback(sync, fallback, Log, "read")!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Selection, Is.EqualTo("current selection"));
            Assert.That(result.Clipboard.Png, Is.EqualTo(Image));
        }
    }

    [Test]
    public void ReadWithFallback_NoSelection_DoesNotTakeTheFallbacks()
    {
        var sync = new FakeClipboardSync();
        var fallback = Snapshot("echoed", "older selection", null).Clipboard;

        var result = ClipboardUtils.ReadWithFallback(sync, fallback, Log, "read")!;

        Assert.That(result, Is.EqualTo(Snapshot("echoed", null, null)));
    }

    [Test]
    public void ReadWithFallback_FileClipboard_StillCarriesTheSelection()
    {
        var sync = new FakeClipboardSync { HasFileClipboardValue = true };
        sync.SetPrimaryText("selection");

        var result = ClipboardUtils.ReadWithFallback(sync, null, Log, "read")!;

        Assert.That(result, Is.EqualTo(Snapshot(null, "selection", null)));
    }

    [Test]
    public void TrimToFit_PrimaryRepeatingTheText_CostsNothing()
    {
        var text = new string('a', (int)(ClipboardUtils.MaxClipboardBytes / 2) + 1);

        var result = ClipboardUtils.TrimToFit(new ClipboardContents(text, "<b>rich</b>", null, null, false), text, Log, "trim");

        Assert.That(result, Is.EqualTo(Snapshot(text, text, null, Html: "<b>rich</b>")));
    }

    [Test]
    public void TrimToFit_ImageWithALargeSelection_DropsTheSelectionFirst()
    {
        // the copy is what the user asked to move; the selection beside it is the lesser of the two
        var selection = new string('s', (int)(ClipboardUtils.MaxClipboardBytes / 2) + 1);
        var image = new byte[ClipboardUtils.MaxClipboardBytes / 2];

        var result = ClipboardUtils.TrimToFit(new ClipboardContents(null, null, null, image, false), selection, Log, "trim");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Clipboard.Png, Is.SameAs(image));
            Assert.That(result.Selection, Is.Null);
        }
    }

    [Test]
    public void TrimToFit_OversizedImage_KeepsASelectionThatFits()
    {
        var image = new byte[ClipboardUtils.MaxClipboardBytes + 1];

        var result = ClipboardUtils.TrimToFit(new ClipboardContents(null, null, null, image, false), "selection", Log, "trim");

        Assert.That(result, Is.EqualTo(Snapshot(null, "selection", null)));
    }

    [Test]
    public void ApplyPreservingFiles_SelectionOnly_LeavesTheClipboardAlone()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("local copy");

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot(null, "selection", null), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.EqualTo(new Applied.SelectionAlone("selection")));
            Assert.That(sync.Text, Is.EqualTo("local copy"));
            Assert.That(sync.PrimaryText, Is.EqualTo("selection"));
        }
    }

    [Test]
    public void ApplyPreservingFiles_NowhereToKeepASelection_ReportsNothing()
    {
        var sync = new FakeClipboardSync { WriteFailsValue = true, KeepsSelectionValue = false };

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot("incoming", "selection", null), Log, "test");

        Assert.That(applied, Is.InstanceOf<Applied.Failed>().With.Property(nameof(Applied.Failed.Kept)).Null);
    }

    [Test]
    public void ApplyPreservingFiles_FailedWrite_ReportsOnlyTheSelection()
    {
        var sync = new FakeClipboardSync { WriteFailsValue = true };

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot("incoming", "selection", null), Log, "test");

        Assert.That((applied as Applied.Failed)?.Kept, Is.EqualTo(new Applied.SelectionAlone("selection")));
    }

    [Test]
    public void ApplyPreservingFiles_ConcealedMarkersRefused_KeepsOnlyTheSelection()
    {
        var sync = new FakeClipboardSync { RefusesMarkersValue = true };

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot("secret", "selection", null, Concealed: true), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That((applied as Applied.MarkersRefused)?.Kept, Is.EqualTo(new Applied.SelectionAlone("selection")));
            Assert.That(applied?.Written, Is.Not.Null, "Hydra's own write");
            Assert.That(sync.Text, Is.Null);
        }
    }

    // the placeholder refused too: nothing of the copy left, yet Hydra's own write, as every format refused is
    [Test]
    public void ApplyPreservingFiles_ConcealedMarkersAndThePlaceholderRefused_IsAFailedWriteOfHydras()
    {
        var sync = new FakeClipboardSync { RefusesMarkersValue = true };
        sync.RefusedFormats.Add(ClipboardFormat.Text);

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot("secret", "selection", null, Concealed: true), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That((applied as Applied.Failed)?.Kept, Is.EqualTo(new Applied.SelectionAlone("selection")));
            Assert.That(applied?.Written, Is.Not.Null, "Hydra's own write");
        }
    }

    [Test]
    public void ApplyPreservingFiles_ConcealedCopy_EveryFormatRefused_IsAFailedWriteOfHydras()
    {
        var sync = new FakeClipboardSync();
        sync.RefuseEveryFormat();

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot("secret", "selection", null, Concealed: true), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That((applied as Applied.Failed)?.Kept, Is.EqualTo(new Applied.SelectionAlone("selection")));
            Assert.That(applied?.Written, Is.Not.Null, "Hydra's own write");
        }
    }

    [Test]
    public void ApplyPreservingFiles_NotReplaced_IsAFailedWriteOfNoOnes()
    {
        var sync = new FakeClipboardSync { WriteFailsValue = true };

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot("secret", null, null, Concealed: true), Log, "test");

        Assert.That(applied, Is.InstanceOf<Applied.Failed>().With.Property(nameof(Applied.Written)).Null);
    }

    // a peer's copy that is the placeholder alone is written as it is, the placeholder Hydra's own write
    [Test]
    public void ApplyPreservingFiles_ThePlaceholderAlone_IsCleared()
    {
        var sync = new FakeClipboardSync();

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot(ClipboardUtils.PlaceholderText, "selection", null), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.InstanceOf<Applied.Cleared>());
            Assert.That((applied as Applied.Cleared)?.Selection, Is.EqualTo("selection"));
            Assert.That(applied?.Written?.Taken, Is.EqualTo(Snapshot(ClipboardUtils.PlaceholderText, null, null).Clipboard));
            Assert.That(sync.PrimaryText, Is.EqualTo("selection"));
        }
    }

    // a secret one with its markers, which the master then knows it holds
    [Test]
    public void ApplyPreservingFiles_ThePlaceholderAlone_Secret_KeepsItsMarkers()
    {
        var sync = new FakeClipboardSync();

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot(ClipboardUtils.PlaceholderText, null, null, Concealed: true), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.InstanceOf<Applied.Cleared>());
            Assert.That(applied?.Written?.Taken.Secret, Is.True);
            Assert.That(sync.ConcealedValue, Is.True);
        }
    }

    [Test]
    public void ApplyPreservingFiles_ThePlaceholderAlone_ItsTextRefused_IsAFailedWriteOfHydras()
    {
        var sync = new FakeClipboardSync();
        sync.RefusedFormats.Add(ClipboardFormat.Text);

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot(ClipboardUtils.PlaceholderText, "selection", null), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That((applied as Applied.Failed)?.Kept, Is.EqualTo(new Applied.SelectionAlone("selection")));
            Assert.That(applied?.Written, Is.Not.Null, "Hydra's own write");
        }
    }

    // a platform with its own selection sets it after the write, as it did beside its own write; only once the write
    // replaced the clipboard, the failed write's selection set once, after it
    [Test]
    public void ApplyPreservingFiles_NativeSelection_IsSetAfterTheWrite()
    {
        var sync = new FakeClipboardSync();
        string? selectionDuringWrite = "unset";
        sync.AfterWrite = () => selectionDuringWrite = sync.PrimaryText;

        ClipboardUtils.ApplyPreservingFiles(sync, Snapshot("copy", "selection", null), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selectionDuringWrite, Is.Null);
            Assert.That(sync.PrimaryText, Is.EqualTo("selection"));
        }
    }

    [Test]
    public void ReadWithFallback_FileClipboard_TrimsTheSelection()
    {
        var sync = new FakeClipboardSync { HasFileClipboardValue = true };
        sync.SetPrimaryText(OversizedText());

        Assert.That(ClipboardUtils.ReadWithFallback(sync, null, Log, "read")!.Selection, Is.Null);
    }

    [Test]
    public void ApplyPreservingFiles_FileClipboard_StillTakesTheSelection()
    {
        var sync = new FakeClipboardSync { HasFileClipboardValue = true };

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot("incoming", "selection", null), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.EqualTo(new Applied.SelectionAlone("selection")), "only the selection was applied");
            Assert.That(sync.SetClipboardCallCount, Is.Zero);
            Assert.That(sync.PrimaryText, Is.EqualTo("selection"));
        }
    }

    [Test]
    public void ApplyPreservingFiles_SelectionOnly_AppliesBesideFiles()
    {
        var sync = new FakeClipboardSync { HasFileClipboardValue = true };

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot(null, "selection", null), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.Not.Null);
            Assert.That(sync.PrimaryText, Is.EqualTo("selection"));
        }
    }

    [Test]
    public void ApplyPreservingFiles_FileClipboard_DoesNotOverwrite()
    {
        var sync = new FakeClipboardSync { HasFileClipboardValue = true };

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot("incoming", null, null), Log, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.Null);
            Assert.That(sync.SetClipboardCallCount, Is.Zero);
        }
    }

    // -- an unreadable clipboard, and a partially refused write --

    [Test]
    public void ReadWithFallback_Unreadable_IsUnknownNotEmpty()
    {
        var sync = new FakeClipboardSync { ReadFailsValue = true };
        var fallback = Snapshot("echoed", null, null).Clipboard;

        Assert.That(ClipboardUtils.ReadWithFallback(sync, fallback, Log, "read"), Is.Null);
    }

    // the placeholder carries the no-history markers, which read like a password manager's
    [Test]
    public void ReadWithFallback_MarkedPlaceholder_IsNeitherContentNorConcealed()
    {
        var sync = new FakeClipboardSync { StillHoldsValue = false, ConcealedValue = true };
        sync.SetupText(ClipboardUtils.PlaceholderText);

        Assert.That(ClipboardUtils.ReadWithFallback(sync, null, Log, "read"), Is.EqualTo(ClipboardSnapshot.Empty));
    }

    [Test]
    public void ReadWithFallback_TakesOneReadOfEveryFormat()
    {
        var sync = new SnapshotOnlySync(new ClipboardRead.Contents(new ClipboardContents("plain", "<b>rich</b>", null, null, Secret: true)));

        var result = ClipboardUtils.ReadWithFallback(sync, null, Log, "read");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(Snapshot("plain", null, null, Html: "<b>rich</b>", Concealed: true)));
            Assert.That(sync.Reads, Is.EqualTo(1));
        }
    }

    [Test]
    public void ReadWithFallback_FilesInTheOneRead_AreLeftAlone()
    {
        var sync = new SnapshotOnlySync(new ClipboardRead.Files());

        Assert.That(ClipboardUtils.ReadWithFallback(sync, Snapshot("stale", null, null).Clipboard, Log, "read"), Is.EqualTo(ClipboardSnapshot.Empty));
    }

    // a platform whose one read is Read, as Windows reads under one open; asking it for files apart fails the test
    private sealed class SnapshotOnlySync(ClipboardRead read) : IClipboardPlatform, IClipboard
    {
        public IClipboard Clipboard => this;
        public IPrimarySelection? PrimarySelection => null;

        public int Reads { get; private set; }

        public ClipboardRead Read()
        {
            Reads++;
            return read;
        }

        public bool HoldsFiles() => throw new AssertionException("read outside Read");
        public bool StillHolds() => false;
        public ClipboardWrite Write(ClipboardContents contents) => ClipboardUtils.Written(contents, contents, null);
    }

    [Test]
    public void WriteAll_RefusedFormat_IsLeftOutOfWhatWasWritten()
    {
        var contents = new ClipboardContents("plain", "<b>rich</b>", [1], null, false);

        var written = ClipboardUtils.WriteAll(Log, contents,
            new FormatWrite(ClipboardFormat.Html, () => false), null, new FormatWrite(ClipboardFormat.Text, () => true),
            new FormatWrite(ClipboardFormat.Rtf, () => false));

        Assert.That(written, Is.EqualTo(new ClipboardContents("plain", null, null, null, false)));
    }

    [Test]
    public void WriteAll_EveryFormatTaken_IsTheWholeWrite()
    {
        var contents = new ClipboardContents("plain", "<b>rich</b>", null, null, false);

        var written = ClipboardUtils.WriteAll(Log, contents,
            new FormatWrite(ClipboardFormat.Html, () => true), new FormatWrite(ClipboardFormat.Text, () => true));

        Assert.That(written, Is.SameAs(contents));
    }

    // as Windows and macOS write a secret copy: one whose markers were refused goes in as the placeholder, which is what
    // the write took, less its text when that was refused too; a clear is Empty either way
    [Test]
    public void Written_SecretCopy_RefusedMarkers_TookThePlaceholder()
    {
        var asked = new ClipboardContents("secret", null, null, null, true);
        var marked = ClipboardUtils.WriteMarkers(Log, asked, () => false, () => true);
        var placeholder = ClipboardUtils.Writing(ClipboardContents.Empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ClipboardUtils.Written(asked, ClipboardUtils.WriteAll(Log, marked, new FormatWrite(ClipboardFormat.Text, () => true)), 1).Taken,
                Is.EqualTo(placeholder));
            Assert.That(ClipboardUtils.Written(asked, ClipboardUtils.WriteAll(Log, marked, new FormatWrite(ClipboardFormat.Text, () => false)), 1).Taken,
                Is.EqualTo(ClipboardContents.Empty), "nothing at all taken");
            Assert.That(ClipboardUtils.Written(ClipboardContents.Empty, placeholder, 1).Taken, Is.EqualTo(ClipboardContents.Empty));
            Assert.That(ClipboardUtils.Written(ClipboardContents.Empty, placeholder with { Text = null }, 1).Taken, Is.EqualTo(ClipboardContents.Empty),
                "a clear whose placeholder was refused still emptied the clipboard");
        }
    }

    [Test]
    public void ApplyPreservingFiles_RefusedFormat_ReportsOnlyWhatWasWritten()
    {
        var sync = new FakeClipboardSync();
        sync.RefusedFormats.Add(ClipboardFormat.Rtf);

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot("plain", null, null, Html: "<b>x</b>", Rtf: [7]), Log, "test");

        Assert.That((applied as Applied.Copy)?.Written.Taken, Is.EqualTo(Snapshot("plain", null, null, Html: "<b>x</b>").Clipboard));
    }

    [Test]
    public void ApplyPreservingFiles_EveryFormatRefused_ReportsOnlyTheSelection()
    {
        var sync = new FakeClipboardSync();
        sync.RefusedFormats.Add(ClipboardFormat.Text);

        var applied = ClipboardUtils.ApplyPreservingFiles(sync, Snapshot("plain", "selection", null), Log, "test");

        Assert.That((applied as Applied.Failed)?.Kept, Is.EqualTo(new Applied.SelectionAlone("selection")));
    }
}

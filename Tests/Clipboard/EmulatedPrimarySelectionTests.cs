using Hydra.Platform;
using Tests.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using static Tests.Setup.Snapshots;

namespace Tests.Clipboard;

[TestFixture]
public class EmulatedPrimarySelectionTests
{
    // the OS clipboard: its change counter and the text of what is on it
    private long _version;
    private string? _text;
    private bool _busy;
    private int _reads;
    // the write the clipboard makes next: it reports the version it leaves, and says whether it replaced the clipboard
    private Func<Action<long>, bool> _writing = _ => true;
    private EmulatedPrimarySelection _primary = null!;

    [SetUp]
    public void SetUp()
    {
        _version = 1;
        _text = null;
        _busy = false;
        _reads = 0;
        _primary = new EmulatedPrimarySelection(new OsClipboard(this), NullLogger.Instance);
    }

    private void UserCopies(string? text)
    {
        _version++;
        _text = text;
    }

    // Hydra's write through the selection, the selection beside it set in the same step, as
    // ClipboardUtils.ApplyPreservingFiles makes it; whether it replaced the clipboard
    private bool Write(string? incoming, Func<Action<long>, bool> write)
    {
        _writing = write;
        return ((IWritesBesideSelection)_primary.Clipboard).Write(new ClipboardContents("written", null, null, null, false), incoming)
            is ClipboardWrite.Written;
    }

    private void HydraWrites(string? incoming, string written) => Write(incoming, leftAt =>
    {
        _version++;
        _text = written;
        leftAt(_version);
        return true;
    });

    private sealed class OsClipboard(EmulatedPrimarySelectionTests os) : ISelectionlessClipboard
    {
        public long Counter() => os._version;

        public SelectionRead ReadTextCopy()
        {
            os._reads++;
            return os._busy ? new SelectionRead.Unreadable("busy") : os._text is { } text ? new SelectionRead.Text(text) : new SelectionRead.None();
        }

        public ClipboardWrite Write(ClipboardContents contents, Action<long> leftAt)
        {
            var write = os._writing;
            return write(leftAt) ? ClipboardUtils.Written(contents, contents, null) : new ClipboardWrite.NotReplaced("not replaced");
        }

        public ClipboardWrite Write(ClipboardContents contents) => Write(contents, _ => { });

        public ClipboardRead Read() => new ClipboardRead.Contents(ClipboardContents.Empty);
    }

    [Test]
    public void NothingYet_IsTheUsersTextCopy()
    {
        _text = "copied";

        Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("copied"));
    }

    [Test]
    public void RelayedSelection_StandsWhileTheClipboardIsUnchanged()
    {
        _text = "older copy";
        _primary.Write("selected on linux");

        Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("selected on linux"));
    }

    [Test]
    public void CopyAfterTheRelay_IsThePrimary()
    {
        _primary.Write("selected on linux");
        UserCopies("copied since");

        Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("copied since"));
    }

    [Test]
    public void CopyWithoutText_LeavesTheRelayedSelection()
    {
        _primary.Write("selected on linux");
        UserCopies(null);

        Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("selected on linux"));
    }

    [Test]
    public void HydrasOwnWrite_IsNotACopy()
    {
        HydraWrites("selected on linux", "written");

        Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("selected on linux"));
    }

    [Test]
    public void SameTextCopiedAgain_IsACopy()
    {
        // only the change counter says whether a copy happened, so copying Hydra's text again counts
        HydraWrites("selected on linux", "written");
        UserCopies("written");

        Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("written"));
    }

    // the platform's own record of Hydra's last write, which the emulated selection no longer keeps
    [Test]
    public void Owns_OnlyWhileTheClipboardIsHydrasLastWrite()
    {
        var last = new LastWrite(() => _version, NullLogger.Instance);
        Assert.That(last.Holds(), Is.False, "nothing written yet");

        last.Write(ClipboardContents.Empty, left =>
        {
            _version++;
            _text = "written";
            left(_version);
            return ClipboardContents.Empty;
        }, _ => { }, () => _version);
        Assert.That(last.Holds(), Is.True);

        UserCopies("written");
        Assert.That(last.Holds(), Is.False, "the same text copied again is the user's");
    }

    [Test]
    public void Owns_NotAfterAWriteThatNeverSaidWhereItLeftTheClipboard()
    {
        // Windows reports no version when another app owns the clipboard after the write
        var last = new LastWrite(() => _version, NullLogger.Instance);
        last.Write(ClipboardContents.Empty, _ =>
        {
            _version++;
            return ClipboardContents.Empty;
        }, _ => { }, () => _version);

        Assert.That(last.Holds(), Is.False);
    }

    // a failed write still reports where it left the clipboard, as macOS' clear before a failure: the clipboard is Hydra's
    // write there, though no write the relay recorded left it
    [Test]
    public void Holds_AfterAFailedWriteThatReportedItsVersion()
    {
        var last = new LastWrite(() => _version, NullLogger.Instance);
        last.Write(ClipboardContents.Empty, LeavingVersion, _ => { }, () => _version);

        var failed = last.Write(ClipboardContents.Empty, left =>
        {
            LeavingVersion(left);
            return null;
        }, _ => { }, () => _version);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed, Is.InstanceOf<ClipboardWrite.NotReplaced>());
            Assert.That(last.Holds(), Is.True);
        }
    }

    // the user's copy landing after the selection is set but before the write begins is no longer caught as the primary:
    // the two are one step, so the peer's selection wins over a copy the write is about to replace
    [Test]
    public void ApplyPreservingFiles_ACopyBeforeTheWriteBegins_LeavesThePeersSelection()
    {
        var platform = new ClipboardPlatform(_primary.Clipboard, new CopyingAfterKeep(_primary, () => UserCopies("copied meanwhile")));
        _writing = leftAt =>
        {
            _version++;
            _text = "written";
            leftAt(_version);
            return true;
        };

        ClipboardUtils.ApplyPreservingFiles(platform, Snapshot("written", "selected on linux", null), NullLogger.Instance, "test");

        Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("selected on linux"));
    }

    // the selection, the user copying as soon as it is kept
    private sealed class CopyingAfterKeep(IPrimarySelection selection, Action copy) : IPrimarySelection
    {
        public SelectionRead Read() => selection.Read();

        public SelectionWrite Write(string text)
        {
            var kept = selection.Write(text);
            copy();
            return kept;
        }
    }

    private ClipboardContents LeavingVersion(Action<long> left)
    {
        _version++;
        left(_version);
        return ClipboardContents.Empty;
    }

    [Test]
    public void Read_SameVersion_ReadsTheClipboardOnce()
    {
        UserCopies("copied");
        _primary.Read().TextOrNone();
        _primary.Read().TextOrNone();

        Assert.That(_reads, Is.EqualTo(1));
    }

    [Test]
    public void Read_TextlessCopy_ReadsTheClipboardOnce()
    {
        UserCopies(null);
        _primary.Read().TextOrNone();
        _primary.Read().TextOrNone();

        Assert.That(_reads, Is.EqualTo(1));
    }

    [Test]
    public void BusyClipboard_IsTriedAgain()
    {
        _primary.Write("selected on linux");
        UserCopies("copied");
        _busy = true;
        var whileBusy = _primary.Read().TextOrNone();
        _busy = false;
        var afterwards = _primary.Read().TextOrNone();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileBusy, Is.EqualTo("selected on linux"));
            Assert.That(afterwards, Is.EqualTo("copied"));
        }
    }

    [Test]
    public void CopySeen_StaysThePrimaryAfterALaterImageCopy()
    {
        // once noticed, a text copy is the newest selection until something newer comes
        _primary.Write("selected on linux");
        UserCopies("copied");
        _primary.Read().TextOrNone();
        UserCopies(null);

        Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("copied"));
    }

    [Test]
    public void Write_KeepsACopyMadeSinceAsThePrimary()
    {
        // the clipboard is about to be overwritten: the user's copy stays the primary, an older relay cannot return
        _primary.Write("selected on linux");
        UserCopies("copied since");
        HydraWrites(null, "written");

        Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("copied since"));
    }

    [Test]
    public void Write_WithASelection_OutranksACopyFoundThere()
    {
        UserCopies("copied");
        HydraWrites("incoming selection", "written");

        Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("incoming selection"));
    }

    [Test]
    public void Read_DuringHydrasWrite_IsNotACopy()
    {
        // a read racing the write sees the half-written clipboard under a new version
        string? during = null;
        Write("relayed selection", _ =>
        {
            UserCopies("half written");
            during = _primary.Read().TextOrNone();
            return true;
        });

        Assert.That(during, Is.EqualTo("relayed selection"));
    }

    [Test]
    public void Read_DuringTheSecondOfTwoOverlappingWrites_IsNotACopy()
    {
        string? during = null;
        Write("relayed selection", _ =>
        {
            Write(null, leftAt =>
            {
                leftAt(2);
                return true;
            });
            UserCopies("half written");
            during = _primary.Read().TextOrNone();
            return true;
        });

        Assert.That(during, Is.EqualTo("relayed selection"));
    }

    [Test]
    public void FailedWrite_LeavesTheVersionItBeganAt()
    {
        // a write that never learned its version must not make the clipboard Hydra wrote earlier look like a copy
        HydraWrites("relayed selection", "written");
        Write(null, _ => false);

        Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("relayed selection"));
    }

    [Test]
    public void SelectionRelayedDuringAWrite_IsNotUndoneByIt()
    {
        // the write finishing reports an older version than the relay saw; the newer one stands
        Write(null, leftAt =>
        {
            UserCopies("older copy");
            _primary.Write("relayed during the write");
            leftAt(_version - 1);
            return false;
        });

        Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("relayed during the write"));
    }

    [Test]
    public void WriteThatThrows_IsAFailedWriteThatStillEnds()
    {
        var replaced = Write("relayed selection", _ => throw new InvalidOperationException("write failed"));
        UserCopies("copied after");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replaced, Is.False);
            Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("copied after"));
        }
    }

    [Test]
    public void CopyUnreadableWhenAWriteBegan_IsReadOnceTheWriteNeverHappened()
    {
        UserCopies("copied");
        _busy = true;
        Write(null, _ => false);
        _busy = false;

        Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("copied"));
    }

    [Test]
    public void RelayedSelection_OutranksACopyMadeBeforeIt()
    {
        UserCopies("older copy");
        _primary.Write("newer selection");

        Assert.That(_primary.Read().TextOrNone(), Is.EqualTo("newer selection"));
    }
}

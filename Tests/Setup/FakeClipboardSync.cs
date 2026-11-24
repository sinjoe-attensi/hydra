using Hydra.Platform;

namespace Tests.Setup;

// a platform's clipboard and its primary selection in one
public sealed class FakeClipboardSync : IClipboardPlatform, IClipboard, IPrimarySelection
{
    public IClipboard Clipboard => this;
    public IPrimarySelection? PrimarySelection => this;

    public string? Text { get; private set; }
    public string? PrimaryText { get; private set; }
    public byte[]? ImagePng { get; private set; }
    public string? Html { get; private set; }
    public byte[]? Rtf { get; private set; }
    public int ReadCallCount => Volatile.Read(ref _reads);
    private int _reads;
    public int ReadPrimaryCallCount { get; private set; }
    public int SetClipboardCallCount { get; private set; }
    public int ClearClipboardCallCount { get; private set; }
    public bool HasFileClipboardValue { get; set; }

    // whether the clipboard still holds Hydra's write, whichever it was (StillHolds); another app taking the clipboard is a
    // copy, which moves the change counter
    public bool StillHoldsValue
    {
        get => _stillHolds;
        set
        {
            if (_stillHolds && !value) _changes++;
            _stillHolds = value;
        }
    }
    private bool _stillHolds = true;

    // the change counter: every user copy and every one of Hydra's writes and clears moves it; unsupported while false
    public bool ChangeCountSupported { get; set; } = true;
    private long _changes;
    public bool WriteFailsValue { get; set; }
    // a clear fails, as with the clipboard held open by another app
    public bool ClearFailsValue { get; set; }
    public bool KeepsSelectionValue { get; set; } = true;
    // the clipboard's reads are unreadable, as for a clipboard held open by another app
    public bool ReadFailsValue { get; set; }
    // the formats a write leaves out, as a clipboard refusing them does
    public HashSet<ClipboardFormat> RefusedFormats { get; } = [];
    // the concealed markers are refused, so a concealed copy is never written
    public bool RefusesMarkersValue { get; set; }
    // the reads see nothing of Hydra's own write, as a platform filters its echo, until the test sets the clipboard
    public bool HidesOwnWriteValue { get; set; }
    private bool _ownWrite;

    // while held, a read parks in Read until released, as against a hung clipboard owner; it returns the text it saw
    // before parking, as a read slow to answer with an older clipboard
    public Gate ReadGate { get; } = new();
    // a read reaching the held gate, before it parks
    public NotificationQueue<string> ReadsParked { get; } = new();

    // the clipboard carries a password manager's markers
    public bool ConcealedValue { get; set; }

    public ClipboardRead Read()
    {
        if (HoldsFiles()) return new ClipboardRead.Files();
        Interlocked.Increment(ref _reads);
        var text = Text;
        if (ReadGate.IsHeld) ReadsParked.Push("parked");
        ReadGate.Passed().GetAwaiter().GetResult();
        if (ReadFailsValue) return new ClipboardRead.Unreadable("the fake clipboard is unreadable");
        return new ClipboardRead.Contents(new ClipboardContents(Readable(text), Readable(Html), Readable(Rtf), Readable(ImagePng), ConcealedValue));
    }

    private T? Readable<T>(T? value) where T : class => HidesOwnWriteValue && _ownWrite ? null : value;

    public SelectionRead ReadPrimary()
    {
        ReadPrimaryCallCount++;
        return PrimaryText is { } primary ? new SelectionRead.Text(primary) : new SelectionRead.None();
    }

    SelectionRead IPrimarySelection.Read() => ReadPrimary();

    public SelectionWrite Write(string text) => SetPrimaryText(text) ? new SelectionWrite.Kept() : new SelectionWrite.NotKept("the fake keeps no selection");

    public bool SetPrimaryText(string text)
    {
        if (!KeepsSelectionValue) return false;
        PrimaryText = text;
        return true;
    }

    public bool HoldsFiles() => HasFileClipboardValue;

    public bool StillHolds(ClipboardWrite.Written? write) => StillHoldsValue;

    public ClipboardVersion? Version
    {
        get
        {
            OnChangeCount?.Invoke();
            return ChangeCountSupported ? new ClipboardVersion(_changes) : null;
        }
    }

    // runs as the change counter is read, to see where it is read from or act between the reads
    public Action? OnChangeCount { get; set; }

    // the count Hydra's own last write or clear left, as the platforms report it in Written.At
    private long? _wroteAt;

    // runs as a write ends, failed or not, as a copy landing during it
    public Action? AfterWrite { get; set; }

    // a write of contents counts in SetClipboardCallCount, a clear (Empty) in ClearClipboardCallCount
    public ClipboardWrite Write(ClipboardContents contents)
    {
        if (contents.IsEmpty) return Clear();
        try
        {
            return WriteContents(contents);
        }
        finally
        {
            AfterWrite?.Invoke();
        }
    }

    private ClipboardWrite WriteContents(ClipboardContents contents)
    {
        SetClipboardCallCount++;
        // mirror real implementations: every format replaced, a refused one left empty
        if (WriteFailsValue) return new ClipboardWrite.NotReplaced("the fake's write fails");
        _wroteAt = ++_changes;
        // as the platforms do when the markers are refused: the placeholder in place of the copy, taken as empty
        if (contents.Secret && RefusesMarkersValue)
        {
            Text = null;
            ImagePng = null;
            Html = null;
            Rtf = null;
            ConcealedValue = false;
            return Written(ClipboardContents.Empty);
        }
        var written = contents with
        {
            Text = Kept(ClipboardFormat.Text, contents.Text),
            Png = Kept(ClipboardFormat.Image, contents.Png),
            Html = Kept(ClipboardFormat.Html, contents.Html),
            Rtf = Kept(ClipboardFormat.Rtf, contents.Rtf),
        };
        Text = written.Text;
        ImagePng = written.Png;
        Html = written.Html;
        Rtf = written.Rtf;
        // the platforms write the markers beside a concealed copy
        ConcealedValue = written.Secret;
        _ownWrite = true;
        return Written(written);
    }

    private ClipboardWrite.Written Written(ClipboardContents taken) =>
        new(taken, ChangeCountSupported && _wroteAt is { } at ? new ClipboardVersion(at) : null);

    private T? Kept<T>(ClipboardFormat format, T? value) where T : class => RefusedFormats.Contains(format) ? null : value;

    // a write then keeps no clipboard format, though it was made
    public void RefuseEveryFormat() => RefusedFormats.UnionWith(Enum.GetValues<ClipboardFormat>());

    // the selection is left as it is; Hydra reads the clear's placeholder as empty, so the fake keeps nothing
    private ClipboardWrite Clear()
    {
        ClearClipboardCallCount++;
        if (ClearFailsValue) return new ClipboardWrite.NotReplaced("the fake's clear fails");
        _wroteAt = ++_changes;
        Text = null;
        ImagePng = null;
        Html = null;
        Rtf = null;
        ConcealedValue = false;
        return Written(ClipboardContents.Empty);
    }

    // helpers for test setup (bypass the call counters)
    public void SetupText(string text)
    {
        Text = text;
        _ownWrite = false;
        _changes++;
    }

    public void SetupImage(byte[]? png)
    {
        ImagePng = png;
        _ownWrite = false;
        _changes++;
    }
}

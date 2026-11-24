using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

// no primary selection: Hydra keeps an EmulatedPrimarySelection beside it (ClipboardPlatform.Compose)
public sealed class MacClipboardSync : IClipboardPlatform, ISelectionlessClipboard
{
    private const string PasteboardTypeString = "public.utf8-plain-text";
    private const string PasteboardTypePng = "public.png";
    private const string PasteboardTypeHtml = "public.html";
    private const string PasteboardTypeRtf = "public.rtf";
    private const string PasteboardTypeFileUrl = "public.file-url";
    private const string PasteboardTypeLegacyFileNames = "NSFilenamesPboardType";
    private const string PasteboardTypeConcealed = "org.nspasteboard.ConcealedType";
    private const string PasteboardTypeTransient = "org.nspasteboard.TransientType";

    // the formats sent as files, never as their names, and those whose text is no text copy of the user's
    private static readonly string[] FileTypes = [PasteboardTypeFileUrl, PasteboardTypeLegacyFileNames];
    private static readonly string[] ConcealedTypes = [PasteboardTypeConcealed, PasteboardTypeTransient];
    private static readonly string[] NotTextCopyTypes = [PasteboardTypePng, .. FileTypes, .. ConcealedTypes];

    private readonly ILogger<MacClipboardSync> _log;
    private readonly ClipboardEchoFilter _echo;
    private readonly LastWrite _lastWrite;
    private readonly string? _pasteboardName;

    public MacClipboardSync(ILogger<MacClipboardSync> log) : this(log, null) { }

    // a named pasteboard for tests, which leave the user's general one alone
    internal MacClipboardSync(ILogger<MacClipboardSync> log, string? pasteboardName)
    {
        _log = log;
        _pasteboardName = pasteboardName;
        _lastWrite = new LastWrite(Counter, log);
        _echo = new ClipboardEchoFilter(_lastWrite.Holds);
        // NSPasteboard lives in AppKit — must be loaded before objc_getClass can find it.
        // Slaves don't open an event tap, so AppKit may not be loaded otherwise.
        NativeHelpers.EnsureAppKitLoaded();
    }

    public IClipboard Clipboard => this;
    public IPrimarySelection? PrimarySelection => null;

    // tests only: runs between the read's first change count and its last, so a test can copy in the middle of a read,
    // which no pasteboard of its own can time
    internal Action? DuringRead { get; set; }

    // every type in one pool, against one change count: a copy made during the read is read again, once
    public ClipboardRead Read()
    {
        try
        {
            if (Guarded("clipboard", ReadAllOnce) is { } first) return first;
            if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("The pasteboard changed while it was read; reading it again");
            return Guarded("clipboard", ReadAllOnce) ?? new ClipboardRead.Unreadable("the pasteboard changed while it was read, twice");
        }
        catch (ClipboardUnreadableException e)
        {
            return e.AsRead();
        }
    }

    // null when the pasteboard changed during the read
    private ClipboardRead? ReadAllOnce(nint pasteboard)
    {
        var before = ChangeCount(pasteboard);
        var types = PasteboardTypes(pasteboard).ToList();
        ClipboardRead read = types.Any(t => FileTypes.Contains(t)) ? new ClipboardRead.Files() : ReadFormats(pasteboard, types);
        DuringRead?.Invoke();
        return ChangeCount(pasteboard) == before ? read : null;
    }

    private ClipboardRead.Contents ReadFormats(nint pasteboard, List<string?> types)
    {
        var text = _echo.FilterText(ReadString(pasteboard, PasteboardTypeString));
        var png = _echo.FilterImage(ReadData(pasteboard, PasteboardTypePng));
        var html = _echo.FilterHtml(ReadString(pasteboard, PasteboardTypeHtml));
        var rtf = _echo.FilterRtf(ReadData(pasteboard, PasteboardTypeRtf));
        return new ClipboardRead.Contents(new ClipboardContents(text, html, rtf, png, types.Any(t => ConcealedTypes.Contains(t))));
    }

    // one read of the general pasteboard in its own pool; unreadable when there is none or the read fails
    private T Guarded<T>(string what, Func<nint, T> read)
    {
        using var pool = new ObjcAutoreleasePool();
        try
        {
            var pasteboard = GetGeneralPasteboard();
            if (pasteboard != nint.Zero) return read(pasteboard);
        }
        catch (Exception ex)
        {
            throw new ClipboardUnreadableException($"the pasteboard's {what} could not be read", ex);
        }
        throw new ClipboardUnreadableException($"no pasteboard to read the {what} from");
    }

    private static string? ReadString(nint pasteboard, string type)
    {
        using var typeStr = new NsString(type);
        var result = NativeMethods.objc_msgSend(pasteboard, NativeMethods.sel_registerName("stringForType:"), typeStr);
        return result == nint.Zero ? null : NativeHelpers.CfStringToManaged(result);
    }

    private static byte[]? ReadData(nint pasteboard, string type)
    {
        using var typeStr = new NsString(type);
        var nsData = NativeMethods.objc_msgSend(pasteboard, NativeMethods.sel_registerName("dataForType:"), typeStr);
        if (nsData == nint.Zero) return null;

        var length = NativeMethods.CFDataGetLength(nsData);
        var ptr = NativeMethods.CFDataGetBytePtr(nsData);
        if (length <= 0 || ptr == nint.Zero) return null;

        var bytes = new byte[(int)length];
        Marshal.Copy(ptr, bytes, 0, (int)length);
        return bytes;
    }

    // by pasteboard type, in a pool of its own
    public SelectionRead ReadTextCopy()
    {
        using var pool = new ObjcAutoreleasePool();
        return ReadTextCopy(GetGeneralPasteboard());
    }

    private static SelectionRead ReadTextCopy(nint pasteboard) =>
        !PasteboardTypes(pasteboard).Any(t => NotTextCopyTypes.Contains(t)) && ReadString(pasteboard, PasteboardTypeString) is { } text
            ? new SelectionRead.Text(text)
            : new SelectionRead.None();

    private static long ChangeCount(nint pasteboard) => NativeMethods.objc_msgSend_long(pasteboard, NativeMethods.sel_registerName("changeCount"));

    // the counter EmulatedPrimarySelection versions by, in a pool of its own
    public long Counter()
    {
        using var pool = new ObjcAutoreleasePool();
        return ChangeCount(GetGeneralPasteboard());
    }

    // the same counter; null when there is no pasteboard
    public ClipboardVersion? Version
    {
        get
        {
            using var pool = new ObjcAutoreleasePool();
            var pasteboard = GetGeneralPasteboard();
            return pasteboard == nint.Zero ? null : new ClipboardVersion(ChangeCount(pasteboard));
        }
    }

    // a write made without EmulatedPrimarySelection, which no selection sees; a failure is logged, not thrown
    public ClipboardWrite Write(ClipboardContents contents)
    {
        try
        {
            return Write(contents, _ => { });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write clipboard");
            return new ClipboardWrite.NotReplaced(ex.Message);
        }
    }

    public ClipboardWrite Write(ClipboardContents asked, Action<long> leftAt)
    {
        using var pool = new ObjcAutoreleasePool();
        var contents = ClipboardUtils.Writing(asked);
        return _lastWrite.Write(asked, left =>
        {
            var pasteboard = GetGeneralPasteboard();
            if (pasteboard == nint.Zero) return null;
            // single clear, then write every representation atomically; the count the clear leaves is ours, so a copy
            // another app makes after it is still the user's
            var cleared = NativeMethods.objc_msgSend_long(pasteboard, NativeMethods.sel_registerName("clearContents"));
            left(cleared);
            var marked = ClipboardUtils.WriteMarkers(_log, contents, () => WriteConcealedMarkers(pasteboard),
                () => WriteData(pasteboard, [], PasteboardTypeTransient));
            _echo.Track(marked);
            // the clear replaced it
            return ClipboardUtils.WriteAll(_log, marked,
                marked.Text is { } text ? new FormatWrite(ClipboardFormat.Text, () => WriteString(pasteboard, text, PasteboardTypeString)) : null,
                marked.Html is { } html ? new FormatWrite(ClipboardFormat.Html, () => WriteString(pasteboard, html, PasteboardTypeHtml)) : null,
                marked.Rtf is { } rtf ? new FormatWrite(ClipboardFormat.Rtf, () => WriteData(pasteboard, rtf, PasteboardTypeRtf)) : null,
                marked.Png is { } png ? new FormatWrite(ClipboardFormat.Image, () => WriteData(pasteboard, png, PasteboardTypePng)) : null);
        }, leftAt, () => Version?.Value);
    }

    // every marker, even after one is refused
    private static bool WriteConcealedMarkers(nint pasteboard)
    {
        var all = true;
        foreach (var type in ConcealedTypes)
            all &= WriteData(pasteboard, [], type);
        return all;
    }

    // no files when the pasteboard cannot be read: Read says it is unreadable
    public bool HoldsFiles()
    {
        try
        {
            return Guarded("types", pasteboard => PasteboardTypes(pasteboard).Any(t => FileTypes.Contains(t)));
        }
        catch (ClipboardUnreadableException)
        {
            return false;
        }
    }

    private static IEnumerable<string?> PasteboardTypes(nint pasteboard)
    {
        var types = NativeMethods.objc_msgSend_noarg(pasteboard, NativeMethods.sel_registerName("types"));
        if (types == nint.Zero) yield break;

        var count = NativeMethods.objc_msgSend_long(types, NativeMethods.sel_registerName("count"));
        var objectAtIndex = NativeMethods.sel_registerName("objectAtIndex:");
        for (long i = 0; i < count; i++)
        {
            var type = NativeMethods.objc_msgSend_nuint(types, objectAtIndex, (nuint)i);
            if (type != nint.Zero) yield return NativeHelpers.CfStringToManaged(type);
        }
    }

    // by the version Hydra's last write reported, whichever write that was
    public bool StillHolds()
    {
        using var pool = new ObjcAutoreleasePool();
        return _lastWrite.Holds();
    }

    // setString:forType: and setData:forType: answer whether the pasteboard took it, as a BOOL: only its low byte is set
    internal static bool WriteString(nint pasteboard, string value, string type)
    {
        using var nsStr = new NsString(value);
        using var typeStr = new NsString(type);
        return (byte)NativeMethods.objc_msgSend_2arg(pasteboard, NativeMethods.sel_registerName("setString:forType:"), nsStr, typeStr) != 0;
    }

    private static unsafe bool WriteData(nint pasteboard, byte[] bytes, string type)
    {
        nint nsData;
        fixed (byte* ptr = bytes)
            nsData = NativeMethods.objc_msgSend_ptr_nuint(NativeMethods.objc_getClass("NSData"), NativeMethods.sel_registerName("dataWithBytes:length:"), ptr,
                (nuint)bytes.Length);
        if (nsData == nint.Zero) return false;

        using var typeStr = new NsString(type);
        return (byte)NativeMethods.objc_msgSend_2arg(pasteboard, NativeMethods.sel_registerName("setData:forType:"), nsData, typeStr) != 0;
    }

    private nint GetGeneralPasteboard() => Pasteboard(_pasteboardName);

    // the general pasteboard, or the named one
    internal static nint Pasteboard(string? named)
    {
        var cls = NativeMethods.objc_getClass("NSPasteboard");
        if (cls == nint.Zero) return nint.Zero;
        if (named == null) return NativeMethods.objc_msgSend_noarg(cls, NativeMethods.sel_registerName("generalPasteboard"));

        using var name = new NsString(named);
        return NativeMethods.objc_msgSend(cls, NativeMethods.sel_registerName("pasteboardWithName:"), name);
    }
}

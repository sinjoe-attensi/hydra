using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

// a clipboard that could not be read, which says nothing of what it holds: held open by another app, an owner that
// never answered, a native call that failed. Thrown only inside a platform, which answers it as Unreadable
internal sealed class ClipboardUnreadableException(string message, Exception? inner = null) : Exception(message, inner)
{
    public ClipboardRead.Unreadable AsRead() => new(Message, InnerException);

    public SelectionRead.Unreadable AsSelection() => new(Message, InnerException);
}

// a format of the clipboard a write may refuse
public enum ClipboardFormat { Text, Html, Rtf, Image }

// one format a platform writes, and the write, which says whether the clipboard took it
public sealed record FormatWrite(ClipboardFormat Format, Func<bool> Write);

// a host's clipboard and its primary selection, which are apart: a write of one never touches the other
public interface IClipboardPlatform
{
    IClipboard Clipboard { get; }

    // X11's PRIMARY or Wayland's primary selection; null on a platform without one of its own (the raw macOS and
    // Windows platforms, NullClipboardSync). ClipboardPlatform.Compose puts an EmulatedPrimarySelection in place of the
    // null for a selectionless clipboard, so Hydra sees one there
    IPrimarySelection? PrimarySelection { get; }
}

// no member throws: a clipboard that could not be read answers Unreadable, never empty, and one that could not be
// written NotReplaced. ISelectionlessClipboard's own members are the exception
public interface IClipboard
{
    // every format (HTML portable, without Windows' CF_HTML wrapper) and whether it carries a password manager's
    // concealed/transient markers, in one read
    ClipboardRead Read();

    // atomically clears and writes the given contents and says what it took: the contents less any format the clipboard
    // refused. Empty contents are a clear: the placeholder, which Hydra reads as empty and never as secret, marked
    // no-history/no-cloud where the platform has such markers (kept out of clipboard history and cloud sync, yet left
    // for clipboard managers to see), taken as Empty. A secret copy's markers are written before its content, so no
    // clipboard manager or monitor sees the content without them; a copy whose markers were refused is never written
    // unprotected, the placeholder going in its place, which is what it took. Contents that are the placeholder alone
    // are written as they are
    ClipboardWrite Write(ClipboardContents contents);

    // copied files, which Hydra leaves as they are
    bool HoldsFiles() => false;

    // the OS clipboard's change counter, which only grows: moved by every copy (X11: only with XFixes, else only
    // on an owner change) and every one of Hydra's own writes, never by a write of the primary selection alone, so a copy
    // made since a point is told apart from content that merely matches; null where the platform keeps none. The version
    // a write of Hydra's left travels in its Written.At
    ClipboardVersion? Version => null;

    // whether nothing has replaced Hydra's last write here since, so a read that finds no format (its echo suppressed)
    // can still mean it: the clipboard is still at the version that write reported, a failed write's too (Windows, macOS,
    // the portal), or Hydra still owns it (X11, wl-clipboard); false before any write there. A clipboard that tracks
    // neither answers true
    bool StillHolds() => true;
}

// the primary selection: what was last selected, apart from the clipboard
public interface IPrimarySelection
{
    SelectionRead Read();

    // whether this host kept it
    SelectionWrite Write(string text);
}

// A clipboard with no primary selection of its own, whose text copies EmulatedPrimarySelection keeps as one. Counter is
// Version as the OS keeps it, never null, which only grows; ReadTextCopy reads the text of a text copy: none for one whose
// text only names an image or files we send instead, or for a password manager's secret copy; unreadable when the
// clipboard cannot be read, to be tried again. These members may throw: only EmulatedPrimarySelection calls them, and it
// catches
public interface ISelectionlessClipboard : IClipboard
{
    long Counter();

    SelectionRead ReadTextCopy();

    // Write, reporting the version it leaves the clipboard at as soon as it knows it, even a failed or partial write's
    ClipboardWrite Write(ClipboardContents contents, Action<long> leftAt);
}

// a clipboard whose emulated selection is set in the same step as a write beside it, so a copy the user makes before the
// write begins is caught before the selection is set, never after it (EmulatedPrimarySelection)
internal interface IWritesBesideSelection
{
    // Write, selection set first unless null
    ClipboardWrite Write(ClipboardContents contents, string? selection);
}

public sealed record ClipboardPlatform(IClipboard Clipboard, IPrimarySelection? PrimarySelection) : IClipboardPlatform
{
    // the platform as Hydra uses it, the one place this is decided: one without a primary selection of its own keeps an
    // emulated one, which every write goes through. Never the platform object itself, so whoever owns that disposes it
    // once (a container disposes each disposable a factory hands out)
    public static IClipboardPlatform Compose(IClipboardPlatform platform, ILogger log)
    {
        if (platform.PrimarySelection != null || platform.Clipboard is not ISelectionlessClipboard selectionless)
            return new ClipboardPlatform(platform.Clipboard, platform.PrimarySelection);
        var emulated = new EmulatedPrimarySelection(selectionless, log);
        return new ClipboardPlatform(emulated.Clipboard, emulated);
    }
}

// a primary selection read and written by the given calls
internal sealed class DelegatedSelection(Func<SelectionRead> read, Func<string, SelectionWrite> write) : IPrimarySelection
{
    public SelectionRead Read() => read();

    public SelectionWrite Write(string text) => write(text);
}

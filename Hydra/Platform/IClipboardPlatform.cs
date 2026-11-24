using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

// snapshot of clipboard state with the selection beside it — the unit synced between peers and the echo-suppression
// fallback; the platforms see the two apart (IClipboardPlatform).
// Html is portable raw HTML (no Windows CF_HTML wrapper); Rtf is raw RTF bytes. Both are optional
// rich representations that ride alongside the plain Text (which stays the universal fallback).
// Concealed: a password manager's copy, kept out of history and cloud sync wherever it is written.
public record ClipboardSnapshot(string? Text, string? PrimaryText, byte[]? ImagePng, string? Html = null, byte[]? Rtf = null, bool Concealed = false)
{
    // anything for the clipboard itself, beside the selection
    public bool HasClipboard => !string.IsNullOrEmpty(Text) || ImagePng != null || Html != null || Rtf != null;

    public bool HasSelection => !string.IsNullOrEmpty(PrimaryText);

    public bool SelectionIsText => IsCopyOfText(PrimaryText, Text);

    public static bool IsCopyOfText(string? primaryText, string? text) => text != null && primaryText == text;

    public bool IsEmpty => !HasClipboard && !HasSelection;

    public bool IsSelectionOnly => !HasClipboard && HasSelection;

    public static ClipboardSnapshot? SelectionOnly(string? selection) => string.IsNullOrEmpty(selection) ? null : new ClipboardSnapshot(null, selection, null);

    // nothing for the clipboard, and no selection to change
    public static readonly ClipboardSnapshot Empty = new(null, null, null);

    // what a clear writes: clipboard managers refill an empty clipboard, which would read as the user's copy
    public const string PlaceholderText = " ";

    public static readonly ClipboardSnapshot Placeholder = new(PlaceholderText, null, null);

    // the placeholder alone, which is never content
    public static bool IsPlaceholder(string? text, byte[]? image, string? html, byte[]? rtf) =>
        text == PlaceholderText && image == null && html == null && rtf == null;

    // a clear's write: the placeholder, whatever selection beside it
    public bool IsClear => IsPlaceholder(Text, ImagePng, Html, Rtf);

    // the clipboard's part, as a platform writes it: the placeholder is a clear, which writes Empty
    public ClipboardContents Clipboard => IsClear ? ClipboardContents.Empty : new ClipboardContents(Text, Html, Rtf, ImagePng, Concealed);

    // a read of the clipboard with the selection beside it
    public static ClipboardSnapshot Of(ClipboardContents contents, string? primaryText) =>
        new(contents.Text, primaryText, contents.Png, contents.Html, contents.Rtf, contents.Secret);
}

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

    // X11's PRIMARY or Wayland's primary selection; null where the OS has none (macOS, Windows), where Hydra
    // keeps an EmulatedPrimarySelection in its place (ClipboardPlatform.Compose), and where none is kept at all
    IPrimarySelection? PrimarySelection { get; }
}

// no read or write throws: a clipboard that could not be read answers Unreadable, never empty, and one that could not be
// written NotReplaced
public interface IClipboard
{
    // every format (HTML portable, without Windows' CF_HTML wrapper) and whether it carries a password manager's
    // concealed/transient markers, in one read
    ClipboardRead Read();

    // atomically clears and writes the given contents and says what it took: the contents less any format the clipboard
    // refused. Empty contents are a clear: the placeholder, which Hydra reads as empty and never as concealed, marked
    // no-history/no-cloud where the platform has such markers (kept out of clipboard history and cloud sync, yet left
    // for clipboard managers to see), taken as Empty. A concealed copy's markers are written before its content, so no
    // clipboard manager or monitor sees the content without them; a copy whose markers were refused is never written
    // unprotected, the placeholder going in its place, taken as Empty
    ClipboardWrite Write(ClipboardContents contents);

    // copied files, which Hydra leaves as they are
    bool HoldsFiles() => false;

    // True only while an all-null read can mean that this process still owns the clipboard and
    // Read echo-suppressed its representations. Platforms that can track ownership should
    // return false after another process (or the system clipboard) takes ownership.
    bool CanUseEchoFallback() => true;

    // the OS clipboard's change counter, which only grows: moved by every copy (X11: only with XFixes, else only
    // on an owner change) and every one of Hydra's own writes, never by the selection, so a copy made since a point is
    // told apart from content that merely matches; null where the platform keeps none
    long? ChangeCount() => null;

    // the count Hydra's own last successful write or clear left on that counter, as the write itself saw it, so a copy
    // landing during or after it is never taken for that write; null where the platform does not report it
    long? WroteAt() => null;
}

// the primary selection: what was last selected, apart from the clipboard
public interface IPrimarySelection
{
    SelectionRead Read();

    // whether this host kept it
    SelectionWrite Write(string text);
}

// A clipboard with no primary selection of its own, whose text copies EmulatedPrimarySelection keeps as one. Version is
// the OS clipboard's change counter, which only grows; ReadTextCopy reads the text of a text copy: none for one whose
// text only names an image or files we send instead, or for a password manager's concealed copy; unreadable when the
// clipboard cannot be read, to be tried again
public interface ISelectionlessClipboard : IClipboard
{
    long Version();

    SelectionRead ReadTextCopy();

    // Write, reporting the version it leaves the clipboard at as soon as it knows it, even a failed or partial write's;
    // it may throw, as EmulatedPrimarySelection catches
    ClipboardWrite Write(ClipboardContents contents, Action<long> leftAt);
}

public sealed record ClipboardPlatform(IClipboard Clipboard, IPrimarySelection? PrimarySelection) : IClipboardPlatform
{
    // the platform as Hydra uses it, the one place this is decided: one without a primary selection of its own keeps an
    // emulated one, which every write goes through
    public static IClipboardPlatform Compose(IClipboardPlatform platform, ILogger log)
    {
        if (platform.PrimarySelection != null || platform.Clipboard is not ISelectionlessClipboard selectionless) return platform;
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

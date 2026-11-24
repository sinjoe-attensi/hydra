using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using ByteSizeLib;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

public static class ClipboardUtils
{
    public static readonly long MaxClipboardBytes = (long)ByteSize.FromMebiBytes(16).Bytes;

    // what a clear writes: clipboard managers refill an empty clipboard, which would read as the user's copy
    public const string PlaceholderText = " ";

    // reads from sync, falling back to the clipboard Hydra last took from a peer only when every format is null and the
    // clipboard still holds Hydra's last write, whose echo was suppressed; a fresh copy of any one type keeps stale formats
    // from coming back. The selection is read as it stands, never from the fallback. Null when the clipboard could not be
    // read, which is unknown, never empty
    public static ClipboardSnapshot? ReadWithFallback(IClipboardPlatform platform, ClipboardContents? fallback, ILogger log, string context)
    {
        switch (platform.Clipboard.Read())
        {
            case ClipboardRead.Contents { Value: var contents }:
                return Read(platform, contents, fallback, log, context);
            case ClipboardRead.Files:
                if (log.IsEnabled(LogLevel.Debug))
                    log.LogDebug("Clipboard {Context} contains files; preserving the native file clipboard", context);
                return TrimToFit(ClipboardContents.Empty, SelectionText(platform), log, context);
            case ClipboardRead.Unreadable unreadable:
                log.LogWarning(unreadable.Cause, "Clipboard {Context}: the clipboard could not be read ({Reason}), so nothing is known of it this time",
                    context, unreadable.Reason);
                return null;
            default:
                throw new UnreachableException();
        }
    }

    // the selection as it stands, none where the platform keeps none
    public static SelectionRead ReadSelection(this IClipboardPlatform platform) => platform.PrimarySelection?.Read() ?? new SelectionRead.None();

    // whether the platform kept the selection; never where it keeps none
    public static bool KeepSelection(this IClipboardPlatform platform, string text) => platform.PrimarySelection?.Write(text) is SelectionWrite.Kept;

    // the selection as it stands; one that could not be read is none
    private static string? SelectionText(IClipboardPlatform platform) => platform.ReadSelection() is SelectionRead.Text { Value: var text } ? text : null;

    private static ClipboardSnapshot Read(IClipboardPlatform platform, ClipboardContents read, ClipboardContents? fallback, ILogger log, string context)
    {
        var selection = SelectionText(platform);
        // a clear's placeholder read back, as when Hydra no longer owns it, is no content, and never secret
        if (IsPlaceholder(read)) read = ClipboardContents.Empty;
        if (read is { Text: null, Png: null, Html: null, Rtf: null } && platform.Clipboard.StillHolds())
            read = fallback ?? ClipboardContents.Empty;
        // image and rich text are mutually-exclusive copy actions: an image copy wins outright; otherwise
        // carry the text plus its rich (html/rtf) representations.
        return TrimToFit(read.Png != null ? read with { Text = null, Html = null, Rtf = null } : read, selection, log, context);
    }

    // a hash every host computes alike, for what crosses the wire
    public static ulong StableHash(string text) => XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text));

    // what a clear writes, never crossing the contract: a write of Empty writes it, and the write takes it as Empty
    private static readonly ClipboardContents Placeholder = new(PlaceholderText, null, null, null, false);

    // what a write of contents writes: the placeholder for a clear
    public static ClipboardContents Writing(ClipboardContents contents) => contents.IsEmpty ? Placeholder : contents;

    // the placeholder alone, which is never content
    public static bool IsPlaceholder(ClipboardContents contents) => contents is { Text: PlaceholderText, Png: null, Html: null, Rtf: null };

    // a write of asked that replaced the clipboard: what it wrote, Empty for a clear, at the count it left
    public static ClipboardWrite.Written Written(ClipboardContents asked, ClipboardContents written, long? at) =>
        new(asked.IsEmpty ? ClipboardContents.Empty : written, at is { } count ? new ClipboardVersion(count) : null);

    // a write's contents once its markers are written, before any format, see IClipboard.Write: a secret copy's
    // markers, the placeholder going in its place when they are refused, and the placeholder's no-history ones
    public static ClipboardContents WriteMarkers(ILogger log, ClipboardContents contents, Func<bool> writeSecretMarkers,
        Func<bool> writePlaceholderMarkers)
    {
        var marked = contents;
        if (contents.Secret && !writeSecretMarkers())
        {
            log.LogWarning("The clipboard refused the concealed markers; the placeholder was written in place of the copy");
            marked = Placeholder;
        }
        if (IsPlaceholder(marked) && !writePlaceholderMarkers())
            log.LogWarning("The clipboard refused the no-history markers on its placeholder");
        return marked;
    }

    // every format of a write of contents, in the platform's order, each null when absent; one the clipboard refuses is
    // warned about, and the rest are still written. What was written: contents less the refused formats
    public static ClipboardContents WriteAll(ILogger log, ClipboardContents contents, params FormatWrite?[] writes)
    {
        var refused = writes.Where(w => w != null && !w.Write()).Select(w => w!.Format).ToList();
        if (refused.Count == 0) return contents;
        log.LogWarning("The clipboard refused {Refused}; the rest were written", string.Join(", ", refused));
        return refused.Aggregate(contents, Without);
    }

    private static ClipboardContents Without(ClipboardContents c, ClipboardFormat format) => format switch
    {
        ClipboardFormat.Text => c with { Text = null },
        ClipboardFormat.Html => c with { Html = null },
        ClipboardFormat.Rtf => c with { Rtf = null },
        _ => c with { Png = null },
    };

    // applies a peer's copy, the selection beside it: a selection alone, or beside the user's copied files, leaves the
    // clipboard as it is, and a format the clipboard refused is not in it. A copy that is the placeholder alone is written
    // as it is
    public static Applied? ApplyPreservingFiles(IClipboardPlatform platform, ClipboardSnapshot copy, ILogger log, string context)
    {
        if (copy.IsEmpty) return null;
        if (copy.Clipboard.IsEmpty) return KeptSelection(platform, copy.Selection);
        if (platform.Clipboard.HoldsFiles())
        {
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Clipboard {Context} skipped because the local clipboard contains files", context);
            return KeptSelection(platform, copy.Selection);
        }

        var write = WriteBeside(platform, copy.Clipboard, copy.Selection);
        // the selection is set again after a failed write, or one that took no format, since only the selection's write
        // says whether this host could keep it
        if (write is not ClipboardWrite.Written written) return new Applied.Failed(write, KeptSelection(platform, copy.Selection));
        if (IsPlaceholder(written.Taken))
            return IsPlaceholder(copy.Clipboard)
                ? new Applied.Cleared(written, copy.Selection)
                : new Applied.MarkersRefused(written, KeptSelection(platform, copy.Selection));
        if (written.Taken.IsEmpty) return new Applied.Failed(written, KeptSelection(platform, copy.Selection));
        return new Applied.Copy(written, copy.Selection);
    }

    // the write, with the selection beside it unless null: set in the same step, first, where the selection is emulated;
    // else after the write, once it replaced the clipboard, as a platform with a selection of its own set it
    internal static ClipboardWrite WriteBeside(IClipboardPlatform platform, ClipboardContents contents, string? selection)
    {
        if (platform.Clipboard is IWritesBesideSelection beside) return beside.Write(contents, selection);
        var write = platform.Clipboard.Write(contents);
        if (selection != null && write is ClipboardWrite.Written) platform.KeepSelection(selection);
        return write;
    }

    // the selection alone; none when not kept
    public static Applied.SelectionAlone? KeptSelection(IClipboardPlatform platform, string? selection) =>
        selection != null && platform.KeepSelection(selection) && selection.Length > 0 ? new Applied.SelectionAlone(selection) : null;

    // drop fields until the combined size fits. the selection goes first if that alone makes it fit, as the one thing
    // the user did not copy; then image, the rich reps (html, rtf), the selection, and LAST the universal plain text
    // (it's the Notepad-equivalent fallback).
    public static ClipboardSnapshot TrimToFit(ClipboardContents contents, string? selection, ILogger log, string context)
    {
        var (text, image, html, rtf) = (contents.Text, contents.Png, contents.Html, contents.Rtf);
        long textBytes = text != null ? Encoding.UTF8.GetByteCount(text) : 0;
        // a selection repeating the text crosses once
        long primaryBytes = selection != null && !(text != null && selection == text) ? Encoding.UTF8.GetByteCount(selection) : 0;
        long imageBytes = image?.Length ?? 0;
        long htmlBytes = html != null ? Encoding.UTF8.GetByteCount(html) : 0;
        long rtfBytes = rtf?.Length ?? 0;
        long Total() => textBytes + primaryBytes + imageBytes + htmlBytes + rtfBytes;
        bool Over() => Total() > MaxClipboardBytes;

        // drops a field still present while over the limit
        bool Drop(string what, ref long bytes)
        {
            if (bytes == 0 || !Over()) return false;
            log.LogWarning("Clipboard {Context} too large ({Total} bytes), dropping {What}", context, Total(), what);
            bytes = 0;
            return true;
        }

        if (Total() - primaryBytes <= MaxClipboardBytes && Drop("primary text", ref primaryBytes)) selection = null;
        if (Drop("image", ref imageBytes)) image = null;
        if (Drop("html", ref htmlBytes)) html = null;
        if (Drop("rtf", ref rtfBytes)) rtf = null;
        if (Drop("primary text", ref primaryBytes)) selection = null;
        // any primary left was a copy of the text, so it goes with it
        if (Drop("text", ref textBytes)) { text = null; selection = null; }
        return new ClipboardSnapshot(new ClipboardContents(text, html, rtf, image, contents.Secret), selection);
    }

    // xxhash64 of the clipboard's formats, to skip redundant syncs
    public static ulong ClipboardHash(ClipboardContents contents)
    {
        var hash = new XxHash64();
        Append(hash, contents.Text != null ? Encoding.UTF8.GetBytes(contents.Text) : []);
        Append(hash, contents.Png ?? []);
        Append(hash, contents.Html != null ? Encoding.UTF8.GetBytes(contents.Html) : []);
        Append(hash, contents.Rtf ?? []);
        return BitConverter.ToUInt64(hash.GetCurrentHash().AsSpan());

        static void Append(XxHash64 h, byte[] data)
        {
            h.Append(BitConverter.GetBytes(data.Length));
            h.Append(data);
        }
    }
}

using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using ByteSizeLib;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform;

public static class ClipboardUtils
{
    public static readonly long MaxClipboardBytes = (long)ByteSize.FromMebiBytes(16).Bytes;

    // reads from sync, falling back to the snapshot only when every clipboard field is null and the clipboard still holds
    // lastWritten, Hydra's last write, whose echo was suppressed; a fresh copy of any one type keeps stale fields from
    // coming back. The selection is read as it stands, never from the fallback. Null when the clipboard could not be read,
    // which is unknown, never empty
    public static ClipboardSnapshot? ReadWithFallback(IClipboardPlatform platform, ClipboardSnapshot? fallback, ClipboardWrite.Written lastWritten,
        ILogger log, string context)
    {
        switch (platform.Clipboard.Read())
        {
            case ClipboardRead.Contents { Value: var contents }:
                return Read(platform, contents, fallback, lastWritten, log, context);
            case ClipboardRead.Files:
                if (log.IsEnabled(LogLevel.Debug))
                    log.LogDebug("Clipboard {Context} contains files; preserving the native file clipboard", context);
                return TrimToFit(null, SelectionText(platform), null, null, null, log, context);
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

    private static ClipboardSnapshot Read(IClipboardPlatform platform, ClipboardContents read, ClipboardSnapshot? fallback, ClipboardWrite.Written lastWritten,
        ILogger log, string context)
    {
        var text = read.Text;
        var primaryText = SelectionText(platform);
        var image = read.Png;
        var html = read.Html;
        var rtf = read.Rtf;
        var concealed = read.Secret;
        // a clear's placeholder read back, as when Hydra no longer owns it, is no content, and never concealed
        if (ClipboardSnapshot.IsPlaceholder(text, image, html, rtf))
        {
            text = null;
            concealed = false;
        }
        if (text == null && image == null && html == null && rtf == null && platform.Clipboard.StillHolds(lastWritten))
        {
            text = fallback?.Text;
            image = fallback?.ImagePng;
            html = fallback?.Html;
            rtf = fallback?.Rtf;
            concealed = fallback?.Concealed ?? false;
        }
        // image and rich text are mutually-exclusive copy actions: an image copy wins outright; otherwise
        // carry the text plus its rich (html/rtf) representations.
        var trimmed = image != null
            ? TrimToFit(null, primaryText, image, null, null, log, context)
            : TrimToFit(text, primaryText, null, html, rtf, log, context);
        return concealed ? trimmed with { Concealed = true } : trimmed;
    }

    // a hash every host computes alike, for what crosses the wire
    public static ulong StableHash(string text) => XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text));

    // what a clear writes, never crossing the contract: a write of Empty writes it, and the write takes it as Empty
    private static readonly ClipboardContents Placeholder = new(ClipboardSnapshot.PlaceholderText, null, null, null, false);

    // what a write of contents writes: the placeholder for a clear
    public static ClipboardContents Writing(ClipboardContents contents) => contents.IsEmpty ? Placeholder : contents;

    // the placeholder alone, which is never content
    public static bool IsPlaceholder(ClipboardContents contents) => ClipboardSnapshot.IsPlaceholder(contents.Text, contents.Png, contents.Html, contents.Rtf);

    // a write that replaced the clipboard: what it wrote, the placeholder taken as Empty, at the count it left
    public static ClipboardWrite.Written Written(ClipboardContents written, long? at) =>
        new(IsPlaceholder(written) ? ClipboardContents.Empty : written, at is { } count ? new ClipboardVersion(count) : null);

    // a write's contents once its markers are written, before any format, see IClipboard.Write: a concealed copy's
    // markers, the placeholder going in its place when they are refused, and the placeholder's no-history ones
    public static ClipboardContents WriteMarkers(ILogger log, ClipboardContents contents, Func<bool> writeConcealedMarkers,
        Func<bool> writePlaceholderMarkers)
    {
        var marked = contents;
        if (contents.Secret && !writeConcealedMarkers())
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

    // applies what it may and returns that, null for nothing: a selection alone, or beside the user's copied files,
    // leaves the clipboard as it is, and a format the clipboard refused is not in it; written is told of the clipboard's
    // write whenever there was one: the write, and what it took, the selection beside it, null when it did not replace the
    // clipboard. The selection is set before the clipboard is written, as the emulated one always took it
    public static ClipboardSnapshot? ApplyPreservingFiles(IClipboardPlatform platform, ClipboardSnapshot contents, ILogger log, string context,
        Action<ClipboardWrite, ClipboardSnapshot?>? written = null)
    {
        if (contents.IsEmpty) return null;
        if (contents.IsSelectionOnly) return KeptSelection(platform, contents.PrimaryText);
        if (platform.Clipboard.HoldsFiles())
        {
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Clipboard {Context} skipped because the local clipboard contains files", context);
            return KeptSelection(platform, contents.PrimaryText);
        }

        if (contents.PrimaryText is { } selection) platform.KeepSelection(selection);
        var write = platform.Clipboard.Write(contents.Clipboard);
        var wrote = write is ClipboardWrite.Written { Taken: var taken } ? Wrote(contents, taken) : null;
        written?.Invoke(write, wrote);
        // set again after a failed write, or one that took no format, since only the selection's write says whether this
        // host could keep it; the placeholder written in place of a copy whose markers were refused is no copy applied
        return wrote is { HasClipboard: true } && (!wrote.IsClear || contents.IsClear) ? wrote : KeptSelection(platform, contents.PrimaryText);
    }

    // what a write of asked took, as a snapshot with the selection asked beside it: the placeholder for a clear, and for
    // a concealed copy whose markers were refused (taken without its secret)
    internal static ClipboardSnapshot Wrote(ClipboardSnapshot asked, ClipboardContents taken) =>
        asked.IsClear || (asked.Concealed && !taken.Secret)
            ? ClipboardSnapshot.Placeholder with { PrimaryText = asked.PrimaryText }
            : ClipboardSnapshot.Of(taken, asked.PrimaryText);

    private static ClipboardSnapshot? KeptSelection(IClipboardPlatform platform, string? selection) =>
        selection != null && platform.KeepSelection(selection) ? ClipboardSnapshot.SelectionOnly(selection) : null;

    // drop fields until the combined size fits. the selection goes first if that alone makes it fit, as the one thing
    // the user did not copy; then image, the rich reps (html, rtf), the selection, and LAST the universal plain text
    // (it's the Notepad-equivalent fallback).
    public static ClipboardSnapshot TrimToFit(string? text, string? primaryText, byte[]? image, string? html, byte[]? rtf, ILogger log, string context)
    {
        long textBytes = text != null ? Encoding.UTF8.GetByteCount(text) : 0;
        long primaryBytes = primaryText != null && !ClipboardSnapshot.IsCopyOfText(primaryText, text) ? Encoding.UTF8.GetByteCount(primaryText) : 0;
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

        if (Total() - primaryBytes <= MaxClipboardBytes && Drop("primary text", ref primaryBytes)) primaryText = null;
        if (Drop("image", ref imageBytes)) image = null;
        if (Drop("html", ref htmlBytes)) html = null;
        if (Drop("rtf", ref rtfBytes)) rtf = null;
        if (Drop("primary text", ref primaryBytes)) primaryText = null;
        // any primary left was a copy of the text, so it goes with it
        if (Drop("text", ref textBytes)) { text = null; primaryText = null; }
        return new ClipboardSnapshot(text, primaryText, image, html, rtf);
    }

    // xxhash64 of the clipboard's fields, to skip redundant syncs
    public static ulong ClipboardHash(ClipboardSnapshot snap)
    {
        var hash = new XxHash64();
        Append(hash, snap.Text != null ? Encoding.UTF8.GetBytes(snap.Text) : []);
        Append(hash, snap.ImagePng ?? []);
        Append(hash, snap.Html != null ? Encoding.UTF8.GetBytes(snap.Html) : []);
        Append(hash, snap.Rtf ?? []);
        return BitConverter.ToUInt64(hash.GetCurrentHash().AsSpan());

        static void Append(XxHash64 h, byte[] data)
        {
            h.Append(BitConverter.GetBytes(data.Length));
            h.Append(data);
        }
    }
}

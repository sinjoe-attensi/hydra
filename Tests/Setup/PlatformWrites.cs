using Hydra.Platform;

namespace Tests.Setup;

// a platform's clipboard and selection as a test drives them, in the terms of the snapshot Hydra relays
public static class PlatformWrites
{
    // Hydra's write as ClipboardUtils.ApplyPreservingFiles makes it, the selection beside it set first: what it took, with
    // the selection beside it (Empty for a clear, or for a concealed copy whose markers were refused), null when it did
    // not replace the clipboard
    public static ClipboardSnapshot? SetClipboard(this IClipboardPlatform platform, ClipboardSnapshot contents) =>
        platform.Write(contents) is ClipboardWrite.Written { Taken: var taken } ? new ClipboardSnapshot(taken, contents.Selection) : null;

    // the selection set first, then the clipboard; the placeholder alone is a clear
    private static ClipboardWrite Write(this IClipboardPlatform platform, ClipboardSnapshot contents)
    {
        if (contents.Selection is { } selection) platform.SetSelection(selection);
        return platform.Clipboard.Write(ClipboardUtils.IsPlaceholder(contents.Clipboard) ? ClipboardContents.Empty : contents.Clipboard);
    }

    // whether the clear replaced the clipboard
    public static bool ClearClipboard(this IClipboardPlatform platform) => platform.Clipboard.Write(ClipboardContents.Empty) is ClipboardWrite.Written;

    // Hydra's write as SetClipboard makes it, and the write itself, which fails the test when it did not replace the clipboard
    public static ClipboardWrite.Written Written(this IClipboardPlatform platform, ClipboardSnapshot contents) => Replaced(platform.Write(contents));

    // the clear, which fails the test when it did not replace the clipboard
    public static ClipboardWrite.Written Cleared(this IClipboardPlatform platform) => Replaced(platform.Clipboard.Write(ClipboardContents.Empty));

    private static ClipboardWrite.Written Replaced(ClipboardWrite write) =>
        write as ClipboardWrite.Written ?? throw new AssertionException($"not written: {write}");

    // the version as a count, null where the platform keeps none
    public static long? Count(this IClipboard clipboard) => clipboard.Version?.Value;

    // the version the write left, as a count
    public static long? Count(this ClipboardWrite.Written written) => written.At?.Value;

    public static SelectionRead ReadPrimary(this IClipboardPlatform platform) => platform.ReadSelection();

    // whether the platform kept it
    public static bool SetPrimaryText(this IClipboardPlatform platform, string text) => platform.SetSelection(text);

    public static bool HasFileClipboard(this IClipboardPlatform platform) => platform.Clipboard.HoldsFiles();
}

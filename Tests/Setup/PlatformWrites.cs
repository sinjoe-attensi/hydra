using Hydra.Platform;

namespace Tests.Setup;

// a platform's clipboard and selection as a test drives them, in the terms of the snapshot Hydra relays
public static class PlatformWrites
{
    // Hydra's write as ClipboardUtils.ApplyPreservingFiles makes it, the selection beside it set first: what it took, with
    // the selection beside it (the placeholder for a clear, or for a concealed copy whose markers were refused), null
    // when it did not replace the clipboard
    public static ClipboardSnapshot? SetClipboard(this IClipboardPlatform platform, ClipboardSnapshot contents)
    {
        if (contents.PrimaryText is { } selection) platform.KeepSelection(selection);
        return platform.Clipboard.Write(contents.Clipboard) is ClipboardWrite.Written { Taken: var taken } ? ClipboardUtils.Wrote(contents, taken) : null;
    }

    // whether the clear replaced the clipboard
    public static bool ClearClipboard(this IClipboardPlatform platform) => platform.Clipboard.Write(ClipboardContents.Empty) is ClipboardWrite.Written;

    public static SelectionRead ReadPrimary(this IClipboardPlatform platform) => platform.ReadSelection();

    // whether the platform kept it
    public static bool SetPrimaryText(this IClipboardPlatform platform, string text) => platform.KeepSelection(text);

    public static bool HasFileClipboard(this IClipboardPlatform platform) => platform.Clipboard.HoldsFiles();
}

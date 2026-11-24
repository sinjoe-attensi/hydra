using System.Runtime.CompilerServices;
using Hydra.Platform;

namespace Hydra.Relay;

// Hydra's last write of a clipboard, one per clipboard, so a write made as master or as slave is never taken for the
// user's copy by the other. A copy since a point is the clipboard's version moved past it and past that write's; every
// answer of the version is null where the platform keeps none
public sealed class ClipboardChanges
{
    private static readonly ConditionalWeakTable<IClipboard, ClipboardChanges> All = [];

    // Hydra's last write before it wrote here: one at no version, which no platform that tracks versions or ownership
    // holds, and one that tracks neither always does, as it answered before Hydra's first write
    public static readonly ClipboardWrite.Written NothingWritten = new(ClipboardContents.Empty, null);

    private readonly IClipboard _clipboard;
    private ClipboardWrite.Written _last = NothingWritten;

    private ClipboardChanges(IClipboard clipboard) => _clipboard = clipboard;

    public static ClipboardChanges Of(IClipboard clipboard) => All.GetValue(clipboard, c => new ClipboardChanges(c));

    // Hydra's last write here that replaced the clipboard; NothingWritten before any
    public ClipboardWrite.Written Last => Volatile.Read(ref _last);

    public ClipboardVersion? Now() => _clipboard.Version;

    // Hydra's own write, kept as the last only when took finds it replaced the clipboard, at the version the write reported,
    // so a failed write, a selection alone, or a copy landing during the write is never taken for Hydra's own
    public T Write<T>(Func<T> write, Func<T, ClipboardWrite.Written?> took)
    {
        var result = write();
        if (took(result) is { } written) Volatile.Write(ref _last, written);
        return result;
    }

    // whether it replaced the clipboard
    public bool Write(Func<ClipboardWrite> write) => Write(write, w => w as ClipboardWrite.Written) is ClipboardWrite.Written;

    // whether the user copied since at: the version moved past it and past Hydra's own last write; null when the platform
    // keeps no counter, or kept none at
    public bool? CopiedSince(ClipboardVersion? at) => CopiedSince(at, _clipboard.Version);

    // as CopiedSince(at), against now, the version as read earlier, so a caller holding a lock reads it before taking it
    public bool? CopiedSince(ClipboardVersion? at, ClipboardVersion? now)
    {
        if (at == null || now == null) return null;
        return now != at && now != Last.At;
    }

    // whether the clipboard still holds Hydra's last write here (IClipboard.StillHolds)
    public bool StillHolds() => _clipboard.StillHolds(Last);

    // whether the clipboard is still Hydra's own last write by the version, nothing copied since: never, before Hydra wrote
    // here; StillHolds where the platform keeps no counter
    public bool StillOwnWrite() => _clipboard.Version is { } now ? Last.At == now : StillHolds();
}

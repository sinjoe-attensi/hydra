using System.Runtime.CompilerServices;
using Hydra.Platform;

namespace Hydra.Relay;

// Hydra's last write of a clipboard that replaced it, as the relay recorded it, one per clipboard, so a write made as
// master or as slave is never taken for the user's copy by the other. A copy since a point is the clipboard's version
// moved past it and past that write's; every answer of the version is null where the platform keeps none. Not the write
// IClipboard.StillHolds judges, which is the platform's own last write, a failed one included
public sealed class ClipboardChanges
{
    private static readonly ConditionalWeakTable<IClipboard, ClipboardChanges> All = [];

    private readonly IClipboard _clipboard;
    private readonly Lock _lock = new();
    private ClipboardWrite.Written? _last;

    private ClipboardChanges(IClipboard clipboard) => _clipboard = clipboard;

    public static ClipboardChanges Of(IClipboard clipboard) => All.GetValue(clipboard, c => new ClipboardChanges(c));

    // Hydra's last write here that replaced the clipboard; null before any
    public ClipboardWrite.Written? Last
    {
        get
        {
            lock (_lock) return _last;
        }
    }

    public ClipboardVersion? Now() => _clipboard.Version;

    // Hydra's own write, kept as the last only when took finds it replaced the clipboard, at the version the write reported,
    // so a failed write, a selection alone, or a copy landing during the write is never taken for Hydra's own. Master and
    // slave write apart, so a write kept late never replaces a later one: the version only grows
    public T Write<T>(Func<T> write, Func<T, ClipboardWrite.Written?> took)
    {
        var result = write();
        if (took(result) is { } written)
            lock (_lock)
                if (_last?.At is not { } last || written.At is not { } at || at.Value >= last.Value)
                    _last = written;
        return result;
    }

    // the write, where it replaced the clipboard
    public ClipboardWrite.Written? Write(Func<ClipboardWrite> write) =>
        Write(write, w => w as ClipboardWrite.Written) is ClipboardWrite.Written written ? written : null;

    // whether the user copied since at: the version moved past it and past Hydra's own last write; null when the platform
    // keeps no counter, or kept none at
    public bool? CopiedSince(ClipboardVersion? at) => CopiedSince(at, _clipboard.Version);

    // as CopiedSince(at), against now, the version as read earlier, so a caller holding a lock reads it before taking it
    public bool? CopiedSince(ClipboardVersion? at, ClipboardVersion? now)
    {
        if (at == null || now == null) return null;
        return now != at && now != Last?.At;
    }

    // whether the clipboard's version is still the one Last left, nothing copied since: never, before Hydra wrote here;
    // IClipboard.StillHolds where the platform keeps no counter
    public bool VersionIsLastWrite() => _clipboard.Version is { } now ? Last?.At == now : _clipboard.StillHolds();
}

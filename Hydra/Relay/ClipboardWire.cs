using System.Text;
using Hydra.Platform;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

// What a host compares a peer's clipboard against: its clipboard's hash, and the text and selection a selection-only
// answer resolves against. Never the clipboard itself, which may be a large image.
public sealed record ClipboardBaseline(ulong? Hash, string? Text, string? Selection)
{
    public static ClipboardBaseline Of(ClipboardSnapshot s) => new(ClipboardUtils.ClipboardHash(s.Clipboard), s.Clipboard.Text, s.Selection);

    // the selection alone, against a peer that offers no clipboard, so this host's need not be read; one that could not be
    // read is none
    public static ClipboardBaseline SelectionOf(IClipboardPlatform platform) =>
        new(null, null, platform.ReadSelection() is SelectionRead.Text { Value: var selection } ? selection : null);
}

// what a slave's hash check found it needs from the master: nothing, the selection alone, or the clipboard
public enum PushNeed { None, Selection, Clipboard }

// Clipboard snapshots on the wire, and the steps of the handshake that ClipboardSlave and ClipboardMaster take.
// A primary that repeats the text is the PrimaryIsText flag, so a copy's text crosses once. The selection travels beside
// the clipboard's hash: inline when small, else as its own hash, so a change to it alone never rewrites a clipboard, and
// one that fits inline never resends one. Concealed is the clipboard's Secret.
public static class ClipboardWire
{
    private const int InlineSelectionBytes = 4096;

    public static ClipboardPushMessage ToPush(ClipboardSnapshot s) =>
        new(s.Clipboard.Text ?? "", WirePrimary(s), s.Clipboard.Png, s.Clipboard.Html, s.Clipboard.Rtf, PrimaryIsText(s), Concealed(s));

    private static ClipboardPullResponseMessage ToPullResponse(ClipboardSnapshot s, ClipboardPullMessage pull) =>
        new(pull.PullId, s.Clipboard.Text, WirePrimary(s), s.Clipboard.Png, Html: s.Clipboard.Html, Rtf: s.Clipboard.Rtf,
            PrimaryIsText: PrimaryIsText(s), Concealed: Concealed(s));

    public static ClipboardHashMessage ToHash(ClipboardSnapshot s)
    {
        var hash = s.Clipboard.IsEmpty ? (ulong?)null : ClipboardUtils.ClipboardHash(s.Clipboard);
        var primary = WirePrimary(s);
        return primary == null || Encoding.UTF8.GetByteCount(primary) <= InlineSelectionBytes
            ? new ClipboardHashMessage(hash, primary, PrimaryIsText(s))
            : new ClipboardHashMessage(hash, PrimaryHash: SelectionHash(primary));
    }

    public static ClipboardPullMessage ToPull(ClipboardBaseline against, int pullId, int? resendFrom = null) =>
        new(pullId, against.Hash, SelectionHash(against.Selection), resendFrom);

    // each field within the limit; context names the message and its sender, for the logs
    public static ClipboardSnapshot From(IClipboardPayload m, ILogger log, string context)
    {
        var text = Fit(m.Text, "text", log, context);
        var selection = Fit(Primary(m.Text, m.PrimaryText, m.PrimaryIsText), "primary text", log, context);
        var image = Fit(m.ImagePng, "image", log, context);
        var html = Fit(m.Html, "html", log, context);
        var rtf = Fit(m.Rtf, "rtf", log, context);
        return new ClipboardSnapshot(new ClipboardContents(text, html, rtf, image, m.Concealed == true), selection);
    }

    // the hash of the clipboard a payload carries, as a hash or a pull carries it
    public static ulong ContentHash(IClipboardPayload m, ILogger log, string context) => ClipboardUtils.ClipboardHash(From(m, log, context).Clipboard);

    // for the logs of both sides: sizes and flags, never the content
    public static string Describe(IClipboardPayload m) =>
        $"{(m is ClipboardPullResponseMessage { Unchanged: true } ? "unchanged, " : "")}" +
        $"{(m is ClipboardPullResponseMessage { Cleared: true } ? "cleared, " : "")}{(m.Concealed == true ? "concealed, " : "")}text={m.Text?.Length}, " +
        $"primary={(m.PrimaryIsText == true ? "the text" : m.PrimaryText?.Length)}, image={m.ImagePng?.Length}";

    // a master's hash against this host's clipboard: takes the selection beside a clipboard it already has, and says what
    // it still needs pushed
    internal static PushNeed NeedsPush(ClipboardHashMessage m, IClipboardPlatform platform, ClipboardBaseline local, ILogger log, string host)
    {
        if (m.Hash is { } hash && hash != local.Hash) return PushNeed.Clipboard;
        if (m.PrimaryHash is { } selectionHash) return selectionHash != SelectionHash(local.Selection) ? PushNeed.Selection : PushNeed.None;
        TakeSelectionBeside(platform, m, local, log, $"selection from {host}");
        return PushNeed.None;
    }

    // as NeedsPush, the clipboard here counting as matching: only a selection of the master's own beside it is taken or
    // asked for, one repeating the master's text having gone with the master's clipboard
    internal static PushNeed NeedsSelection(ClipboardHashMessage m, IClipboardPlatform platform, ClipboardBaseline local, ILogger log, string host) =>
        NeedsPush(m with { Hash = null, PrimaryIsText = null }, platform, local, log, host);

    // the selection inline beside a hash whose clipboard is still on its way, taken against the selection here alone
    internal static void TakeSelection(ClipboardHashMessage m, IClipboardPlatform platform, ILogger log, string host) =>
        NeedsPush(m with { Hash = null, PrimaryHash = null }, platform, ClipboardBaseline.SelectionOf(platform), log, host);

    // the master's selection beside a clipboard still streaming here, which that stream's own must not overwrite: the one
    // a hash carries inline, or, sent as its own hash that matched (need), the one already here; null for none
    internal static IClipboardSelection? SelectionBeside(ClipboardHashMessage m, ClipboardBaseline local, PushNeed need)
    {
        if (m.PrimaryHash == null && (m.PrimaryText != null || m.PrimaryIsText == true)) return new WireSelection(m.PrimaryText, m.PrimaryIsText);
        if (m.PrimaryHash != null && need == PushNeed.None) return new WireSelection(local.Selection, null);
        return null;
    }

    // the push with the selection given in place of its own
    internal static ClipboardPushMessage WithSelection(ClipboardPushMessage push, IClipboardSelection selection) =>
        push with { PrimaryText = selection.PrimaryText, PrimaryIsText = selection.PrimaryIsText };

    // the answer with the selection of another in place of its own
    internal static ClipboardPullResponseMessage WithSelection(ClipboardPullResponseMessage answer, IClipboardSelection selection) =>
        answer with { PrimaryText = selection.PrimaryText, PrimaryIsText = selection.PrimaryIsText };

    // the push that check asked for; with selectionFor, it was asked for the selection alone, and the clipboard, unless the
    // master's has changed since, is left as it is (a baseline without a clipboard hash takes the push whole). What it
    // applied, if anything
    internal static Applied? ApplyPush(IClipboardPlatform platform, ClipboardPushMessage push, ClipboardBaseline? selectionFor, ILogger log, string host)
    {
        var context = $"push from {host}";
        var received = From(push, log, context);
        return selectionFor != null && ClipboardUtils.ClipboardHash(received.Clipboard) == selectionFor.Hash
            ? TakeSelectionBeside(platform, push, selectionFor, log, context)
            : ClipboardUtils.ApplyPreservingFiles(platform, received, log, context);
    }

    // a slave's answer to a pull: unchanged when its clipboard matches the master's, with its selection beside it only
    // when that differs, else the whole clipboard; localHash stands in for the clipboard's own (a stream pending here)
    internal static ClipboardPullResponseMessage AnswerPull(ClipboardSnapshot local, ClipboardPullMessage pull, ulong? localHash = null)
    {
        if (pull.MasterHash != (localHash ?? ClipboardUtils.ClipboardHash(local.Clipboard))) return ToPullResponse(local, pull);
        return SelectionHash(local.Selection) == pull.MasterPrimaryHash
            ? new ClipboardPullResponseMessage(pull.PullId, null, Unchanged: true)
            : new ClipboardPullResponseMessage(pull.PullId, null, WirePrimary(local), Unchanged: true, PrimaryIsText: PrimaryIsText(local));
    }

    // the master's side of that answer, against what it pulled with: the selection alone beside an unchanged
    // clipboard, else the whole clipboard, which differs. What it applied, if anything
    internal static Applied? TakePullAnswer(IClipboardPlatform platform, ClipboardPullResponseMessage answer, ClipboardBaseline pulledAgainst, ILogger log,
        string host)
    {
        var context = $"pull response from {host}";
        return answer.Unchanged == true
            ? TakeSelectionBeside(platform, answer, pulledAgainst, log, context)
            : ClipboardUtils.ApplyPreservingFiles(platform, From(answer, log, context), log, context);
    }

    // the selection beside a clipboard this host already has, set if it differs; a primary repeating the text is the
    // receiver's own text. What it applied, if anything
    private static Applied.SelectionAlone? TakeSelectionBeside(IClipboardPlatform platform, IClipboardSelection m, ClipboardBaseline beside, ILogger log,
        string context)
    {
        var selection = Fit(Primary(beside.Text, m.PrimaryText, m.PrimaryIsText), "primary text", log, context);
        return selection != beside.Selection ? ClipboardUtils.SetSelectionAlone(platform, selection) : null;
    }

    // the text if it is within the limit, else null and a warning; empty is null
    private static string? Fit(string? text, string what, ILogger log, string context) =>
        string.IsNullOrEmpty(text) ? null : Fits(Encoding.UTF8.GetByteCount(text), what, log, context) ? text : null;

    private static byte[]? Fit(byte[]? data, string what, ILogger log, string context) =>
        data != null && Fits(data.Length, what, log, context) ? data : null;

    private static bool Fits(long bytes, string what, ILogger log, string context)
    {
        if (bytes <= ClipboardUtils.MaxClipboardBytes) return true;
        log.LogWarning("Clipboard {Context}: {What} exceeds {Max} bytes, dropping", context, what, ClipboardUtils.MaxClipboardBytes);
        return false;
    }

    // a selection as a message carries it
    private sealed record WireSelection(string? PrimaryText, bool? PrimaryIsText) : IClipboardSelection;

    private static ulong? SelectionHash(string? selection) => selection == null ? null : ClipboardUtils.StableHash(selection);
    private static bool SelectionIsText(ClipboardSnapshot s) => s.Clipboard.Text != null && s.Selection == s.Clipboard.Text;
    private static string? WirePrimary(ClipboardSnapshot s) => SelectionIsText(s) ? null : s.Selection;
    private static bool? PrimaryIsText(ClipboardSnapshot s) => SelectionIsText(s) ? true : null;
    private static bool? Concealed(ClipboardSnapshot s) => s.Clipboard.Secret ? true : null;
    private static string? Primary(string? text, string? primaryText, bool? primaryIsText) => primaryIsText == true ? text : primaryText;
}

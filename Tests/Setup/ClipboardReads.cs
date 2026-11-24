using Hydra.Platform;

namespace Tests.Setup;

// what a read found, for a test that expects it found something: one that did not fails the test
public static class ClipboardReads
{
    public static ClipboardContents AsContents(this ClipboardRead read) =>
        read is ClipboardRead.Contents { Value: var contents } ? contents : throw new AssertionException($"expected the clipboard's contents, read {read}");

    // the selection's text, null for none
    public static string? TextOrNone(this SelectionRead read) => read switch
    {
        SelectionRead.Text { Value: var text } => text,
        SelectionRead.None => null,
        _ => throw new AssertionException($"expected the selection, read {read}"),
    };
}

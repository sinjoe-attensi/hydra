using System.Diagnostics;
using System.Runtime.Versioning;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;
using static Tests.Setup.Snapshots;

namespace Tests.Platform.Windows;

// the real clipboard, which is one per session
[TestFixture]
[NonParallelizable]
[SupportedOSPlatform("windows")]
public class WindowsClipboardSyncTests : EmulatedPrimarySelectionContract
{
    protected override IClipboardPlatform Create() => new WindowsClipboardSync(NullLogger<WindowsClipboardSync>.Instance);

    [TestCase("ExcludeClipboardContentFromMonitorProcessing", 1u)]
    [TestCase("CanIncludeInClipboardHistory", 0u)]
    public void PasswordManagersCopy_IsNotThePrimary(string format, uint value)
    {
        HydraWritesThen(() => WriteForeignCopy("secret", format, value));

        Assert.That(Hydra.ReadSelection().TextOrNone(), Is.EqualTo("relayed selection"));
    }

    [Test]
    public void CopyAllowedIntoHistory_IsThePrimary()
    {
        HydraWritesThen(() => WriteForeignCopy("copied", "CanIncludeInClipboardHistory", 1));

        Assert.That(Hydra.ReadSelection().TextOrNone(), Is.EqualTo("copied"));
    }

    [Test]
    public void FilesCopiedInExplorer_AreLeftOnTheClipboard()
    {
        var file = Path.Combine(TestPaths.FreshFixtureRoot(nameof(WindowsClipboardSyncTests)), "copied.txt");
        File.WriteAllText(file, "");
        HydraWritesThen(() => RunInOtherProcess("$f = New-Object System.Collections.Specialized.StringCollection; " +
                                                $"[void]$f.Add('{file}'); [System.Windows.Forms.Clipboard]::SetFileDropList($f)"));

        var applied = ClipboardUtils.ApplyPreservingFiles(Hydra, Snapshot("pushed", null, null), NullLogger.Instance, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.Null);
            Assert.That(Hydra.Clipboard.HoldsFiles(), Is.True);
            Assert.That(Hydra.Clipboard.Read().AsContents().Text, Is.Null);
        }
    }

    [Test]
    public void ClipboardHeldOpenByAnotherApp_IsUnreadable_NotEmpty()
    {
        OtherAppCopies("copied");
        var held = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holder = new Thread(() =>
        {
            NativeMethods.OpenClipboard(nint.Zero);
            held.SetResult();
            release.Task.Wait();
            NativeMethods.CloseClipboard();
        });
        holder.Start();
        try
        {
            Assert.That(held.Task.Wait(TestBound.Bound), Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Hydra.Clipboard.Read(), Is.InstanceOf<ClipboardRead.Unreadable>(), "unknown, never empty or \"not concealed\"");
                Assert.That(ClipboardUtils.ReadWithFallback(Hydra, null, ClipboardChanges.Of(Hydra.Clipboard).Last, NullLogger.Instance, "test"), Is.Null);
            }
        }
        finally
        {
            release.SetResult();
            holder.Join();
        }
    }

    [Test]
    public void Clear_KeepsThePlaceholderOutOfHistoryAndCloud_ButNotFromMonitors()
    {
        Clear();

        Assert.That(NativeMethods.OpenClipboard(nint.Zero), Is.True);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(IsAvailable("CanIncludeInClipboardHistory"), Is.True);
                Assert.That(IsAvailable("CanUploadToCloudClipboard"), Is.True);
                Assert.That(IsAvailable("ExcludeClipboardContentFromMonitorProcessing"), Is.False);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    // on a clipboard the caller opened
    private static bool IsAvailable(string format) => NativeMethods.IsClipboardFormatAvailable(NativeMethods.RegisterClipboardFormat(format));

    [Test]
    public void ReadAll_ReadsEveryFormatAndTheMarkersAtOnce()
    {
        Write(Snapshot("hunter2", null, null, Html: "<b>hunter2</b>", Concealed: true));
        using var otherApp = (WindowsClipboardSync)Create();

        Assert.That(otherApp.Read().AsContents(), Is.EqualTo(new ClipboardContents("hunter2", "<b>hunter2</b>", null, null, Secret: true)));
    }

    [Test]
    public void ReadAll_ReadsEveryFormat()
    {
        byte[] rtf = "{\\rtf1 hunter2}"u8.ToArray();
        Write(Snapshot("hunter2", null, Png, Html: "<b>hunter2</b>", Rtf: rtf));
        using var otherApp = (WindowsClipboardSync)Create();

        var all = otherApp.Read().AsContents();

        // field by field, as the record compares its byte arrays by reference
        using (Assert.EnterMultipleScope())
        {
            Assert.That(all.Text, Is.EqualTo("hunter2"));
            Assert.That(all.Png, Is.EqualTo(Png));
            Assert.That(all.Html, Is.EqualTo("<b>hunter2</b>"));
            Assert.That(all.Rtf, Is.EqualTo(rtf));
            Assert.That(all.Secret, Is.False);
            Assert.That(otherApp.HoldsFiles(), Is.False);
        }
    }

    [Test]
    public void ConcurrentReadsAndWritesFromHydrasOwnThreads_AllSucceed()
    {
        var failures = 0;
        var threads = Enumerable.Range(0, 6).Select(t => new Thread(() =>
        {
            // half the readers on an instance of their own, as the slave's worker and the master's are
            using var own = t % 4 == 3 ? (WindowsClipboardSync)Create() : null;
            for (var i = 0; i < 40; i++)
            {
                if (t % 2 == 0)
                {
                    if (Write(Snapshot($"copy {t} {i}", null, Png)) is not ClipboardWrite.Written) Interlocked.Increment(ref failures);
                }
                else if ((own ?? Hydra.Clipboard).Read() is ClipboardRead.Unreadable) Interlocked.Increment(ref failures);
            }
        })).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.That(failures, Is.Zero);
    }

    // another app's copy, made by another process: its text, and a marker format holding a DWORD beside it
    private static void WriteForeignCopy(string text, string markerFormat, uint value) =>
        RunInOtherProcess("$d = New-Object System.Windows.Forms.DataObject; " +
                          $"$d.SetText('{text}'); " +
                          $"$d.SetData('{markerFormat}', (New-Object System.IO.MemoryStream(,[BitConverter]::GetBytes([uint32]{value})))); " +
                          "[System.Windows.Forms.Clipboard]::SetDataObject($d, $true)");

    // a clipboard script run by Windows Forms in another process, as another app's copy would be
    private static void RunInOtherProcess(string clipboardScript)
    {
        var script = "Add-Type -AssemblyName System.Windows.Forms; " + clipboardScript;
        using var process = Process.Start(new ProcessStartInfo("powershell", ["-NoProfile", "-STA", "-Command", script]) { RedirectStandardError = true })!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.Zero, error);
    }
}

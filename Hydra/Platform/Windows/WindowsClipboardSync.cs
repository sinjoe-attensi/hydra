using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

// no primary selection: Hydra keeps an EmulatedPrimarySelection beside it (ClipboardPlatform.Compose)
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardSync : IClipboardPlatform, ISelectionlessClipboard, IDisposable
{
    // registered once per process; Windows caches the value
    private static readonly uint CfPng = NativeMethods.RegisterClipboardFormat("PNG");
    private static readonly uint CfHtmlFormat = NativeMethods.RegisterClipboardFormat("HTML Format");
    private static readonly uint CfRtf = NativeMethods.RegisterClipboardFormat("Rich Text Format");
    // how password managers mark a copy no clipboard monitor or history should keep
    private static readonly uint CfExcludeFromMonitor = NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CfCanIncludeInHistory = NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint CfCanUploadToCloud = NativeMethods.RegisterClipboardFormat("CanUploadToCloudClipboard");

    // what a read takes as an image, in the order it tries them: the "PNG" registered format first (Chrome, Firefox,
    // etc.), then CF_DIB; and the formats whose text is no text copy of the user's
    private static readonly uint[] ImageFormats = [CfPng, NativeMethods.CF_DIB];
    private static readonly uint[] NotTextCopyFormats = [.. ImageFormats, NativeMethods.CF_HDROP];

    private const int OpenTries = 10;

    // Hydra's own threads take turns at the clipboard, so none spends another's retries waiting on it. Nothing under it
    // takes EmulatedPrimarySelection's gate, since that gate is held around ReadTextCopy, which takes this lock
    private static readonly Lock OpenLock = new();

    private readonly ILogger<WindowsClipboardSync> _log;
    private readonly ClipboardEchoFilter _echo;
    private readonly LastWrite _lastWrite;

    // dedicated owner window for the clipboard — passing NULL to OpenClipboard makes EmptyClipboard
    // set a NULL owner, which per MSDN causes SetClipboardData to fail. A real HWND fixes that.
    private readonly Win32Window? _owner;

    public WindowsClipboardSync(ILogger<WindowsClipboardSync> log)
    {
        _log = log;
        _owner = CreateOwnerWindow();
        _lastWrite = new LastWrite(Counter, log);
        _echo = new ClipboardEchoFilter(_lastWrite.Holds);
    }

    public IClipboard Clipboard => this;
    public IPrimarySelection? PrimarySelection => null;

    // message-only window created once on the constructing (main) thread. No message pump is needed:
    // OpenClipboard/SetClipboardData do not require one — only clipboard-listener notifications do.
    private Win32Window? CreateOwnerWindow()
    {
        try
        {
            return Win32Window.MessageOnly("HydraClipboardOwner");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to create clipboard owner window — falling back to NULL owner");
            return null;
        }
    }

    private nint OwnerWindow => _owner?.Handle ?? nint.Zero;

    public void Dispose() => _owner?.Dispose();

    // every format and the markers under one open of the clipboard, so no copy lands between them, echo-filtered and
    // converted after the clipboard is closed
    public ClipboardRead Read()
    {
        RawRead? raw;
        try
        {
            raw = WithClipboard(() => HoldsFiles()
                ? null
                : new RawRead(ReadOpenText(), ReadOpenImage(), ReadOpenHtml(), ReadOpenRtf(), IsOpenConcealed()));
        }
        catch (ClipboardUnreadableException e)
        {
            return e.AsRead();
        }
        if (raw == null) return new ClipboardRead.Files();
        var text = _echo.FilterText(raw.Text);
        var png = ToPng(raw.Image);
        var html = ToHtml(raw.Html);
        var rtf = _echo.FilterRtf(raw.Rtf);
        return new ClipboardRead.Contents(new ClipboardContents(text, html, rtf, png, raw.Concealed));
    }

    private static string? ReadOpenText()
    {
        var hMem = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
        if (hMem == nint.Zero) return null;

        var ptr = NativeMethods.GlobalLock(hMem);
        if (ptr == nint.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            NativeMethods.GlobalUnlock(hMem);
        }
    }

    // by clipboard format; one attempt, since it runs under EmulatedPrimarySelection's gate
    public SelectionRead ReadTextCopy()
    {
        try
        {
            return WithClipboard(() => NotTextCopyFormats.Any(NativeMethods.IsClipboardFormatAvailable) || IsOpenConcealed() ? null : ReadOpenText(),
                tries: 1) is { } text ? new SelectionRead.Text(text) : new SelectionRead.None();
        }
        catch (ClipboardUnreadableException e)
        {
            return e.AsSelection();
        }
    }

    // a password manager's markers, on a clipboard the caller opened
    private static bool IsOpenConcealed() =>
        NativeMethods.IsClipboardFormatAvailable(CfExcludeFromMonitor) || IsDenied(CfCanIncludeInHistory) || IsDenied(CfCanUploadToCloud);

    // a DWORD 0 the copy carries for format
    private static bool IsDenied(uint format)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(format)) return false;
        var allowed = ReadGlobalMemory(NativeMethods.GetClipboardData(format));
        return allowed is { Length: >= 4 } && BitConverter.ToUInt32(allowed) == 0;
    }

    public bool HoldsFiles() => NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP);

    // by the version Hydra's last write reported, whichever write that was
    public bool StillHolds() => _lastWrite.Holds();

    // the sequence number, which EmulatedPrimarySelection versions by too
    public long Counter() => NativeMethods.GetClipboardSequenceNumber();

    public ClipboardVersion? Version => new ClipboardVersion(Counter());

    // the first image format there is, as it lies on the clipboard, so converting it waits until the clipboard is closed
    private static ImageRead? ReadOpenImage()
    {
        foreach (var format in ImageFormats)
        {
            if (!NativeMethods.IsClipboardFormatAvailable(format)) continue;
            var data = ReadGlobalMemory(NativeMethods.GetClipboardData(format));
            if (data != null) return new ImageRead(data, format != CfPng);
        }
        return null;
    }

    // raw PNG bytes, else a device-independent bitmap converted to PNG; Hydra writes a DIB only beside its PNG, so a DIB
    // alone is never its echo
    private byte[]? ToPng(ImageRead? image) => image switch
    {
        null => null,
        { Dib: true } => DibToPng(image.Data),
        _ => _echo.FilterImage(image.Data),
    };

    private static byte[]? ReadOpenHtml() =>
        NativeMethods.IsClipboardFormatAvailable(CfHtmlFormat) ? ReadGlobalMemory(NativeMethods.GetClipboardData(CfHtmlFormat)) : null;

    // strip the Windows CF_HTML wrapper → portable HTML
    private string? ToHtml(byte[]? blob) => _echo.FilterHtml(blob != null ? CfHtml.Read(blob) : null);

    // RTF is null-terminated ASCII and GlobalSize may over-report; trim trailing padding/null bytes
    private static byte[]? ReadOpenRtf() =>
        NativeMethods.IsClipboardFormatAvailable(CfRtf) ? TrimTrailingNulls(ReadGlobalMemory(NativeMethods.GetClipboardData(CfRtf))) : null;

    // read under one open of the clipboard, Hydra's lock held throughout; read takes no lock of its own
    private T WithClipboard<T>(Func<T> read, int tries = OpenTries)
    {
        lock (OpenLock)
        {
            return OpenClipboard(tries)
                ? WhileOpen(read)
                : throw new ClipboardUnreadableException($"the clipboard is held by another app ({tries} tries)");
        }
    }

    // runs against a clipboard the caller opened under OpenLock, closing it after, then afterClose
    private static T WhileOpen<T>(Func<T> body, Action? afterClose = null)
    {
        try
        {
            return body();
        }
        finally
        {
            NativeMethods.CloseClipboard();
            afterClose?.Invoke();
        }
    }

    // a write made without EmulatedPrimarySelection, which no selection sees; a failure is logged, not thrown
    public ClipboardWrite Write(ClipboardContents contents)
    {
        try
        {
            return Write(contents, _ => { });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write clipboard");
            return new ClipboardWrite.NotReplaced(ex.Message);
        }
    }

    public ClipboardWrite Write(ClipboardContents asked, Action<long> leftAt) => Prepare(asked)(leftAt);

    // the DIB beside an image is converted before the clipboard is opened, and before the write begins
    public Func<Action<long>, ClipboardWrite> Prepare(ClipboardContents asked)
    {
        var contents = ClipboardUtils.Writing(asked);
        var dib = contents.Png is { } png ? PngToDibV5(png) : null;
        return leftAt => _lastWrite.Write(asked, left =>
        {
            lock (OpenLock)
            {
                if (!OpenClipboard(OpenTries))
                {
                    _log.LogWarning("Failed to open clipboard to write it after {Tries} tries", OpenTries);
                    return null;
                }
                return WhileOpen(() => WriteOpenClipboard(contents, dib), () =>
                {
                    // after closing, which moves the sequence number too; read before asking who owns the clipboard, so
                    // another app writing after either leaves it untaken, never taken as ours
                    var after = NativeMethods.GetClipboardSequenceNumber();
                    if (NativeMethods.GetClipboardOwner() == OwnerWindow) left(after);
                });
            }
        }, leftAt, () => Counter());
    }

    // what it wrote, null when it could not empty the clipboard
    private ClipboardContents? WriteOpenClipboard(ClipboardContents contents, byte[]? dib)
    {
        if (!NativeMethods.EmptyClipboard()) return null;
        contents = ClipboardUtils.WriteMarkers(_log, contents, WriteConcealedMarkers, WriteNoHistoryMarkers);
        _echo.Track(contents);

        // image first (legacy image pasters pick the first supported format); rich before plain text
        // so rich-aware apps prefer HTML/RTF over CF_UNICODETEXT.
        return ClipboardUtils.WriteAll(_log, contents,
            contents.Png is { } png ? new FormatWrite(ClipboardFormat.Image, () => WriteImageToOpenClipboard(png, dib)) : null,
            contents.Html is { } html ? new FormatWrite(ClipboardFormat.Html, () => WriteGlobalMemory(CfHtmlFormat, NullTerminated(CfHtml.Wrap(html)))) : null,
            contents.Rtf is { } rtf ? new FormatWrite(ClipboardFormat.Rtf, () => WriteGlobalMemory(CfRtf, NullTerminated(rtf))) : null,
            contents.Text is { } text
                ? new FormatWrite(ClipboardFormat.Text, () => WriteGlobalMemory(NativeMethods.CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + '\0')))
                : null);
    }

    private static byte[] NullTerminated(byte[] data) => [.. data, 0]; // text-based clipboard formats expect a NUL

    // every marker, even after one is refused; the clipboard refuses an empty block, and only the exclusion's presence counts
    private static bool WriteConcealedMarkers()
    {
        var excluded = WriteGlobalMemory(CfExcludeFromMonitor, BitConverter.GetBytes(0u));
        return WriteNoHistoryMarkers() && excluded;
    }

    // both, even after one is refused
    private static bool WriteNoHistoryMarkers()
    {
        byte[] denied = BitConverter.GetBytes(0u);
        var noHistory = WriteGlobalMemory(CfCanIncludeInHistory, denied);
        var noCloud = WriteGlobalMemory(CfCanUploadToCloud, denied);
        return noHistory && noCloud;
    }

    private static byte[]? TrimTrailingNulls(byte[]? data)
    {
        if (data == null) return null;
        var len = data.Length;
        while (len > 0 && data[len - 1] == 0) len--;
        return len == data.Length ? data : data[..len];
    }

    private static bool WriteImageToOpenClipboard(byte[] pngData, byte[]? dib)
    {
        // write as "PNG" registered format (raw bytes — modern apps prefer this, full fidelity + alpha)
        if (!WriteGlobalMemory(CfPng, pngData)) return false;

        // also write CF_DIBV5 (32bpp, preserves alpha). Windows auto-synthesizes CF_DIB and CF_BITMAP from
        // it for legacy apps (Paint/Office), so a single write covers both alpha-aware and legacy pasters.
        // only beside the PNG: a DIB alone would read back as a copy of the user's, past the echo filter
        if (dib != null)
            WriteGlobalMemory(NativeMethods.CF_DIBV5, dib);
        return true;
    }

    private static byte[]? ReadGlobalMemory(nint hMem)
    {
        if (hMem == nint.Zero) return null;
        var size = (int)NativeMethods.GlobalSize(hMem);
        if (size <= 0) return null;

        var ptr = NativeMethods.GlobalLock(hMem);
        if (ptr == nint.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        finally
        {
            NativeMethods.GlobalUnlock(hMem);
        }
    }

    // whether the clipboard took it
    private static bool WriteGlobalMemory(uint format, byte[] data)
    {
        var hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_DDESHARE, (nuint)data.Length);
        if (hMem == nint.Zero) return false;
        if (!CopyInto(hMem, data))
        {
            NativeMethods.GlobalFree(hMem);
            return false;
        }

        if (NativeMethods.SetClipboardData(format, hMem) != nint.Zero) return true;
        NativeMethods.GlobalFree(hMem);
        return false;
    }

    private static bool CopyInto(nint hMem, byte[] data)
    {
        var ptr = NativeMethods.GlobalLock(hMem);
        if (ptr == nint.Zero) return false;
        Marshal.Copy(data, 0, ptr, data.Length);
        NativeMethods.GlobalUnlock(hMem);
        return true;
    }

    // converts a PNG to a CF_DIBV5 blob (BITMAPV5HEADER + 32bpp BGRA, bottom-up) preserving alpha.
    private byte[]? PngToDibV5(byte[] pngData)
    {
        try
        {
            using var ms = new MemoryStream(pngData);
            using var bitmap = new Bitmap(ms);

            var width = bitmap.Width;
            var height = bitmap.Height;
            const int headerSize = 124; // BITMAPV5HEADER
            var stride = width * 4;     // 32bpp rows are inherently 4-byte aligned
            var imageSize = stride * height;
            var dib = new byte[headerSize + imageSize];

            BitConverter.GetBytes(headerSize).CopyTo(dib, 0);                  // bV5Size
            BitConverter.GetBytes(width).CopyTo(dib, 4);                       // bV5Width
            BitConverter.GetBytes(height).CopyTo(dib, 8);                      // bV5Height (positive = bottom-up)
            BitConverter.GetBytes((short)1).CopyTo(dib, 12);                   // bV5Planes
            BitConverter.GetBytes((short)32).CopyTo(dib, 14);                  // bV5BitCount
            BitConverter.GetBytes(3).CopyTo(dib, 16);                         // bV5Compression = BI_BITFIELDS
            BitConverter.GetBytes(imageSize).CopyTo(dib, 20);                  // bV5SizeImage
            BitConverter.GetBytes(0x00FF0000).CopyTo(dib, 40);                // bV5RedMask
            BitConverter.GetBytes(0x0000FF00).CopyTo(dib, 44);                // bV5GreenMask
            BitConverter.GetBytes(0x000000FF).CopyTo(dib, 48);                // bV5BlueMask
            BitConverter.GetBytes(unchecked((int)0xFF000000)).CopyTo(dib, 52); // bV5AlphaMask
            BitConverter.GetBytes(0x73524742).CopyTo(dib, 56);                // bV5CSType = LCS_sRGB

            var rect = new Rectangle(0, 0, width, height);
            var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                // Format32bppArgb is B,G,R,A in memory — matches the BGRA masks above.
                // LockBits gives top-down rows; DIB needs bottom-up — copy in reverse.
                for (var y = 0; y < height; y++)
                {
                    var srcOffset = y * bmpData.Stride;
                    var dstOffset = headerSize + (height - 1 - y) * stride;
                    Marshal.Copy(bmpData.Scan0 + srcOffset, dib, dstOffset, stride);
                }
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }

            return dib;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PNG to DIBv5 conversion failed");
            return null;
        }
    }

    private byte[]? DibToPng(byte[] dib)
    {
        try
        {
            if (dib.Length < 4) return null;
            // read biSize from the DIB info header (supports extended headers like BITMAPV4/V5)
            var biSize = BitConverter.ToInt32(dib, 0);
            if (biSize < 40 || dib.Length < biSize) return null;

            // compute colour table size for indexed-colour bitmaps
            var colourTableSize = 0;
            var biBitCount = BitConverter.ToUInt16(dib, 14); // offset 14 in BITMAPINFOHEADER
            var biClrUsed = BitConverter.ToInt32(dib, 32);
            if (biBitCount <= 8)
                colourTableSize = (biClrUsed > 0 ? biClrUsed : (1 << biBitCount)) * 4;
            else if (biClrUsed > 0)
                colourTableSize = biClrUsed * 4;

            // a 40-byte BITMAPINFOHEADER with BI_BITFIELDS is followed by 3 (or 4 with alpha) DWORD colour
            // masks that sit BEFORE the pixel data — screenshots (Snipping Tool/PrtScn) arrive this way.
            // (V4/V5 headers carry the masks inside the header, so biSize>40 needs no extra.)
            const int biBitfields = 3, biAlphaBitfields = 6;
            var biCompression = BitConverter.ToInt32(dib, 16);
            var maskSize = biSize == 40 && biCompression == biBitfields ? 12
                : biSize == 40 && biCompression == biAlphaBitfields ? 16
                : 0;

            // prepend 14-byte BMP file header to make a complete BMP file
            var bmpHeader = new byte[14];
            bmpHeader[0] = (byte)'B';
            bmpHeader[1] = (byte)'M';
            var totalSize = dib.Length + 14;
            bmpHeader[2] = (byte)totalSize;
            bmpHeader[3] = (byte)(totalSize >> 8);
            bmpHeader[4] = (byte)(totalSize >> 16);
            bmpHeader[5] = (byte)(totalSize >> 24);
            // pixel data offset: file header + info header + colour table (or BITFIELDS masks)
            var pixelOffset = 14 + biSize + colourTableSize + maskSize;
            bmpHeader[10] = (byte)pixelOffset;
            bmpHeader[11] = (byte)(pixelOffset >> 8);
            bmpHeader[12] = (byte)(pixelOffset >> 16);
            bmpHeader[13] = (byte)(pixelOffset >> 24);

            var bmpBytes = new byte[totalSize];
            bmpHeader.CopyTo(bmpBytes, 0);
            dib.CopyTo(bmpBytes, 14);

            using var ms = new MemoryStream(bmpBytes);
            using var bitmap = new Bitmap(ms);
            using var pngMs = new MemoryStream();
            bitmap.Save(pngMs, ImageFormat.Png);
            return pngMs.ToArray();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DIB to PNG conversion failed");
            return null;
        }
    }

    // under OpenLock
    private bool OpenClipboard(int tries)
    {
        // clipboard is a global mutex; holders such as clipboard history let go quickly, so retry, backing off from 2 ms
        // to 18, about 100 ms in all; the caller says what failed
        for (var i = 0; i < tries; i++)
        {
            if (NativeMethods.OpenClipboard(OwnerWindow)) return true;
            if (i < tries - 1) Thread.Sleep(2 * (i + 1));
        }
        return false;
    }

    // the formats one open of the clipboard read, before echo filtering and conversion
    private sealed record RawRead(string? Text, ImageRead? Image, byte[]? Html, byte[]? Rtf, bool Concealed);

    // an image's bytes as the clipboard holds them: PNG, or a DIB still to convert
    private sealed record ImageRead(byte[] Data, bool Dib);
}

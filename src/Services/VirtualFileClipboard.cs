using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using Path = System.IO.Path;

namespace Tfx;

/// <summary>
/// One entry of a FileGroupDescriptorW listing. RelativePath uses '\'
/// separators and has been validated to stay inside the paste destination
/// (not rooted, no "..", no invalid name characters).
/// </summary>
internal sealed record VirtualFileEntry(string RelativePath, bool IsDirectory, int Index, long? Size);

/// <summary>
/// Reads "virtual files" from the OLE clipboard: FileGroupDescriptorW (the
/// file listing) plus FileContents (one stream per file). Sources that cannot
/// put real paths on the clipboard publish these instead of CF_HDROP — most
/// importantly the RDP clipboard (rdpclip), but also Outlook attachments and
/// Explorer's zip folders. The bytes only exist behind the source's
/// IDataObject, so they must be pulled out as streams. WPF's Clipboard class
/// cannot address the per-file FileContents streams (each needs its own
/// FORMATETC.lindex), hence the raw COM interop here.
/// </summary>
internal sealed class VirtualFileClipboard : IDisposable
{
    private const int S_OK = 0;
    private const uint FD_ATTRIBUTES = 0x00000004;
    private const uint FD_FILESIZE = 0x00000040;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    private static readonly uint FileGroupDescriptorWFormat = RegisterClipboardFormatW("FileGroupDescriptorW");
    private static readonly uint FileContentsFormat = RegisterClipboardFormatW("FileContents");

    private readonly ComIDataObject _dataObject;

    public IReadOnlyList<VirtualFileEntry> Entries { get; }

    private VirtualFileClipboard(ComIDataObject dataObject, IReadOnlyList<VirtualFileEntry> entries)
    {
        _dataObject = dataObject;
        Entries = entries;
    }

    /// <summary>
    /// Cheap availability probe (for menu enablement); does not open the
    /// clipboard or touch the data object.
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            return IsClipboardFormatAvailable(FileGroupDescriptorWFormat)
                && IsClipboardFormatAvailable(FileContentsFormat);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Grabs the clipboard's data object and parses the descriptor listing.
    /// Returns null when the clipboard holds no (readable) virtual files.
    /// Dispose the instance to release the data object.
    /// </summary>
    public static VirtualFileClipboard? TryOpen()
    {
        ComIDataObject? dataObject = null;
        try
        {
            if (OleGetClipboard(out dataObject) != S_OK || dataObject is null)
            {
                return null;
            }

            var entries = ReadDescriptors(dataObject);
            if (entries is null || entries.Count == 0)
            {
                Release(ref dataObject);
                return null;
            }

            var result = new VirtualFileClipboard(dataObject, entries);
            dataObject = null; // ownership moved to the instance
            return result;
        }
        catch
        {
            Release(ref dataObject);
            return null;
        }
    }

    /// <summary>
    /// Writes one file entry's contents to <paramref name="targetPath"/>,
    /// overwriting an existing file. Throws on failure (bad medium, source
    /// stream errors), matching the per-item error handling of the paste loop.
    /// </summary>
    public void ExtractToFile(VirtualFileEntry entry, string targetPath)
    {
        var format = MakeFormat(FileContentsFormat, entry.Index, TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL);
        _dataObject.GetData(ref format, out var medium);
        try
        {
            switch (medium.tymed)
            {
                case TYMED.TYMED_ISTREAM:
                {
                    var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                    try
                    {
                        CopyStreamToFile(stream, targetPath);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(stream);
                    }
                    break;
                }
                case TYMED.TYMED_HGLOBAL:
                    WriteHGlobalToFile(medium.unionmember, entry.Size, targetPath);
                    break;
                default:
                    throw new IOException($"Unsupported clipboard medium: {medium.tymed}");
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    public void Dispose()
    {
        if (Marshal.IsComObject(_dataObject))
        {
            Marshal.ReleaseComObject(_dataObject);
        }
    }

    private static List<VirtualFileEntry>? ReadDescriptors(ComIDataObject dataObject)
    {
        var format = MakeFormat(FileGroupDescriptorWFormat, -1, TYMED.TYMED_HGLOBAL);
        if (dataObject.QueryGetData(ref format) != S_OK)
        {
            return null;
        }

        dataObject.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero)
            {
                return null;
            }

            var block = GlobalLock(medium.unionmember);
            if (block == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var count = Marshal.ReadInt32(block);
                var entrySize = Marshal.SizeOf<FILEDESCRIPTORW>();
                var available = (long)GlobalSize(medium.unionmember);
                // Reject a count that doesn't fit the allocation: a truncated
                // block would otherwise read past the end.
                if (count <= 0 || 4 + (long)count * entrySize > available)
                {
                    return null;
                }

                var entries = new List<VirtualFileEntry>(count);
                for (var i = 0; i < count; i++)
                {
                    var descriptor = Marshal.PtrToStructure<FILEDESCRIPTORW>(block + 4 + i * entrySize);
                    var relativePath = NormalizeRelativePath(descriptor.cFileName);
                    if (relativePath is null)
                    {
                        // Hostile or malformed name (rooted, "..", invalid
                        // characters): refuse the whole listing rather than
                        // paste a subset of it.
                        return null;
                    }

                    var isDirectory = (descriptor.dwFlags & FD_ATTRIBUTES) != 0
                        && (descriptor.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                    long? size = (descriptor.dwFlags & FD_FILESIZE) != 0
                        ? ((long)descriptor.nFileSizeHigh << 32) | descriptor.nFileSizeLow
                        : null;
                    entries.Add(new VirtualFileEntry(relativePath, isDirectory, i, size));
                }

                return entries;
            }
            finally
            {
                GlobalUnlock(medium.unionmember);
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static string? NormalizeRelativePath(string rawName)
    {
        var raw = (rawName ?? "").Replace('/', '\\').Trim('\\');
        if (raw.Length == 0 || Path.IsPathRooted(raw))
        {
            return null;
        }

        var segments = raw.Split('\\');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return null;
            }
        }

        return string.Join('\\', segments);
    }

    private static void CopyStreamToFile(IStream stream, string targetPath)
    {
        using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
        var buffer = new byte[81920];
        var bytesReadPtr = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            while (true)
            {
                Marshal.WriteInt64(bytesReadPtr, 0);
                stream.Read(buffer, buffer.Length, bytesReadPtr);
                var read = Marshal.ReadInt32(bytesReadPtr);
                if (read <= 0)
                {
                    break;
                }
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(bytesReadPtr);
        }
    }

    private static void WriteHGlobalToFile(IntPtr hGlobal, long? declaredSize, string targetPath)
    {
        var source = GlobalLock(hGlobal);
        if (source == IntPtr.Zero)
        {
            throw new IOException("GlobalLock failed for clipboard file contents");
        }

        try
        {
            var size = (long)GlobalSize(hGlobal);
            // HGLOBAL allocations can be rounded up; the descriptor carries
            // the exact size when the source provides one.
            if (declaredSize is { } declared && declared < size)
            {
                size = declared;
            }

            using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
            var buffer = new byte[81920];
            long written = 0;
            while (written < size)
            {
                var chunk = (int)Math.Min(buffer.Length, size - written);
                Marshal.Copy(IntPtr.Add(source, (int)written), buffer, 0, chunk);
                output.Write(buffer, 0, chunk);
                written += chunk;
            }
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }
    }

    private static FORMATETC MakeFormat(uint clipFormat, int lindex, TYMED tymed) => new()
    {
        cfFormat = unchecked((short)clipFormat),
        ptd = IntPtr.Zero,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = lindex,
        tymed = tymed,
    };

    private static void Release(ref ComIDataObject? dataObject)
    {
        if (dataObject is not null && Marshal.IsComObject(dataObject))
        {
            Marshal.ReleaseComObject(dataObject);
        }
        dataObject = null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FILEDESCRIPTORW
    {
        public uint dwFlags;
        public Guid clsid;
        public int sizelCx;
        public int sizelCy;
        public int pointlX;
        public int pointlY;
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int OleGetClipboard([MarshalAs(UnmanagedType.Interface)] out ComIDataObject? dataObject);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr hMem);
}

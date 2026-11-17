using System.Runtime.InteropServices;

namespace PartCombiner.Core;

/// <summary>Moves files to the Windows Recycle Bin (so "delete parts afterwards" stays undoable).</summary>
public static partial class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    // Asks before permanently deleting a file that is too big for the Recycle Bin.
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>Returns true when every file was moved to the Recycle Bin.</summary>
    public static bool Send(IEnumerable<string> paths)
    {
        var existing = paths.Where(File.Exists).Select(Path.GetFullPath).ToList();
        if (existing.Count == 0) return true;

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // Double-null-terminated list; the marshaller adds the final terminator.
            pFrom = string.Join('\0', existing) + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        int result = SHFileOperation(ref op);
        return result == 0 && !op.fAnyOperationsAborted && existing.All(p => !File.Exists(p));
    }
}

using System.Runtime.InteropServices;
using Kakitome.Application.Recording;

namespace Kakitome.Infrastructure.FileSystem;

/// <summary>
/// Sends a folder to the Recycle Bin with SHFileOperation(FOF_ALLOWUNDO). Only local fixed drives are accepted
/// (other volumes may delete permanently); anything else returns false and the caller keeps the data.
/// </summary>
public sealed partial class WindowsRecycleBin : IRecycleBin
{
    private const uint FoDelete = 0x3;
    private const ushort FofSilent = 0x4;
    private const ushort FofNoConfirmation = 0x10;
    private const ushort FofAllowUndo = 0x40;
    private const ushort FofNoErrorUi = 0x400;
    private const ushort FofWantNukeWarning = 0x4000;

    public bool TryMoveToRecycleBin(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        var full = Path.GetFullPath(folderPath);
        if (!Directory.Exists(full) || full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var root = Path.GetPathRoot(full);
        if (root is null || new DriveInfo(root).DriveType != DriveType.Fixed)
        {
            return false;
        }

        var from = Marshal.StringToHGlobalUni(full + "\0\0");
        try
        {
            var op = new ShFileOpStruct
            {
                Func = FoDelete,
                From = from,
                // FOF_WANTNUKEWARNING: if Windows would delete permanently (e.g. Recycle Bin disabled), it asks first.
                Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning),
            };
            var result = SHFileOperationW(ref op);
            return result == 0 && op.AnyOperationsAborted == 0 && !Directory.Exists(full);
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHFileOperationW(ref ShFileOpStruct fileOp);
}

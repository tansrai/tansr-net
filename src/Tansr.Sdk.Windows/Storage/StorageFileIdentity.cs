using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Windows.Storage;

/// <summary>持有不共享 DELETE 的 Windows 文件句柄；拒绝重解析点及硬链接，防止打开后被换盘面。</summary>
internal sealed class StorageFileIdentity : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private readonly bool _directory;
    private readonly uint _volume;
    private readonly ulong _index;

    private StorageFileIdentity(string path, bool directory, SafeFileHandle handle)
    {
        _path = path;
        _directory = directory;
        _handle = handle;
        var info = Read(handle, directory);
        _volume = info.VolumeSerialNumber;
        _index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        CheckResolved(handle);
    }

    internal string Device => _volume.ToString(CultureInfo.InvariantCulture);
    internal string Inode => _index.ToString(CultureInfo.InvariantCulture);

    internal static string FullPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length < 3 || !char.IsLetter(input[0]) || input[1] != ':' || (input[2] != '\\' && input[2] != '/') || input.IndexOf('\0') >= 0 ||
            input.StartsWith(@"\\", StringComparison.Ordinal)) throw new StorageException("invalid_input");
        var path = Path.GetFullPath(input);
        if (path.Length < 4 || path[1] != ':' || path.IndexOf(':', 2) >= 0) throw new StorageException("invalid_input");
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static StorageFileIdentity Open(string path, bool directory, bool create = false, bool metadataOnly = false)
    {
        var flags = 0x00200000u | (directory ? 0x02000000u : 0u); // OPEN_REPARSE_POINT / BACKUP_SEMANTICS
        var handle = CreateFile(path, directory || metadataOnly ? 0x80u : 0xC0000000u, 3, IntPtr.Zero, create ? 1u : 3u, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new StorageException("storage_error");
        }
        try { return new StorageFileIdentity(path, directory, handle); }
        catch { handle.Dispose(); throw; }
    }

    internal void Check()
    {
        var original = Read(_handle, _directory);
        if (original.VolumeSerialNumber != _volume || (((ulong)original.FileIndexHigh << 32) | original.FileIndexLow) != _index)
            throw new StorageException("identity_mismatch");
        using var current = Open(_path, _directory, metadataOnly: true);
        if (current._volume != _volume || current._index != _index) throw new StorageException("identity_mismatch");
        CheckResolved(_handle);
    }

    private void CheckResolved(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new StorageException("identity_mismatch");
        var value = buffer.ToString();
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal)) value = value.Substring(4);
        if (!string.Equals(value.TrimEnd('\\'), _path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new StorageException("identity_mismatch");
    }

    private static FileInformation Read(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var value) || (value.FileAttributes & 0x400) != 0 ||
            ((value.FileAttributes & 0x10) != 0) != directory || (!directory && value.NumberOfLinks != 1))
            throw new StorageException("identity_mismatch");
        return value;
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}

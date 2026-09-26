using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Tansr.Sdk.Windows.Execution;

/// <summary>每次只解析一个路径片段，并以已持有的父目录句柄为根。</summary>
internal static class NativeWorkspace
{
    private const uint ReadAttributes = 0x80, Synchronize = 0x100000, Delete = 0x10000;
    private const uint OpenReparsePoint = 0x200000, Synchronous = 0x20;
    internal const uint DirectoryAttribute = 0x10, ReparseAttribute = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    { public uint Length; public IntPtr RootDirectory, ObjectName; public uint Attributes; public IntPtr SecurityDescriptor, SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus { public IntPtr Status, Information; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        public long Length => checked(((long)FileSizeHigh << 32) | FileSizeLow);
        public DateTime WriteTime => DateTime.FromFileTimeUtc(((long)(uint)LastWriteTime.dwHighDateTime << 32) | (uint)LastWriteTime.dwLowDateTime);
    }
    internal sealed class DirectoryItem
    {
        internal DirectoryItem(string name, uint attributes, long length, DateTime writeTime)
        { Name = name; Attributes = attributes; Length = length; WriteTime = writeTime; }
        internal string Name { get; }
        internal uint Attributes { get; }
        internal long Length { get; }
        internal DateTime WriteTime { get; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatus status,
        IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(SafeFileHandle handle, IntPtr eventHandle, IntPtr apc, IntPtr context, out IoStatus status,
        IntPtr buffer, uint length, int informationClass, [MarshalAs(UnmanagedType.U1)] bool singleEntry, IntPtr fileName, [MarshalAs(UnmanagedType.U1)] bool restart);
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatus status, IntPtr buffer, uint length, int informationClass);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    internal static SafeFileHandle OpenDrive(string drive, bool denyWrite = false)
    {
        var handle = CreateFileW(@"\\?\" + drive, 1 | ReadAttributes | Synchronize | 0x20000, denyWrite ? 1u : 3u, IntPtr.Zero, 3, 0x02000000 | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); ThrowWin32(error); }
        try { Validate(handle, true); return handle; }
        catch { handle.Dispose(); throw; }
    }

    internal static SafeFileHandle OpenDirectory(SafeFileHandle parent, string name, bool create = false, bool denyWrite = false)
        => Open(parent, name, true, create ? 2u : 1u, false, false, denyWrite);

    internal static SafeFileHandle OpenFile(SafeFileHandle parent, string name, bool write = false, bool create = false, bool allowRename = false)
        => Open(parent, name, false, create ? 2u : 1u, write, allowRename);

    internal static SafeFileHandle OpenMetadata(SafeFileHandle parent, string name)
        => Open(parent, name, false, 1, false, false, false, true);

    private static SafeFileHandle Open(SafeFileHandle parent, string name, bool directory, uint disposition, bool write, bool allowRename, bool denyDirectoryWrite = false, bool metadataOnly = false)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicode = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = nameBuffer };
        var unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(unicode, unicodeBuffer, false);
            var attributes = new ObjectAttributes { Length = (uint)Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = unicodeBuffer, Attributes = 0x40 | 0x1000 };
            // 目录不共享 DELETE，固定已批准路径。对象管理器及文件系统两层禁止重解析。
            uint access = (metadataOnly ? 0u : 1u) | ReadAttributes | Synchronize | 0x20000 | (write ? 2u | Delete : 0u);
            uint share = metadataOnly ? 7u : directory ? (denyDirectoryWrite ? 1u : 3u) : 1u | (allowRename ? 4u : 0u);
            int status = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero, directory ? DirectoryAttribute : 0x80,
                share, disposition, Synchronous | OpenReparsePoint | (metadataOnly ? 0u : directory ? 1u : 0x40u), IntPtr.Zero, 0);
            if (status < 0) { handle?.Dispose(); ThrowStatus(status); }
            try { if (!metadataOnly) Validate(handle!, directory); return handle!; }
            catch { handle!.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(unicodeBuffer); Marshal.FreeHGlobal(nameBuffer); }
    }

    internal static FileInformation Validate(SafeFileHandle handle, bool directory)
    {
        var info = Information(handle);
        if ((info.Attributes & ReparseAttribute) != 0 || ((info.Attributes & DirectoryAttribute) != 0) != directory)
            throw new WindowsWorkspaceException("unsafe_path");
        if (!directory && info.NumberOfLinks != 1) throw new WindowsWorkspaceException("hard_link_rejected");
        return info;
    }

    internal static FileInformation Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) ThrowWin32(Marshal.GetLastWin32Error());
        return info;
    }

    /// <summary>协作写者域须保持用户私有 ACL；不会擅自修改开发者目录权限。</summary>
    internal static void ValidatePrivatePermissions(SafeFileHandle handle)
    {
        uint status = GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out var descriptor);
        if (status != 0) ThrowWin32(unchecked((int)status));
        try
        {
            uint length = GetSecurityDescriptorLength(descriptor);
            if (length == 0 || length > 65536) throw new WindowsWorkspaceException("private_permissions_required");
            byte[] bytes = new byte[(int)length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            using var current = WindowsIdentity.GetCurrent();
            string? currentSid = current.User?.Value;
            bool Allowed(SecurityIdentifier? sid) => sid != null && (sid.Value == currentSid || sid.Value == "S-1-5-18" || sid.Value == "S-1-5-32-544");
            if (currentSid == null || !Allowed(security.Owner) || security.DiscretionaryAcl == null)
                throw new WindowsWorkspaceException("private_permissions_required");
            foreach (GenericAce entry in security.DiscretionaryAcl)
            {
                if (entry is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed || !Allowed(ace.SecurityIdentifier))
                    throw new WindowsWorkspaceException("private_permissions_required");
            }
        }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }

    internal static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new WindowsWorkspaceException("path_identity_unavailable");
        return buffer.ToString();
    }

    internal static IReadOnlyList<DirectoryItem> Enumerate(SafeFileHandle directory, int maximum)
    {
        Validate(directory, true);
        var results = new List<DirectoryItem>();
        var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            bool restart = true;
            while (true)
            {
                int status = NtQueryDirectoryFile(directory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var io, buffer, 65536, 1, false, IntPtr.Zero, restart);
                restart = false;
                if (status == unchecked((int)0x80000006)) break;
                if (status < 0 && status != unchecked((int)0x80000005)) ThrowStatus(status);
                long available = io.Information.ToInt64();
                if (available <= 0 || available > 65536) throw new WindowsWorkspaceException("invalid_directory_record");
                int offset = 0;
                while (true)
                {
                    if (offset < 0 || offset + 64 > available) throw new WindowsWorkspaceException("invalid_directory_record");
                    IntPtr record = IntPtr.Add(buffer, offset);
                    int next = Marshal.ReadInt32(record), nameBytes = Marshal.ReadInt32(record, 60);
                    if (nameBytes < 0 || (nameBytes & 1) != 0 || offset + 64L + nameBytes > available) throw new WindowsWorkspaceException("invalid_directory_record");
                    string name = Marshal.PtrToStringUni(IntPtr.Add(record, 64), nameBytes / 2)!;
                    if (name != "." && name != "..")
                    {
                        if (results.Count == maximum) throw new WindowsWorkspaceException("entry_limit");
                        results.Add(new DirectoryItem(name, unchecked((uint)Marshal.ReadInt32(record, 56)), Marshal.ReadInt64(record, 40), DateTime.FromFileTimeUtc(Marshal.ReadInt64(record, 24))));
                    }
                    if (next == 0) break;
                    if (next < 64 || offset + (long)next >= available) throw new WindowsWorkspaceException("invalid_directory_record");
                    offset += next;
                }
            }
            return results;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void Flush(SafeFileHandle handle)
    { if (!FlushFileBuffers(handle)) ThrowWin32(Marshal.GetLastWin32Error()); }

    internal static void Rename(SafeFileHandle file, SafeFileHandle parent, string name, bool replace)
    {
        int rootOffset = IntPtr.Size == 8 ? 8 : 4, lengthOffset = rootOffset + IntPtr.Size, nameOffset = lengthOffset + 4;
        byte[] text = Encoding.Unicode.GetBytes(name);
        int size = checked(nameOffset + text.Length);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteByte(buffer, replace ? (byte)1 : (byte)0);
            Marshal.WriteIntPtr(buffer, rootOffset, parent.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, text.Length);
            Marshal.Copy(text, 0, IntPtr.Add(buffer, nameOffset), text.Length);
            int status = NtSetInformationFile(file, out _, buffer, (uint)size, 10);
            if (status < 0) ThrowStatus(status);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void DeleteOpenedFile(SafeFileHandle file)
    {
        IntPtr buffer = Marshal.AllocHGlobal(1);
        try { Marshal.WriteByte(buffer, 1); int status = NtSetInformationFile(file, out _, buffer, 1, 13); if (status < 0) ThrowStatus(status); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void ThrowStatus(int status) => ThrowWin32(unchecked((int)RtlNtStatusToDosError(status)));
    private static void ThrowWin32(int error)
    {
        switch (error)
        {
            case 2: case 3: throw new WindowsWorkspaceException("not_found");
            case 80: case 183: throw new WindowsWorkspaceException("already_exists");
            case 5: throw new WindowsWorkspaceException("access_denied");
            case 32: case 33: throw new WindowsWorkspaceException("sharing_conflict");
            case 4390: case 4392: case 4393: throw new WindowsWorkspaceException("unsafe_path");
            default: throw new WindowsWorkspaceException("native_error_" + error.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

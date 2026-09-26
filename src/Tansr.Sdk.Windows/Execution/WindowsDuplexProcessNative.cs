using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Tansr.Sdk.Windows.Execution;

/// <summary>复用已有 Win32 ABI、参数转义及逐级目录句柄；仅新增持久 stdin 所需的装配。</summary>
internal sealed class WindowsDuplexProcessNative : IDisposable
{
    private readonly List<SafeFileHandle> pins = new();
    internal NativeProcessHandle? Process { get; private set; }
    internal NativeProcessHandle? Job { get; private set; }
    internal SafeFileHandle? Input { get; private set; }
    internal SafeFileHandle? Output { get; private set; }
    internal SafeFileHandle? Error { get; private set; }
    internal int ProcessId { get; private set; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int count, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeHandle sourceHandle, IntPtr targetProcess,
        out IntPtr targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    internal NativeProcessHandle DuplicateProcessHandle()
    {
        if (Process == null || Process.IsClosed || Process.IsInvalid) throw new WindowsDuplexProcessException("closed");
        try
        {
            // SafeHandle 封送在整个 DuplicateHandle 调用期间持有源引用，不按可复用 PID 重新打开。
            if (!DuplicateHandle(new IntPtr(-1), Process, new IntPtr(-1), out var raw, 0, false, 2))
                throw new WindowsDuplexProcessException("process_identity_unavailable");
            var duplicate = new NativeProcessHandle(raw);
            if (NativeProcessMethods.WaitForSingleObject(duplicate, 0) != NativeProcessMethods.WaitTimeout)
            {
                duplicate.Dispose();
                throw new WindowsDuplexProcessException("closed");
            }

            return duplicate;
        }
        catch (ObjectDisposedException) { throw new WindowsDuplexProcessException("closed"); }
    }

    internal static WindowsDuplexProcessNative Start(string executable, IReadOnlyList<string> arguments, string directory,
        IDictionary<string, string> environment, string? expectedExecutableSha256, CancellationToken cancellation, Action validateWorkingDirectory)
    {
        var native = new WindowsDuplexProcessNative();
        try
        {
            native.PinExecutable(executable, expectedExecutableSha256, cancellation);
            native.Create(executable, arguments, directory, environment, cancellation, validateWorkingDirectory);
            return native;
        }
        catch
        {
            if (native.Process != null)
            {
                NativeProcessMethods.TerminateProcess(native.Process, 0xC000013A);
                NativeProcessMethods.WaitForSingleObject(native.Process, 5000);
            }

            native.Dispose();
            throw;
        }
    }

    private void Create(string executable, IReadOnlyList<string> arguments, string directory, IDictionary<string, string> environment,
        CancellationToken cancellation, Action validateWorkingDirectory)
    {
        Job = new NativeProcessHandle(NativeProcessMethods.CreateJobObjectW(IntPtr.Zero, null));
        if (Job.IsInvalid) throw Failure();
        var limits = new NativeProcessMethods.ExtendedLimitInformation();
        limits.Basic.Flags = NativeProcessMethods.KillOnJobClose;
        if (!NativeProcessMethods.SetInformationJobObject(Job, 9, ref limits, Marshal.SizeOf<NativeProcessMethods.ExtendedLimitInformation>())) throw Failure();

        using var childInput = Pipe(false, out var input);
        Input = input;
        using var childOutput = Pipe(true, out var output);
        Output = output;
        using var childError = Pipe(true, out var error);
        Error = error;
        var attributes = IntPtr.Zero;
        var handles = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        var environmentLength = 0;
        var initialized = false;
        try
        {
            var size = IntPtr.Zero;
            NativeProcessMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero) throw Failure();
            attributes = Marshal.AllocHGlobal(size);
            if (!NativeProcessMethods.InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw Failure();
            initialized = true;
            handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handles, 0, childInput.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, childOutput.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size * 2, childError.DangerousGetHandle());
            if (!NativeProcessMethods.UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), handles,
                new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero)) throw Failure();
            var startup = new NativeProcessMethods.StartupInfoEx
            {
                Attributes = attributes,
                Startup = new NativeProcessMethods.StartupInfo
                {
                    Size = Marshal.SizeOf<NativeProcessMethods.StartupInfoEx>(),
                    Flags = 0x101,
                    StandardInput = childInput.DangerousGetHandle(),
                    StandardOutput = childOutput.DangerousGetHandle(),
                    StandardError = childError.DangerousGetHandle(),
                },
            };
            var environmentText = EnvironmentBlock(environment);
            environmentLength = environmentText.Length;
            environmentBlock = Marshal.StringToHGlobalUni(environmentText);
            cancellation.ThrowIfCancellationRequested();
            ValidateExecutable(executable); validateWorkingDirectory();
            if (!NativeProcessMethods.CreateProcessW(executable, NativeProcessLaunch.BuildCommandLine(executable, arguments),
                IntPtr.Zero, IntPtr.Zero, true,
                NativeProcessMethods.Suspended | NativeProcessMethods.NoWindow | NativeProcessMethods.UnicodeEnvironment | NativeProcessMethods.ExtendedStartupInfo,
                environmentBlock, directory, ref startup, out var information)) throw Failure();
            Process = new NativeProcessHandle(information.Process);
            ProcessId = checked((int)information.ProcessId);
            using var thread = new NativeProcessHandle(information.Thread);
            if (!NativeProcessMethods.AssignProcessToJobObject(Job, Process)) throw Failure();
            cancellation.ThrowIfCancellationRequested();
            ValidateExecutable(executable); validateWorkingDirectory();
            if (NativeProcessMethods.ResumeThread(thread) == uint.MaxValue) throw Failure();
        }
        finally
        {
            if (initialized) NativeProcessMethods.DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (environmentBlock != IntPtr.Zero)
            {
                for (var index = 0; index < environmentLength; index++) Marshal.WriteInt16(environmentBlock, index * sizeof(char), 0);
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    private static SafeFileHandle Pipe(bool output, out SafeFileHandle parent)
    {
        var security = new NativeProcessMethods.SecurityAttributes { Length = Marshal.SizeOf<NativeProcessMethods.SecurityAttributes>(), Inherit = true };
        if (!NativeProcessMethods.CreatePipe(out var read, out var write, ref security, 0)) throw Failure();
        parent = new SafeFileHandle(output ? read : write, true);
        var child = new SafeFileHandle(output ? write : read, true);
        if (!NativeProcessMethods.SetHandleInformation(parent.DangerousGetHandle(), 1, 0))
        {
            parent.Dispose();
            child.Dispose();
            throw Failure();
        }

        return child;
    }

    private void PinExecutable(string executable, string? expectedExecutableSha256, CancellationToken cancellation)
    {
        ValidatePath(executable);
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("An approved executable is required.", nameof(executable));
        var root = Path.GetPathRoot(executable)!;
        var parent = NativeWorkspace.OpenDrive(root);
        pins.Add(parent);
        var components = executable.Substring(root.Length).Split('\\');
        for (var index = 0; index < components.Length - 1; index++)
        {
            parent = NativeWorkspace.OpenDirectory(parent, components[index]);
            pins.Add(parent);
        }

        // 祖先已逐级固定。允许受信 Windows 可执行文件的系统硬链接，但运行期拒绝写入/替换。
        var file = NativeProcessMethods.CreateFileW(executable, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        pins.Add(file);
        if (file.IsInvalid || !NativeProcessMethods.GetFileInformationByHandle(file, out var info)) throw Failure();
        if ((info.Attributes & 0x410) != 0) throw new WindowsDuplexProcessException("unsafe_executable");
        if (!string.Equals(NativeWorkspace.FinalPath(file), @"\\?\" + executable, StringComparison.OrdinalIgnoreCase))
            throw new WindowsDuplexProcessException("aliased_executable");
        if (expectedExecutableSha256 != null) VerifyExecutableDigest(file, expectedExecutableSha256, cancellation);
        ValidateExecutable(executable);
    }

    private void ValidateExecutable(string executable)
    {
        // 各目录下一个固定子项及 exe 叶不可删除，整条祖先链持续非空，不能设重解析点。
        for (var index = 0; index < pins.Count - 1; index++) NativeWorkspace.Validate(pins[index], true);
        var file = pins[pins.Count - 1];
        if (!NativeProcessMethods.GetFileInformationByHandle(file, out var info) || (info.Attributes & 0x410) != 0 ||
            !string.Equals(NativeWorkspace.FinalPath(file), @"\\?\" + executable, StringComparison.OrdinalIgnoreCase))
            throw new WindowsDuplexProcessException("unsafe_executable");
    }

    private static void VerifyExecutableDigest(SafeFileHandle file, string expected, CancellationToken cancellation)
    {
        // 副本仍指向同一已固定文件对象，不重新按路径打开；其关闭不会释放 pins 的原始句柄。
        if (!DuplicateHandle(new IntPtr(-1), file, new IntPtr(-1), out var raw, 0, false, 2))
            throw new WindowsDuplexProcessException("executable_identity_unavailable");
        using var duplicate = new SafeFileHandle(raw, true);
        using var stream = new FileStream(duplicate, FileAccess.Read, 65536, false);
        using var algorithm = SHA256.Create();
        stream.Position = 0;
        var buffer = new byte[65536];
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                algorithm.TransformBlock(buffer, 0, count, buffer, 0);
            }

            algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var actual = BitConverter.ToString(algorithm.Hash!).Replace("-", string.Empty).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new WindowsDuplexProcessException("executable_digest_mismatch");
        }
        finally { Array.Clear(buffer, 0, buffer.Length); }
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.IndexOfAny(new[] { '\0', '/', '"', '*', '?' }) >= 0 || path.IndexOf(':', 2) >= 0 ||
            !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A canonical absolute local path is required.", nameof(path));
    }

    private static string EnvironmentBlock(IDictionary<string, string> environment)
    {
        var value = new StringBuilder();
        foreach (var pair in environment.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.IndexOfAny(new[] { '\0', '=' }) >= 0 || pair.Value == null || pair.Value.IndexOf('\0') >= 0)
                throw new ArgumentException("Invalid environment entry.", nameof(environment));
            value.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        value.Append('\0');
        if (environment.Count == 0) value.Append('\0');
        if (value.Length > 32767) throw new ArgumentException("Environment exceeds the Windows limit.", nameof(environment));
        return value.ToString();
    }

    private static WindowsDuplexProcessException Failure() => new("native_start_" + Marshal.GetLastWin32Error());

    public void Dispose()
    {
        Job?.Dispose();
        Input?.Dispose();
        Output?.Dispose();
        Error?.Dispose();
        Process?.Dispose();
        for (var index = pins.Count - 1; index >= 0; index--) pins[index].Dispose();
    }
}

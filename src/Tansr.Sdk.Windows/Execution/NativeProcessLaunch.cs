using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Tansr.Sdk.Windows.Execution;

/// <summary>严格控制句柄继承和进程树归属。任何失败都发生在主线程恢复前。</summary>
internal sealed class NativeProcessLaunch : IDisposable
{
    private readonly List<SafeFileHandle> executablePins = new();

    private NativeProcessLaunch()
    {
    }

    internal NativeProcessHandle? Process { get; private set; }

    internal NativeProcessHandle? Job { get; private set; }

    internal SafeFileHandle? StandardOutput { get; private set; }

    internal SafeFileHandle? StandardError { get; private set; }

    internal int? ProcessId { get; private set; }

    internal bool Started { get; private set; }

    internal int? StartError { get; private set; }

    internal bool CleanupConfirmed { get; private set; } = true;

    internal static NativeProcessLaunch Start(string executable, IReadOnlyList<string> arguments, string directory, IDictionary<string, string> environment,
        string? expectedExecutableSha256 = null, CancellationToken cancellation = default, Action? validateBeforeStart = null)
    {
        var launch = new NativeProcessLaunch();
        try
        {
            launch.PinExecutable(executable);
            if (expectedExecutableSha256 != null) VerifyExecutableDigest(launch.executablePins[launch.executablePins.Count - 1], expectedExecutableSha256, cancellation);
            cancellation.ThrowIfCancellationRequested();
            launch.StartCore(executable, arguments, directory, environment, () => { cancellation.ThrowIfCancellationRequested(); validateBeforeStart?.Invoke(); });
            return launch;
        }
        catch (Win32Exception error)
        {
            launch.StartError = error.NativeErrorCode;
            if (launch.Process != null)
            {
                NativeProcessMethods.TerminateProcess(launch.Process, 0xC000013A);
                launch.CleanupConfirmed = NativeProcessMethods.WaitForSingleObject(launch.Process, 5000) == NativeProcessMethods.WaitObject;
            }

            return launch;
        }
        catch
        {
            launch.Dispose();
            throw;
        }
    }

    private void StartCore(string executable, IReadOnlyList<string> arguments, string directory, IDictionary<string, string> environment, Action validateBeforeStart)
    {
        Job = new NativeProcessHandle(NativeProcessMethods.CreateJobObjectW(IntPtr.Zero, null));
        if (Job.IsInvalid)
        {
            throw NativeError();
        }

        var limits = new NativeProcessMethods.ExtendedLimitInformation();
        limits.Basic.Flags = NativeProcessMethods.KillOnJobClose;
        if (!NativeProcessMethods.SetInformationJobObject(Job, 9, ref limits, Marshal.SizeOf<NativeProcessMethods.ExtendedLimitInformation>()))
        {
            throw NativeError();
        }

        using var stdoutWrite = CreateOutputPipe(out var stdoutRead);
        StandardOutput = stdoutRead;
        using var stderrWrite = CreateOutputPipe(out var stderrRead);
        StandardError = stderrRead;
        // stdin 永远为 EOF，不能误用宿主终端输入；交互式 stdin 属于另外显式协商的能力。
        var security = new NativeProcessMethods.SecurityAttributes { Length = Marshal.SizeOf<NativeProcessMethods.SecurityAttributes>(), Inherit = true };
        if (!NativeProcessMethods.CreatePipe(out var stdinReadHandle, out var stdinWriteHandle, ref security, 0))
        {
            throw NativeError();
        }

        using var stdinRead = new SafeFileHandle(stdinReadHandle, true);
        using (var stdinWrite = new SafeFileHandle(stdinWriteHandle, true))
        {
            // 在 CreateProcess 前关闭写端，不会把额外输入能力交给子进程。
        }

        var attributeList = IntPtr.Zero;
        var handleList = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        var environmentLength = 0;
        var initialized = false;
        try
        {
            var size = IntPtr.Zero;
            NativeProcessMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero)
            {
                throw NativeError();
            }

            attributeList = Marshal.AllocHGlobal(size);
            if (!NativeProcessMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
            {
                throw NativeError();
            }

            initialized = true;
            handleList = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handleList, 0, stdinRead.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, stdoutWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size * 2, stderrWrite.DangerousGetHandle());
            // PROC_THREAD_ATTRIBUTE_HANDLE_LIST 排除宿主所有其余可继承句柄。
            if (!NativeProcessMethods.UpdateProcThreadAttribute(attributeList, 0, new IntPtr(0x00020002), handleList,
                new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
            {
                throw NativeError();
            }

            var startup = new NativeProcessMethods.StartupInfoEx
            {
                Startup = new NativeProcessMethods.StartupInfo
                {
                    Size = Marshal.SizeOf<NativeProcessMethods.StartupInfoEx>(),
                    Flags = 0x00000101, // USESTDHANDLES | USESHOWWINDOW; SW_HIDE=0
                    StandardInput = stdinRead.DangerousGetHandle(),
                    StandardOutput = stdoutWrite.DangerousGetHandle(),
                    StandardError = stderrWrite.DangerousGetHandle(),
                },
                Attributes = attributeList,
            };
            var environmentText = BuildEnvironment(environment);
            environmentLength = environmentText.Length;
            environmentBlock = Marshal.StringToHGlobalUni(environmentText);
            var flags = NativeProcessMethods.Suspended | NativeProcessMethods.NoWindow |
                NativeProcessMethods.UnicodeEnvironment | NativeProcessMethods.ExtendedStartupInfo;
            validateBeforeStart();
            if (!NativeProcessMethods.CreateProcessW(executable, BuildCommandLine(executable, arguments), IntPtr.Zero, IntPtr.Zero,
                true, flags, environmentBlock, directory, ref startup, out var information))
            {
                throw NativeError();
            }

            Process = new NativeProcessHandle(information.Process);
            using var thread = new NativeProcessHandle(information.Thread);
            ProcessId = checked((int)information.ProcessId);
            CleanupConfirmed = false;
            if (!NativeProcessMethods.AssignProcessToJobObject(Job, Process))
            {
                throw NativeError();
            }

            // 被挂起的主线程尚未运行用户代码，不存在先 fork 再加入 job 的窗口。
            if (NativeProcessMethods.ResumeThread(thread) == uint.MaxValue)
            {
                throw NativeError();
            }

            Started = true;
        }
        finally
        {
            if (initialized)
            {
                NativeProcessMethods.DeleteProcThreadAttributeList(attributeList);
            }

            if (attributeList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(attributeList);
            }

            if (handleList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(handleList);
            }

            if (environmentBlock != IntPtr.Zero)
            {
                // 环境块中含多个 NUL 分隔项，不能只清理第一个 NUL 之前的内容。
                for (var index = 0; index < environmentLength; index++)
                {
                    Marshal.WriteInt16(environmentBlock, index * sizeof(char), 0);
                }

                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    private static SafeFileHandle CreateOutputPipe(out SafeFileHandle read)
    {
        var security = new NativeProcessMethods.SecurityAttributes { Length = Marshal.SizeOf<NativeProcessMethods.SecurityAttributes>(), Inherit = true };
        if (!NativeProcessMethods.CreatePipe(out var readHandle, out var writeHandle, ref security, 0))
        {
            throw NativeError();
        }

        read = new SafeFileHandle(readHandle, true);
        var write = new SafeFileHandle(writeHandle, true);
        if (!NativeProcessMethods.SetHandleInformation(readHandle, 1, 0))
        {
            var error = NativeError();
            read.Dispose();
            write.Dispose();
            throw error;
        }

        return write;
    }

    private void PinExecutable(string executable)
    {
        var root = Path.GetPathRoot(executable)!;
        var current = root;
        PinPath(current, true);
        var parts = executable.Substring(root.Length).Split(Path.DirectorySeparatorChar);
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            PinPath(current, index != parts.Length - 1);
        }
    }

    private static void VerifyExecutableDigest(SafeFileHandle file, string expected, CancellationToken cancellation)
    {
        // 包装同一固定句柄且不拥有它；当前 launch 持有原句柄直到验证、启动及运行全部结束。
        using var borrowed = new SafeFileHandle(file.DangerousGetHandle(), ownsHandle: false);
        using var stream = new FileStream(borrowed, FileAccess.Read, 65536, false);
        using var hash = SHA256.Create(); var buffer = new byte[65536];
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var count = stream.Read(buffer, 0, buffer.Length); if (count == 0) break;
                hash.TransformBlock(buffer, 0, count, buffer, 0);
            }
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if (!string.Equals(BitConverter.ToString(hash.Hash!).Replace("-", "").ToLowerInvariant(), expected, StringComparison.Ordinal))
                throw new WindowsWorkspaceException("executable_digest_mismatch");
        }
        finally { Array.Clear(buffer, 0, buffer.Length); GC.KeepAlive(file); }
    }

    private void PinPath(string path, bool directory)
    {
        // 逐级持有祖先，拒绝重解析点，并禁止 rename/delete；可执行文件另禁止写入。
        var handle = NativeProcessMethods.CreateFileW(path, directory ? 0x80U : 0x80000000U,
            1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        executablePins.Add(handle);
        if (handle.IsInvalid || !NativeProcessMethods.GetFileInformationByHandle(handle, out var information))
        {
            throw NativeError();
        }

        if ((information.Attributes & 0x400) != 0 || ((information.Attributes & 0x10) != 0) != directory)
        {
            throw new ArgumentException("Approved executable must have a stable, non-reparse local path.", nameof(path));
        }

        var resolved = new StringBuilder(32768);
        var count = NativeProcessMethods.GetFinalPathNameByHandleW(handle, resolved, (uint)resolved.Capacity, 0);
        if (count == 0 || count >= resolved.Capacity)
        {
            throw NativeError();
        }

        var final = resolved.ToString();
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            final = final.Substring(4);
        }

        if (!string.Equals(final.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Approved executable path resolves to a different resource.", nameof(path));
        }
    }

    internal static StringBuilder BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var command = new StringBuilder(QuoteArgument(executable));
        foreach (var argument in arguments)
        {
            command.Append(' ').Append(QuoteArgument(argument));
        }

        if (command.Length >= 32767)
        {
            throw new ArgumentException("The command line exceeds the Windows limit.", nameof(arguments));
        }

        return command;
    }

    private static string QuoteArgument(string value)
    {
        if (value.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("An argument contains a null character.", nameof(value));
        }

        // 与 Windows ArgumentList 一致：无空白/引号的参数不添加多余引号。
        // cmd.exe 有自己的开关解析，给 /d /s /c 一律加引号会改变命令语义。
        if (value.Length > 0 && value.IndexOf('"') < 0 && !value.Any(char.IsWhiteSpace))
        {
            return value;
        }

        var output = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            output.Append('\\', character == '"' ? (slashes * 2) + 1 : slashes);
            output.Append(character);
            slashes = 0;
        }

        output.Append('\\', slashes * 2).Append('"');
        return output.ToString();
    }

    private static string BuildEnvironment(IDictionary<string, string> environment)
    {
        var value = new StringBuilder();
        foreach (var pair in environment.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.IndexOfAny(new[] { '=', '\0' }) >= 0 || pair.Value == null || pair.Value.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("Environment entries must have explicit valid names and values.", nameof(environment));
            }

            value.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        value.Append('\0');
        if (environment.Count == 0)
        {
            value.Append('\0');
        }

        if (value.Length > 32767)
        {
            throw new ArgumentException("The environment exceeds the supported size limit.", nameof(environment));
        }

        return value.ToString();
    }

    internal static Win32Exception NativeError() => new(Marshal.GetLastWin32Error());

    public void Dispose()
    {
        Job?.Dispose(); // 关闭最后一个 job 句柄是清理兜底，不伪称已确认退出。
        StandardOutput?.Dispose();
        StandardError?.Dispose();
        Process?.Dispose();
        for (var index = executablePins.Count - 1; index >= 0; index--)
        {
            executablePins[index].Dispose();
        }
    }
}

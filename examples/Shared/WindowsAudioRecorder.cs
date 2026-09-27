using System.IO;
using System.Runtime.InteropServices;

namespace Tansr.Examples;

/// <summary>WinMM 16 kHz/16 bit/mono 录音；启动必须来自用户操作，最多两分钟，输出标准 WAV。</summary>
internal sealed class WindowsAudioRecorder : IDisposable
{
    private const int MaximumBytes = 16000 * 2 * 120;
    private readonly object _gate = new();
    private readonly MemoryStream _pcm = new();
    private readonly List<(IntPtr Header, IntPtr Data)> _buffers = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _capture = Task.CompletedTask;
    private IntPtr _device;
    private bool _stopping, _disposed;
    private string? _failure;
    internal bool LimitReached { get; private set; }

    internal sealed class InputDevice
    {
        internal static InputDevice SystemDefault { get; } = new(uint.MaxValue, "系统默认（由 Windows 选择）", null);
        internal uint Id { get; }
        public string Name { get; }
        private readonly WaveInputCapabilities? _capabilities;
        private InputDevice(uint id, string name, WaveInputCapabilities? capabilities) { Id = id; Name = name; _capabilities = capabilities; }
        internal static InputDevice FromCapabilities(uint id, WaveInputCapabilities capabilities) => new(id, capabilities.Name + " [" + id + "]", capabilities);
        internal bool Matches(WaveInputCapabilities capabilities) => _capabilities is WaveInputCapabilities expected &&
            expected.Manufacturer == capabilities.Manufacturer && expected.Product == capabilities.Product && expected.Version == capabilities.Version &&
            expected.Name == capabilities.Name && expected.Formats == capabilities.Formats && expected.Channels == capabilities.Channels;
        public override string ToString() => Name;
    }

    // 只枚举，不打开或启动输入设备。设备号不持久化；每次录音前后重新核对所选设备。
    internal static IReadOnlyList<InputDevice> EnumerateInputDevices()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("recording_requires_windows");
        var devices = new List<InputDevice>();
        var count = waveInGetNumDevs();
        for (uint id = 0; id < count; id++)
            if (waveInGetDevCaps(new UIntPtr(id), out var capabilities, (uint)Marshal.SizeOf(typeof(WaveInputCapabilities))) == 0)
                devices.Add(InputDevice.FromCapabilities(id, capabilities));
        return devices;
    }

    internal WindowsAudioRecorder(InputDevice? input = null)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("recording_requires_windows");
        input ??= InputDevice.SystemDefault;
        if (input.Id != uint.MaxValue)
        {
            Check(waveInGetDevCaps(new UIntPtr(input.Id), out var capabilities, (uint)Marshal.SizeOf(typeof(WaveInputCapabilities))), "recording_device_unavailable");
            if (!input.Matches(capabilities)) throw new InvalidOperationException("recording_device_changed");
        }
        var format = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSec = 16000, AvgBytesPerSec = 32000, BlockAlign = 2, BitsPerSample = 16 };
        Check(waveInOpen(out _device, new UIntPtr(input.Id), ref format, IntPtr.Zero, IntPtr.Zero, 0), "recording_device_unavailable");
        try
        {
            if (input.Id != uint.MaxValue)
            {
                Check(waveInGetOpenedDeviceCaps(_device, out var capabilities, (uint)Marshal.SizeOf(typeof(WaveInputCapabilities))), "recording_device_unavailable");
                if (!input.Matches(capabilities)) throw new InvalidOperationException("recording_device_changed");
            }
            for (var i = 0; i < 4; i++)
            {
                var data = Marshal.AllocHGlobal(4096); var header = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveHeader)));
                Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = 4096 }, header, false); _buffers.Add((header, data));
                Check(waveInPrepareHeader(_device, header, (uint)Marshal.SizeOf(typeof(WaveHeader))), "recording_prepare_failed");
                Check(waveInAddBuffer(_device, header, (uint)Marshal.SizeOf(typeof(WaveHeader))), "recording_buffer_failed");
            }
            Check(waveInStart(_device), "recording_start_failed");
            _capture = Task.Run(CaptureAsync);
        }
        catch { Dispose(); throw; }
    }

    // 无驱动回调：同一工作线程消费 WHDR_DONE 并归还缓冲，收尾先等待线程，避免回调/驱动锁重入。
    private async Task CaptureAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested && !LimitReached)
            {
                Drain(true);
                await Task.Delay(20, _stop.Token).ConfigureAwait(false);
            }
            if (LimitReached)
            {
                // 到帽不仅停止复制：实际停掉麦克风设备。外部 StopDevice 等待本任务后再排空/释放。
                Check(waveInStop(_device), "recording_stop_failed");
                Check(waveInReset(_device), "recording_reset_failed");
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch { _failure = "recording_capture_failed"; }
    }
    private void Drain(bool requeue)
    {
        foreach (var buffer in _buffers)
        {
            var header = (WaveHeader)Marshal.PtrToStructure(buffer.Header, typeof(WaveHeader))!;
            if ((header.Flags & 1) == 0) continue;
            var size = (int)Math.Min(header.BytesRecorded, MaximumBytes - _pcm.Length); size -= size % 2;
            if (size > 0) { var bytes = new byte[size]; Marshal.Copy(header.Data, bytes, 0, size); _pcm.Write(bytes, 0, size); }
            header.BytesRecorded = 0; Marshal.StructureToPtr(header, buffer.Header, false);
            if (_pcm.Length >= MaximumBytes) LimitReached = true;
            if (requeue && !LimitReached) Check(waveInAddBuffer(_device, buffer.Header, (uint)Marshal.SizeOf(typeof(WaveHeader))), "recording_buffer_failed");
        }
    }

    internal Task<byte[]> StopAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WindowsAudioRecorder));
            StopDevice();
            if (_failure != null) throw new InvalidOperationException(_failure);
            if (_pcm.Length == 0) throw new InvalidOperationException("recording_empty");
            using var output = new MemoryStream(); using (var writer = new BinaryWriter(output, System.Text.Encoding.ASCII, true))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write((int)_pcm.Length + 36); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write((int)_pcm.Length); writer.Write(_pcm.ToArray());
            }
            return output.ToArray();
        }
    });
    private void StopDevice()
    {
        if (_device == IntPtr.Zero || _stopping) return; _stopping = true; _stop.Cancel(); _capture.GetAwaiter().GetResult();
        Check(waveInStop(_device), "recording_stop_failed"); Check(waveInReset(_device), "recording_reset_failed"); Drain(false);
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { StopDevice(); }
            catch { _failure = "recording_cleanup_failed"; }
            finally
            {
                _disposed = true;
                foreach (var buffer in _buffers)
                {
                    var header = (WaveHeader)Marshal.PtrToStructure(buffer.Header, typeof(WaveHeader))!;
                    // 未prepare或已成功归还的内存才释放；驱动失败保留，不能释放仍在使用的native buffer。
                    if ((header.Flags & 2) == 0 || _device == IntPtr.Zero || waveInUnprepareHeader(_device, buffer.Header, (uint)Marshal.SizeOf(typeof(WaveHeader))) == 0)
                    { Marshal.FreeHGlobal(buffer.Header); Marshal.FreeHGlobal(buffer.Data); }
                }
                if (_device != IntPtr.Zero) waveInClose(_device); _device = IntPtr.Zero; _pcm.Dispose(); _stop.Dispose();
            }
        }
    }
    private static void Check(uint result, string code) { if (result != 0) throw new InvalidOperationException(code + ":" + result); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WaveInputCapabilities
    {
        internal ushort Manufacturer, Product;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
        internal uint Formats;
        internal ushort Channels, Reserved;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat { internal ushort FormatTag, Channels; internal uint SamplesPerSec, AvgBytesPerSec; internal ushort BlockAlign, BitsPerSample, ExtraSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader { internal IntPtr Data; internal uint BufferLength, BytesRecorded; internal IntPtr User; internal uint Flags, Loops; internal IntPtr Next, Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveInGetDevCaps(UIntPtr deviceId, out WaveInputCapabilities capabilities, uint size);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetDevCapsW")] private static extern uint waveInGetOpenedDeviceCaps(IntPtr device, out WaveInputCapabilities capabilities, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle, UIntPtr deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInStop(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);
}

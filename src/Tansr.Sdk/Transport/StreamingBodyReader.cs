using System.Net;
using System.Runtime.InteropServices;

namespace Tansr.Sdk.Transport;

/// <summary>Preserves partial-frame delivery on the Framework HTTP response stream.</summary>
internal static class StreamingBodyReader
{
    private static readonly bool IsFramework = RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.Ordinal);

    internal static async Task<int> ReadAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var type = stream.GetType();
            // Framework ConnectStream.BeginRead can consume its existing response buffer
            // and then wait for more network bytes instead of returning that partial frame.
            // Its synchronous Read returns those bytes immediately. Keep the caller's bulk
            // buffer; only this BCL legacy stream occupies a worker while awaiting network IO.
            // The caller still owns disposal on cancellation, which interrupts that read.
            var legacyResponse = IsFramework && type.FullName == "System.Net.ConnectStream" && type.Assembly == typeof(WebException).Assembly;
            var read = legacyResponse
                ? await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return stream.Read(buffer, offset, count);
                }, cancellationToken).ConfigureAwait(false)
                : await stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return read;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }
}

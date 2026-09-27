using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace Tansr.Sdk.Transport;

/// <summary>Preserves partial-frame delivery on the Framework HTTP response stream.</summary>
internal static class StreamingBodyReader
{
    private static readonly bool IsFramework = RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.Ordinal);

    internal static async Task<int> ReadAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken, bool frameworkHttpResponse)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var type = stream.GetType();
            // Framework ConnectStream.BeginRead can consume its existing response buffer
            // and then wait for more network bytes instead of returning that partial frame.
            // Its synchronous Read returns those bytes immediately. Keep the caller's bulk
            // buffer; only the legacy response stream or its BCL HTTP read-only wrapper
            // occupies a worker while awaiting network IO. HttpContent wraps ConnectStream,
            // so recognizing only the inner stream's private type misses real HttpClient IO.
            // The caller still owns disposal on cancellation, which interrupts that read.
            // StreamContent also wraps application-supplied streams. Only a known default
            // HTTP transport may opt into this compatibility path; injected clients retain
            // their asynchronous stream contract even when the SDK owns their disposal.
            var legacyResponse = frameworkHttpResponse && IsFramework &&
                ((type.Assembly == typeof(HttpClient).Assembly && !stream.CanWrite) ||
                 (type.FullName == "System.Net.ConnectStream" && type.Assembly == typeof(WebException).Assembly));
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

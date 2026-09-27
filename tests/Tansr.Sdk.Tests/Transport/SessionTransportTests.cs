using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Tansr.Sdk.Client;
using Tansr.Sdk.Transport;

namespace Tansr.Sdk.Tests.Transport;

public sealed class SessionTransportTests
{
    private const string Created = "{\"sessionId\":\"s\",\"resumed\":false,\"lastSeq\":0}";

    [Theory]
    [InlineData("eof")]
    [InlineData("bytes")]
    [InlineData("io-error")]
    public async Task CancelledSdk1ResponseCannotCommitBufferedSuccessWhenDisposalCompletesTheRead(string completion)
    {
        using var stream = new DisposalCompletedStream(Encoding.UTF8.GetBytes(Created), completion);
        using var handler = new Handler(stream);
        using var http = new HttpClient(handler);
        using var client = new TansrClient(new TansrClientOptions
        {
            BaseUri = new Uri("https://serve.test/"),
            TokenProvider = _ => Task.FromResult("synthetic-token")
        }, http);
        using var cancellation = new CancellationTokenSource();

        var create = client.CreateSessionAsync(new(), cancellation.Token);
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => create.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stream.Disposed);
        Assert.Equal(new[] { "POST /v2/sessions" }, handler.Requests);
    }

    [Fact]
    public async Task NormalEofKeepsExactBodyAndTheOriginalResponseLimit()
    {
        var bytes = Encoding.UTF8.GetBytes(Created);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        Assert.Equal(bytes, await SessionTransport.ReadBodyAsync(response, bytes.Length, default));

        using var tooLarge = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        Assert.Equal("response_too_large", (await Assert.ThrowsAsync<TansrProtocolException>(
            () => SessionTransport.ReadBodyAsync(tooLarge, bytes.Length - 1, default))).Code);
    }

    private sealed class Handler(Stream stream) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                }
            });
        }
    }

    // A valid body has already arrived. The trailing network read ignores the token and is
    // released by Dispose, as response streams may be; EOF is not evidence of request success.
    private sealed class DisposalCompletedStream(byte[] payload, string completion) : Stream
    {
        private readonly TaskCompletionSource<bool> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool sent;
        internal TaskCompletionSource<bool> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (!sent)
            {
                Assert.True(count >= payload.Length);
                sent = true;
                Buffer.BlockCopy(payload, 0, buffer, offset, payload.Length);
                return payload.Length;
            }
            Waiting.TrySetResult(true);
            await closed.Task.ConfigureAwait(false);
            if (completion == "io-error") throw new IOException("synthetic response closed");
            return completion == "bytes" ? 1 : 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposed = true; closed.TrySetResult(true); }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

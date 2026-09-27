using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tansr.Sdk.Media;

namespace Tansr.Sdk.Tests.Views;

public sealed class MediaDownloadLifecycleTests
{
    [Theory]
    [InlineData("caller", "eof")]
    [InlineData("caller", "io")]
    [InlineData("caller", "disposed")]
    [InlineData("downloader", "eof")]
    [InlineData("timeout", "eof")]
    public async Task CancellationNeverReturnsPartialMediaEvenWhenClosingTheStreamReportsEof(string trigger, string closeResult)
    {
        using var stream = new ClosingStream(closeResult);
        using var handler = new Handler(stream);
        using var downloader = new MediaDownloader(new MediaDownloadOptions
        {
            AllowedHttpsHosts = ["cdn.example"],
            Timeout = trigger == "timeout" ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(10)
        }, handler);
        using var canceled = new CancellationTokenSource();
        var reading = downloader.ReadAsync(Image(), canceled.Token);
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (trigger == "caller") canceled.Cancel();
        else if (trigger == "downloader") downloader.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(reading.IsCanceled);
        Assert.True(stream.Closed);
        Assert.Equal(1, handler.Requests);
        if (trigger == "downloader")
            await Assert.ThrowsAsync<ObjectDisposedException>(() => downloader.ReadAsync(Image()));
    }

    [Fact]
    public async Task InterruptedBodyIsAVisibleFailureWithoutPartialContentOrTransportDetails()
    {
        using var stream = new ClosingStream("io");
        using var handler = new Handler(stream);
        using var downloader = new MediaDownloader(new MediaDownloadOptions { AllowedHttpsHosts = ["cdn.example"] }, handler);
        var reading = downloader.ReadAsync(Image());
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stream.Dispose();
        var error = await Assert.ThrowsAsync<MediaException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("media_download_failed", error.Code);
        Assert.Equal(error.Code, error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public async Task MissingContentLengthStillEnforcesTheActualByteLimit(int bytes, bool succeeds)
    {
        using var stream = new UnknownLengthStream(new byte[bytes]);
        using var handler = new Handler(stream);
        using var downloader = new MediaDownloader(new MediaDownloadOptions { AllowedHttpsHosts = ["cdn.example"], MaximumBytes = 4 }, handler);
        if (succeeds) Assert.Equal(bytes, (await downloader.ReadAsync(Image())).Length);
        else Assert.Equal("media_too_large", (await Assert.ThrowsAsync<MediaException>(() => downloader.ReadAsync(Image()))).Code);
        Assert.Equal(1, handler.Requests);
    }

    private static MediaResource Image()
    {
        using var data = JsonDocument.Parse("{\"model\":\"m\",\"images\":[{\"url\":\"https://cdn.example/image\"}]}");
        return MediaArtifactParser.Parse(data.RootElement)!.Resources[0];
    }

    private sealed class Handler(Stream stream) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }
            });
        }
    }

    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class ClosingStream(string closeResult) : Stream
    {
        private readonly TaskCompletionSource<int> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _prefix;
        internal TaskCompletionSource<bool> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Closed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (!_prefix)
            {
                _prefix = true;
                buffer[offset] = 1; buffer[offset + 1] = 2; buffer[offset + 2] = 3;
                return Task.FromResult(3);
            }
            Waiting.TrySetResult(true);
            // Models a transport whose outstanding read is released by Dispose, not by its token.
            return _end.Task;
        }
        protected override void Dispose(bool disposing)
        {
            Closed = true;
            if (closeResult == "io") _end.TrySetException(new IOException("synthetic transport details"));
            else if (closeResult == "disposed") _end.TrySetException(new ObjectDisposedException("synthetic transport"));
            else _end.TrySetResult(0);
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

using System.Diagnostics;

namespace AdbSync.Core.Services.Common;

/// <summary>Wraps a stream and delays reads/writes so cumulative throughput stays under <paramref name="maxBytesPerSecond"/>.</summary>
internal sealed class ThrottledStream(Stream inner, long maxBytesPerSecond) : Stream
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _bytesTransferred;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        ThrottleAsync(read, CancellationToken.None).GetAwaiter().GetResult();
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        await ThrottleAsync(read, cancellationToken);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ThrottleAsync(count, CancellationToken.None).GetAwaiter().GetResult();
        inner.Write(buffer, offset, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await ThrottleAsync(count, cancellationToken);
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    private async Task ThrottleAsync(int bytes, CancellationToken ct)
    {
        if (bytes <= 0 || maxBytesPerSecond <= 0)
            return;

        _bytesTransferred += bytes;
        var expectedSeconds = (double)_bytesTransferred / maxBytesPerSecond;
        var delay = expectedSeconds - _stopwatch.Elapsed.TotalSeconds;
        if (delay > 0)
            await Task.Delay(TimeSpan.FromSeconds(delay), ct);
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}

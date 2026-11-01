namespace OutPost2.Web.Files.Internal;

/// <summary>Thrown when a request body runs past the size its ticket allowed.</summary>
public sealed class BodyTooLargeException(long limit)
    : Exception($"The body exceeded the agreed {limit} bytes.")
{
    public long Limit { get; } = limit;
}

/// <summary>
/// A read-only wrapper that refuses to hand over more than it was told to (OP2-252).
/// <para>
/// The size is agreed when the upload ticket is minted, and the obvious way to enforce it is
/// <c>Content-Length</c> — which the sender writes. This counts what actually arrives
/// instead, so a header that lies produces a 413 rather than a quota's worth of somebody
/// else's disk.
/// </para>
/// <para>
/// It throws rather than truncating. Truncation would hand the staging service a file that
/// is short by an arbitrary amount, and that file would then go out as an attachment: a
/// corrupt document delivered successfully is worse than a refused upload.
/// </para>
/// </summary>
public sealed class CappedStream(Stream inner, long limit) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = inner.Read(buffer);
        Count(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken);
        Count(n);
        return n;
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Count(int n)
    {
        if (n <= 0) return;

        _read += n;

        // Strictly greater: a body of exactly the agreed size is fine.
        if (_read > limit) throw new BodyTooLargeException(limit);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

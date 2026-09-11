namespace SmartX.Application.Attachments;

/// <summary>
/// Presents an already-consumed prefix followed by the remainder of an
/// underlying readable stream.
///
/// Smart-X uses this stream after inspecting the first few bytes of an
/// attachment. The inspected bytes are replayed so downstream encryption
/// still receives the complete original file without buffering the entire
/// upload in memory.
/// </summary>
internal sealed class PrefixReplayingReadStream : Stream
{
    private readonly byte[] _prefix;
    private readonly Stream _inner;
    private readonly bool _leaveOpen;

    private int _prefixPosition;

    public PrefixReplayingReadStream(
        byte[] prefix,
        Stream inner,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(inner);

        if (!inner.CanRead)
        {
            throw new ArgumentException(
                "The underlying stream must be readable.",
                nameof(inner));
        }

        // Own a private copy so callers cannot mutate the bytes being
        // replayed after construction.
        _prefix =
            prefix.ToArray();

        _inner =
            inner;

        _leaveOpen =
            leaveOpen;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length =>
        throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        // Read-only stream: nothing to flush.
    }

    public override int Read(
        byte[] buffer,
        int offset,
        int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return Read(
            buffer.AsSpan(
                offset,
                count));
    }

    public override int Read(
        Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var prefixBytesRead =
            CopyPrefixTo(buffer);

        if (prefixBytesRead ==
            buffer.Length)
        {
            return prefixBytesRead;
        }

        var innerBytesRead =
            _inner.Read(
                buffer[prefixBytesRead..]);

        return prefixBytesRead +
               innerBytesRead;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return ReadAsync(
                buffer.AsMemory(
                    offset,
                    count),
                cancellationToken)
            .AsTask();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (buffer.IsEmpty)
        {
            return 0;
        }

        var prefixBytesRead =
            CopyPrefixTo(
                buffer.Span);

        if (prefixBytesRead ==
            buffer.Length)
        {
            return prefixBytesRead;
        }

        var innerBytesRead =
            await _inner.ReadAsync(
                buffer[prefixBytesRead..],
                cancellationToken);

        return prefixBytesRead +
               innerBytesRead;
    }

    public override long Seek(
        long offset,
        SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(
        long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(
        byte[] buffer,
        int offset,
        int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(
        bool disposing)
    {
        if (disposing &&
            !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await _inner.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private int CopyPrefixTo(
        Span<byte> destination)
    {
        var remainingPrefixLength =
            _prefix.Length -
            _prefixPosition;

        if (remainingPrefixLength <= 0)
        {
            return 0;
        }

        var count =
            Math.Min(
                destination.Length,
                remainingPrefixLength);

        _prefix
            .AsSpan(
                _prefixPosition,
                count)
            .CopyTo(destination);

        _prefixPosition +=
            count;

        return count;
    }
}
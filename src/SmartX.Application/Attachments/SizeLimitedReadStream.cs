namespace SmartX.Application.Attachments;

/// <summary>
/// Wraps an incoming stream and prevents consumers from reading more than
/// the configured maximum number of bytes.
///
/// The limit is enforced while the stream is consumed instead of relying
/// only on a client-provided Content-Length header, which cannot be trusted.
/// </summary>
internal sealed class SizeLimitedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private readonly bool _leaveOpen;

    private long _bytesRead;

    public SizeLimitedReadStream(
        Stream inner,
        long maxBytes,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (!inner.CanRead)
        {
            throw new ArgumentException(
                "The supplied attachment stream must be readable.",
                nameof(inner));
        }

        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        _inner = inner;
        _maxBytes = maxBytes;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length =>
        throw new NotSupportedException();

    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        // This is a read-only wrapper, so there is nothing to flush.
    }

    public override int Read(
        byte[] buffer,
        int offset,
        int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var allowedCount = CalculateAllowedReadSize(count);

        if (allowedCount == 0)
        {
            return 0;
        }

        var read = _inner.Read(
            buffer,
            offset,
            allowedCount);

        RecordRead(read);

        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var allowedCount = CalculateAllowedReadSize(buffer.Length);

        if (allowedCount == 0)
        {
            return 0;
        }

        var read = _inner.Read(
            buffer[..allowedCount]);

        RecordRead(read);

        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return ReadAsync(
                buffer.AsMemory(offset, count),
                cancellationToken)
            .AsTask();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var allowedCount = CalculateAllowedReadSize(buffer.Length);

        if (allowedCount == 0)
        {
            return 0;
        }

        var read = await _inner.ReadAsync(
            buffer[..allowedCount],
            cancellationToken);

        RecordRead(read);

        return read;
    }

    public override long Seek(
        long offset,
        SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
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

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
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

    private int CalculateAllowedReadSize(int requestedCount)
    {
        if (requestedCount <= 0)
        {
            return 0;
        }

        var remaining = _maxBytes - _bytesRead;

        // Read one byte beyond the remaining allowance when possible.
        // Doing this lets the wrapper detect that the source exceeds the
        // maximum instead of incorrectly treating an exact-limit read as EOF.
        var detectionWindow = remaining + 1;

        return (int)Math.Min(
            requestedCount,
            Math.Max(1, detectionWindow));
    }

    private void RecordRead(int read)
    {
        _bytesRead += read;

        if (_bytesRead > _maxBytes)
        {
            throw new InvalidSensorAttachmentException(
                $"The attachment exceeds the maximum allowed size of " +
                $"{_maxBytes} bytes.");
        }
    }
}
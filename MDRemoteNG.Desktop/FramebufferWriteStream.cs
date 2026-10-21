namespace MDRemoteNG.Desktop;

/// <summary>Batches only a framebuffer sender's writes; reads never hold the write lock.</summary>
internal sealed class FramebufferWriteStream(Stream inner) : Stream
{
    private readonly object _sync = new();
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _count, _frameThread;
    private bool _disposed;

    internal void BeginFrame()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_frameThread != 0) throw new InvalidOperationException("A framebuffer write is already in progress.");
            _frameThread = Environment.CurrentManagedThreadId;
        }
    }

    internal void EndFrame()
    {
        lock (_sync)
        {
            _frameThread = 0;
            FlushCore();
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_frameThread != Environment.CurrentManagedThreadId)
            {
                FlushCore();
                inner.Write(buffer);
                return;
            }
            while (!buffer.IsEmpty)
            {
                int count = Math.Min(buffer.Length, _buffer.Length - _count);
                buffer[..count].CopyTo(_buffer.AsSpan(_count));
                _count += count;
                buffer = buffer[count..];
                if (_count == _buffer.Length) FlushCore();
            }
        }
    }

    public override void Flush()
    {
        lock (_sync)
        {
            FlushCore();
            inner.Flush();
        }
    }

    private void FlushCore()
    {
        if (_count == 0) return;
        int count = _count;
        _count = 0;
        inner.Write(_buffer, 0, count);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Flush();
        return inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        Flush();
        return inner.Read(buffer);
    }

    protected override void Dispose(bool disposing)
    {
        lock (_sync)
        {
            _disposed = true;
            _count = _frameThread = 0;
        }
        // The authenticated session owns the underlying TLS stream and closes it after its workers stop.
        base.Dispose(disposing);
    }

    public override bool CanRead => !_disposed && inner.CanRead;
    public override bool CanWrite => !_disposed && inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

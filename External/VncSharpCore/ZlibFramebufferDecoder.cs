// MDRemoteNG addition (2026-10-21), distributed under this project's GPL-2.0 license.
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace VncSharpCore
{
    /// <summary>Bounded encoding-6 decompression using the runtime's connection-wide zlib inflater.</summary>
    public sealed class ZlibFramebufferDecoder : IDisposable
    {
        private const int MaximumOutput = 64 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly SegmentedInput input = new SegmentedInput();
        private readonly LimitedOutput output = new LimitedOutput();
        private readonly byte[] inflated = new byte[64 * 1024];
        private readonly ZLibStream inflater;
        private bool ended, failed;
        private int disposed;
        public BinaryReader Reader { get; }

        public ZlibFramebufferDecoder()
        {
            inflater = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
            Reader = new BinaryReader(output);
        }

        public void Decode(Stream source, int expectedBytes)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (expectedBytes < 0 || expectedBytes > MaximumOutput) throw new InvalidDataException("Invalid compressed VNC rectangle size.");
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                if (ended || failed) throw new InvalidDataException("The VNC compression stream cannot continue.");
                try
                {
                    Span<byte> header = stackalloc byte[4];
                    source.ReadExactly(header);
                    uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (length > MaximumOutput + 64 * 1024 || (length == 0 && expectedBytes != 0))
                        throw new InvalidDataException("Invalid compressed VNC payload length.");
                    input.Begin(source, (int)length);
                    output.Reset(expectedBytes);
                    while (output.Length < expectedBytes)
                    {
                        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                        int count = Math.Min(inflated.Length, expectedBytes - (int)output.Length);
                        int read = inflater.Read(inflated, 0, count);
                        if (read == 0) throw new InvalidDataException("Incomplete compressed VNC rectangle.");
                        output.Write(inflated, 0, read);
                    }

                    // Consume the current block's remaining compression metadata and
                    // reject even one output byte beyond this rectangle's declared size.
                    try
                    {
                        if (inflater.Read(inflated, 0, 1) != 0)
                            throw new InvalidDataException("Compressed VNC data exceeds the rectangle.");
                        ended = !input.BoundaryObserved;
                    }
                    // The optional runtime strict-validation setting reports a
                    // temporary input boundary as truncated data. All rectangle
                    // pixels were already decoded, and the persistent inflater
                    // resumes from the next framed block without resetting history.
                    catch (InvalidDataException) when (input.BoundaryObserved) { }
                    if (input.Remaining != 0)
                        throw new InvalidDataException("Trailing data in the VNC compression stream.");
                    output.Position = 0;
                }
                catch { failed = true; throw; }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (gate)
            {
                inflater.Dispose();
                input.Clear();
                Array.Clear(inflated);
                output.ClearAndDispose();
                Reader.Dispose();
            }
        }

        private sealed class SegmentedInput : Stream
        {
            private const int ReservedTail = 16;
            private readonly byte[] tail = new byte[ReservedTail];
            private Stream source;
            private int remaining, tailOffset, tailCount;
            internal bool BoundaryObserved { get; private set; }
            internal int Remaining => remaining;

            internal void Begin(Stream nextSource, int count)
            {
                source = nextSource;
                remaining = count;
                tailOffset = tailCount = 0;
                BoundaryObserved = false;
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
            public override int Read(Span<byte> buffer)
            {
                if (buffer.IsEmpty) return 0;
                if (remaining == 0)
                {
                    BoundaryObserved = true;
                    return 0;
                }
                if (remaining > ReservedTail)
                {
                    int count = Math.Min(buffer.Length, Math.Min(remaining - ReservedTail, 64 * 1024));
                    int read = source.Read(buffer[..count]);
                    if (read == 0) throw new EndOfStreamException("Incomplete compressed VNC payload.");
                    remaining -= read;
                    return read;
                }
                if (tailCount == 0)
                {
                    tailCount = remaining;
                    source.ReadExactly(tail.AsSpan(0, tailCount));
                }
                // Feeding only one byte prevents native read-ahead from hiding bytes
                // after a final Adler checksum. The tail itself needs only one wire read.
                buffer[0] = tail[tailOffset++];
                remaining--;
                return 1;
            }

            internal void Clear() { Array.Clear(tail); source = null; remaining = tailOffset = tailCount = 0; BoundaryObserved = false; }
            public override bool CanRead => true;
            public override bool CanWrite => false;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class LimitedOutput : MemoryStream
        {
            private int limit;
            public void Reset(int length) { Position = 0; SetLength(0); limit = length; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count < 0 || count > limit - Position) throw new InvalidDataException("Compressed VNC data exceeds the rectangle.");
                base.Write(buffer, offset, count);
            }
            public void ClearAndDispose() { GetBuffer().AsSpan().Clear(); Dispose(); }
        }
    }
}

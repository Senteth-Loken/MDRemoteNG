// MDRemoteNG addition (2026-10-21), distributed under this project's GPL-2.0 license.
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using VncSharpCore.zlib.NET;

namespace VncSharpCore
{
    /// <summary>Bounded encoding-6 decompression with connection-wide deflate history.</summary>
    public sealed class ZlibFramebufferDecoder : IDisposable
    {
        private const int MaximumOutput = 64 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly ZStream inflater = new ZStream();
        private readonly LimitedOutput output = new LimitedOutput();
        private readonly byte[] compressed = new byte[64 * 1024], inflated = new byte[64 * 1024];
        private bool ended;
        private bool failed;
        private int disposed;
        public BinaryReader Reader { get; }

        public ZlibFramebufferDecoder()
        {
            if (inflater.inflateInit() != zlibConst.Z_OK) throw new InvalidDataException("Cannot initialize VNC compression.");
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
                    output.Reset(expectedBytes);
                    int remaining = (int)length;
                    while (remaining > 0)
                    {
                        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                        int received = Math.Min(remaining, compressed.Length);
                        source.ReadExactly(compressed.AsSpan(0, received));
                        remaining -= received;
                        inflater.next_in = compressed;
                        inflater.next_in_index = 0;
                        inflater.avail_in = received;
                        bool drain;
                        do
                        {
                            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                            int before = inflater.avail_in;
                            inflater.next_out = inflated;
                            inflater.next_out_index = 0;
                            // Allow one extra byte to detect over-inflation before storing it.
                            inflater.avail_out = (int)Math.Min(inflated.Length, expectedBytes - output.Length + 1);
                            int available = inflater.avail_out;
                            int status = inflater.inflate(zlibConst.Z_SYNC_FLUSH);
                            int produced = available - inflater.avail_out;
                            if (status != zlibConst.Z_OK && status != zlibConst.Z_BUF_ERROR && status != zlibConst.Z_STREAM_END)
                                throw new InvalidDataException("Invalid compressed VNC data.");
                            output.Write(inflated, 0, produced);
                            if (status == zlibConst.Z_STREAM_END)
                            {
                                ended = true;
                                if (inflater.avail_in != 0 || remaining != 0)
                                    throw new InvalidDataException("Trailing data in the VNC compression stream.");
                                break;
                            }
                            if (before == inflater.avail_in && produced == 0)
                            {
                                if (inflater.avail_in != 0) throw new InvalidDataException("VNC decompression made no progress.");
                                break;
                            }
                            drain = inflater.avail_out == 0;
                        } while (inflater.avail_in > 0 || drain);
                    }
                    if (output.Length != expectedBytes) throw new InvalidDataException("Incomplete compressed VNC rectangle.");
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
                inflater.inflateEnd();
                inflater.free();
                Array.Clear(compressed);
                Array.Clear(inflated);
                output.ClearAndDispose();
                Reader.Dispose();
            }
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

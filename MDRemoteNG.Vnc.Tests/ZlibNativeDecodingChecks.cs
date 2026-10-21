using System.Buffers.Binary;
using System.IO.Compression;
using VncSharpCore;

namespace MDRemoteNG.Vnc.Tests;

/// <summary>Guards native inflater read-ahead and bounded rectangle framing.</summary>
internal static class ZlibNativeDecodingChecks
{
    public static int Run()
    {
        int passed = 0;
        foreach (int size in new[] { 0, 1, 4096, 65536 })
        {
            byte[] pixels = new byte[size];
            new Random(201 + size).NextBytes(pixels);
            byte[] finished = Encode(pixels, finish: true);
            using (var decoder = new ZlibFramebufferDecoder())
            using (var source = new CountingStream(Packet(finished).Concat(new byte[] { 0x5A }).ToArray()))
            {
                decoder.Decode(source, size);
                Check(decoder.Reader.ReadBytes(size).SequenceEqual(pixels) && source.ReadByte() == 0x5A,
                    $"A finished {size}-byte rectangle stops exactly before the next RFB message.", ref passed);
            }
            bool rejected = true;
            foreach (int trailing in new[] { 1, 2, 3, 4, 8, 15, 16, 17, 31, 8191, 8192, 65536, 65537 })
            {
                using var decoder = new ZlibFramebufferDecoder();
                byte[] payload = finished.Concat(Enumerable.Repeat((byte)0xA5, trailing)).ToArray();
                using var source = new CountingStream(Packet(payload));
                rejected &= Throws<InvalidDataException>(() => decoder.Decode(source, size));
            }
            Check(rejected,
                $"Trailing bytes after a finished {size}-byte rectangle are rejected inside and beyond native input buffers.", ref passed);
        }

        byte[] data = new byte[4096];
        new Random(37).NextBytes(data);
        byte[] checksum = Encode(data, finish: true);
        checksum[^1] ^= 1;
        using (var decoder = new ZlibFramebufferDecoder())
        using (var source = new CountingStream(Packet(checksum)))
        {
            bool rejected = Throws<InvalidDataException>(() => decoder.Decode(source, data.Length));
            using var next = new CountingStream(Packet(Encode(data, finish: true)));
            Check(rejected && Throws<InvalidDataException>(() => decoder.Decode(next, data.Length)) && next.ReadCalls == 0,
                "An invalid final Adler checksum poisons the decoder before any subsequent packet is read.", ref passed);
        }

        using (var decoder = new ZlibFramebufferDecoder())
        using (var empty = new CountingStream(Packet([])))
        using (var valid = new CountingStream(Packet(Encode(data, finish: false))))
        {
            decoder.Decode(empty, 0);
            decoder.Decode(valid, data.Length);
            bool correct = decoder.Reader.ReadBytes(data.Length).SequenceEqual(data);
            using var rejectedDecoder = new ZlibFramebufferDecoder();
            using var earlierBoundary = new CountingStream(Packet([]));
            using var unexpectedPixels = new CountingStream(Packet(Encode(data, finish: false)));
            rejectedDecoder.Decode(earlierBoundary, 0);
            Check(correct && Throws<InvalidDataException>(() => rejectedDecoder.Decode(unexpectedPixels, 0)),
                "An empty zero-pixel rectangle preserves history and cannot mask over-inflation in the next rectangle.", ref passed);
        }

        byte[] small = data[..1024];
        using (var decoder = new ZlibFramebufferDecoder())
        using (var source = new CountingStream(Packet(Encode(small, finish: false))))
        {
            decoder.Decode(source, small.Length);
            Check(decoder.Reader.ReadBytes(small.Length).SequenceEqual(small) && source.ReadCalls <= 4,
                "Native tail guarding reads a small compressed rectangle from its source in bounded bulk calls.", ref passed);
        }
        return passed;
    }

    private static byte[] Encode(byte[] pixels, bool finish)
    {
        // ZLibStream emits no bytes when no input was ever written. Use a complete
        // empty stream (final empty deflate block and Adler32=1) for end detection.
        if (finish && pixels.Length == 0) return Convert.FromHexString("7801030000000001");
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.Fastest, true))
        {
            compressor.Write(pixels);
            if (!finish) { compressor.Flush(); return output.ToArray(); }
        }
        return output.ToArray();
    }
    private static byte[] Packet(byte[] body)
    {
        byte[] packet = new byte[body.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)body.Length);
        body.CopyTo(packet, 4);
        return packet;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static void Check(bool condition, string message, ref int passed)
    {
        if (!condition) throw new InvalidOperationException(message);
        passed++;
        Console.WriteLine("PASS " + message);
    }

    private sealed class CountingStream(byte[] content) : Stream
    {
        private readonly MemoryStream inner = new(content, writable: false);
        public int ReadCalls { get; private set; }
        public override int Read(Span<byte> buffer) { ReadCalls++; return inner.Read(buffer); }
        public override int Read(byte[] buffer, int offset, int count) { ReadCalls++; return inner.Read(buffer, offset, count); }
        public override int ReadByte() { ReadCalls++; return inner.ReadByte(); }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

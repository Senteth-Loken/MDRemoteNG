using System.Buffers.Binary;
using System.IO.Compression;
using VncSharpCore;

namespace MDRemoteNG.Vnc.Tests;

/// <summary>Checks encoding-6 continuity, fragmented framing and bounded failure paths.</summary>
internal static class ZlibDecodingChecks
{
    public static int Run()
    {
        int passed = 0;
        byte[] first = Data(4096, 12);
        byte[] second = first.ToArray();
        byte[][] dictionaryBlocks = EncodeContinuous(first, second);
        Check(dictionaryBlocks[1].Length < dictionaryBlocks[0].Length / 4,
            "The compression fixture's second rectangle uses the prior rectangle's dictionary.", ref passed);
        foreach (int fragment in new[] { 1, 2, 3 })
        {
            using var decoder = new ZlibFramebufferDecoder();
            bool correct = true;
            for (int index = 0; index < dictionaryBlocks.Length; index++)
            {
                using var source = new FragmentedStream(Packet(dictionaryBlocks[index]).Concat(new byte[] { 0x5A }).ToArray(), fragment);
                decoder.Decode(source, first.Length);
                correct &= decoder.Reader.ReadBytes(first.Length).SequenceEqual(first) && source.ReadByte() == 0x5A;
            }
            Check(correct, $"Continuous Zlib rectangles retain dictionary history across {fragment}-byte reads and consume exactly their declared bytes.", ref passed);
        }
        using (var decoder = new ZlibFramebufferDecoder())
        using (var source = new FragmentedStream(Packet(dictionaryBlocks[1])))
            Check(Throws<InvalidDataException>(() => decoder.Decode(source, second.Length)),
                "A dictionary-dependent rectangle cannot be decoded as a fresh independent stream.", ref passed);

        byte[][] exact = [Data(4096, 21), Data(65536, 22), Data(65539, 23)];
        byte[][] exactBlocks = EncodeContinuous(exact);
        using (var decoder = new ZlibFramebufferDecoder())
            for (int index = 0; index < exact.Length; index++)
            {
                using var source = new FragmentedStream(Packet(exactBlocks[index]));
                decoder.Decode(source, exact[index].Length);
                Check(decoder.Reader.ReadBytes(exact[index].Length).SequenceEqual(exact[index]) && source.MaxRequest <= 65536,
                    $"Zlib decodes an exact {exact[index].Length}-byte rectangle with bounded source reads.", ref passed);
            }

        // Encoding 6 defines a continuous stream, so a frame boundary may split a flush marker.
        using (var decoder = new ZlibFramebufferDecoder())
        {
            byte[] shortFirst = dictionaryBlocks[0][..^2];
            byte[] continuedSecond = dictionaryBlocks[0][^2..].Concat(dictionaryBlocks[1]).ToArray();
            using var source1 = new FragmentedStream(Packet(shortFirst), 2);
            decoder.Decode(source1, first.Length);
            bool correct = decoder.Reader.ReadBytes(first.Length).SequenceEqual(first);
            using var source2 = new FragmentedStream(Packet(continuedSecond), 2);
            decoder.Decode(source2, second.Length);
            Check(correct && decoder.Reader.ReadBytes(second.Length).SequenceEqual(second),
                "A compressed rectangle boundary may split its flush marker without losing stream history.", ref passed);
        }

        foreach (int headerLength in new[] { 0, 1, 2, 3 })
        {
            using var decoder = new ZlibFramebufferDecoder();
            using var source = new FragmentedStream(new byte[headerLength], 1);
            Check(Throws<EndOfStreamException>(() => decoder.Decode(source, first.Length)),
                $"A truncated {headerLength}-byte Zlib length header is rejected.", ref passed);
        }
        using (var decoder = new ZlibFramebufferDecoder())
        using (var source = new FragmentedStream(Packet(dictionaryBlocks[0])[..^1], 3))
            Check(Throws<EndOfStreamException>(() => decoder.Decode(source, first.Length)),
                "A compressed body shorter than its declared length fails explicitly.", ref passed);

        byte[] bomb = EncodeContinuous(new byte[4 * 1024 * 1024])[0];
        using (var decoder = new ZlibFramebufferDecoder())
        using (var source = new FragmentedStream(Packet(bomb)))
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            bool rejected = Throws<InvalidDataException>(() => decoder.Decode(source, 1));
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Check(rejected && allocated < 1024 * 1024,
                "Over-inflation is rejected after bounded output instead of expanding a compressed bomb.", ref passed);
            using var subsequent = new FragmentedStream(Packet(dictionaryBlocks[0]));
            Check(Throws<InvalidDataException>(() => decoder.Decode(subsequent, first.Length)) && subsequent.ReadCalls == 0,
                "A failed compressed rectangle poisons its decoder and rejects later packets before reading their bytes.", ref passed);
        }
        using (var decoder = new ZlibFramebufferDecoder())
        using (var source = new FragmentedStream(Packet(dictionaryBlocks[0]), 3))
            Check(Throws<InvalidDataException>(() => decoder.Decode(source, first.Length + 1)),
                "Compressed data yielding fewer bytes than its rectangle size is rejected.", ref passed);
        foreach (int size in new[] { -1, 64 * 1024 * 1024 + 1 })
        {
            using var decoder = new ZlibFramebufferDecoder();
            using var source = new FragmentedStream([]);
            Check(Throws<InvalidDataException>(() => decoder.Decode(source, size)) && source.ReadCalls == 0,
                $"Invalid decompressed size {size} is rejected before reading or allocating its payload.", ref passed);
        }
        using (var decoder = new ZlibFramebufferDecoder())
        using (var source = new FragmentedStream(LengthHeader(64 * 1024 * 1024u + 65537), 1))
            Check(Throws<InvalidDataException>(() => decoder.Decode(source, 1)) && source.Position == 4,
                "An oversized compressed length is rejected without reading its claimed body.", ref passed);
        using (var decoder = new ZlibFramebufferDecoder())
        using (var source = new FragmentedStream(LengthHeader(0)))
            Check(Throws<InvalidDataException>(() => decoder.Decode(source, 1)),
                "An empty compressed body cannot satisfy a nonempty rectangle.", ref passed);
        using (var decoder = new ZlibFramebufferDecoder())
        using (var source = new FragmentedStream(Packet([0x78, 0x9C]), 1))
            Check(Throws<InvalidDataException>(() => decoder.Decode(source, 1)),
                "An input header that produces no pixels fails without a decompression spin loop.", ref passed);
        using (var decoder = new ZlibFramebufferDecoder())
        using (var source = new FragmentedStream(Packet(new byte[32]), 1))
            Check(Throws<InvalidDataException>(() => decoder.Decode(source, 1)),
                "Invalid deflate data fails through the bounded decoder.", ref passed);

        byte[] finished = EncodeFinished(first);
        using (var decoder = new ZlibFramebufferDecoder())
        {
            using var source = new FragmentedStream(Packet(finished), 3);
            decoder.Decode(source, first.Length);
            bool correct = decoder.Reader.ReadBytes(first.Length).SequenceEqual(first);
            using var next = new FragmentedStream(Packet(dictionaryBlocks[0]));
            Check(correct && Throws<InvalidDataException>(() => decoder.Decode(next, first.Length)) && next.ReadCalls == 0,
                "A finished Zlib stream is accepted once and cannot silently restart for another rectangle.", ref passed);
        }
        using (var decoder = new ZlibFramebufferDecoder())
        using (var source = new FragmentedStream(Packet(finished.Concat(new byte[] { 1, 2, 3 }).ToArray()), 2))
            Check(Throws<InvalidDataException>(() => decoder.Decode(source, first.Length)),
                "Bytes trailing a completed Zlib stream are rejected.", ref passed);
        using (var decoder = new ZlibFramebufferDecoder())
        {
            decoder.Dispose();
            decoder.Dispose();
            using var source = new FragmentedStream(Packet(dictionaryBlocks[0]));
            Check(Throws<ObjectDisposedException>(() => decoder.Decode(source, first.Length)) &&
                  Throws<ObjectDisposedException>(() => decoder.Reader.ReadByte()) && source.ReadCalls == 0,
                "Disposal is idempotent and prevents subsequent decompression and pixel reads.", ref passed);
        }
        return passed;
    }

    private static byte[][] EncodeContinuous(params byte[][] blocks)
    {
        using var sink = new MemoryStream();
        using var compressor = new ZLibStream(sink, CompressionLevel.SmallestSize, true);
        var result = new List<byte[]>();
        foreach (byte[] block in blocks)
        {
            int start = (int)sink.Length;
            compressor.Write(block);
            compressor.Flush();
            result.Add(sink.ToArray()[start..]);
        }
        return result.ToArray();
    }

    private static byte[] EncodeFinished(byte[] content)
    {
        using var sink = new MemoryStream();
        using (var compressor = new ZLibStream(sink, CompressionLevel.SmallestSize, true)) compressor.Write(content);
        return sink.ToArray();
    }

    private static byte[] Data(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
    private static byte[] LengthHeader(uint length)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, length);
        return header;
    }
    private static byte[] Packet(byte[] body) => LengthHeader((uint)body.Length).Concat(body).ToArray();
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static void Check(bool condition, string description, ref int passed)
    {
        if (!condition) throw new InvalidOperationException(description);
        passed++;
        Console.WriteLine("PASS " + description);
    }

    private sealed class FragmentedStream(byte[] content, int maximumFragment = int.MaxValue) : Stream
    {
        private readonly MemoryStream _inner = new(content, false);
        public int ReadCalls { get; private set; }
        public int MaxRequest { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            MaxRequest = Math.Max(MaxRequest, buffer.Length);
            return _inner.Read(buffer[..Math.Min(buffer.Length, maximumFragment)]);
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int ReadByte() { ReadCalls++; return _inner.ReadByte(); }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}

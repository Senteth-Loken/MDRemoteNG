using System.Buffers.Binary;
using System.Drawing.Imaging;
using VncSharpCore;
using VncSharpCore.Encodings;

namespace MDRemoteNG.Vnc.Tests;

/// <summary>Checks bulk Raw decoding without connecting to a desktop or sending input.</summary>
internal static class PixelDecodingChecks
{
    public static int Run()
    {
        int passed = 0;
        foreach (int fragment in new[] { 1, 2, 3 })
        {
            uint[] source = [0x00112233, 0x7FABCDEF, 0xFF010203, 0x00804020, 0x00000000];
            using var stream = new CountingStream(Bytes(source), fragment);
            using var binary = new BinaryReader(stream);
            var reader = new PixelReader32(binary, Frame(5, 1));
            int[] pixels = Enumerable.Repeat(0x12345678, 9).ToArray();
            reader.ReadPixels(pixels, 2, source.Length);
            Check(pixels.Take(2).Concat(pixels.Skip(7)).All(pixel => pixel == 0x12345678) &&
                  pixels.Skip(2).Take(5).Select(pixel => (uint)pixel).SequenceEqual(source.Select(pixel => pixel | 0xFF000000)),
                $"Bulk 32-bit pixels tolerate {fragment}-byte reads, preserve destination offsets and force opaque alpha.", ref passed);
        }

        var shifted = Frame(3, 1, redShift: 0, greenShift: 12, blueShift: 24, redMax: 31, greenMax: 63, blueMax: 15);
        uint[] genericSource = [0x0A02B017, 0xFF03F01F, 0x01001001];
        using (var stream = new CountingStream(Bytes(genericSource), 3))
        using (var binary = new BinaryReader(stream))
        {
            int[] pixels = new int[3];
            new PixelReader32(binary, shifted).ReadPixels(pixels, 0, pixels.Length);
            Check(pixels.Select(pixel => (uint)pixel).SequenceEqual(new uint[] { 0xFF172B0A, 0xFF1F3F0F, 0xFF010101 }),
                "The generic bulk path preserves negotiated channel shifts and masks.", ref passed);
        }

        using (var stream = new CountingStream(Bytes([0x00112233]), 1))
        using (var binary = new BinaryReader(stream))
        {
            var reader = new PixelReader32(binary, Frame(1, 1));
            Check((uint)reader.ReadPixel() == 0xFF112233 && stream.ReadCalls == 4,
                "The single-pixel path also handles fragmented reads exactly.", ref passed);
        }

        using (var binary = new BinaryReader(new CountingStream([0, 0xF8, 0xE0, 7, 0x1F, 0], 1)))
        {
            var frame = Framebuffer.FromPixelFormat(new byte[16], 3, 1, 16, 16);
            int[] pixels = new int[3];
            new PixelReader16(binary, frame).ReadPixels(pixels, 0, pixels.Length);
            Check(pixels.Select(pixel => (uint)pixel).SequenceEqual(new uint[] { 0xFFFF0000, 0xFF00FF00, 0xFF0000FF }),
                "The bulk entry point retains 16-bit RGB565 fallback decoding.", ref passed);
        }
        using (var binary = new BinaryReader(new CountingStream([1, 3, 1], 1)))
        {
            var frame = Framebuffer.FromPixelFormat(new byte[16], 3, 1, 8, 8);
            var protocol = new RfbProtocol();
            protocol.MapEntries[1, 0] = 0x12;
            protocol.MapEntries[1, 1] = 0x34;
            protocol.MapEntries[1, 2] = 0x56;
            protocol.MapEntries[3, 0] = 0xAB;
            protocol.MapEntries[3, 1] = 0xCD;
            protocol.MapEntries[3, 2] = 0xEF;
            int[] pixels = new int[3];
            new PixelReader8(binary, frame, protocol).ReadPixels(pixels, 0, pixels.Length);
            Check(pixels.Select(pixel => (uint)pixel).SequenceEqual(new uint[] { 0xFF123456, 0xFFABCDEF, 0xFF123456 }),
                "The bulk entry point retains 8-bit indexed color-map fallback decoding.", ref passed);
        }

        using (var stream = new CountingStream([]))
        using (var binary = new BinaryReader(stream))
        {
            var reader = new PixelReader32(binary, Frame(1, 1));
            int[] sentinel = [123];
            reader.ReadPixels(sentinel, 1, 0);
            Check(stream.ReadCalls == 0 && sentinel[0] == 123,
                "A zero-count read accepts the destination end without touching the stream.", ref passed);
            Check(Throws<ArgumentNullException>(() => reader.ReadPixels(null!, 0, 0)) &&
                  Throws<ArgumentOutOfRangeException>(() => reader.ReadPixels(sentinel, -1, 1)) &&
                  Throws<ArgumentOutOfRangeException>(() => reader.ReadPixels(sentinel, 0, -1)) &&
                  Throws<ArgumentOutOfRangeException>(() => reader.ReadPixels(sentinel, 1, 1)) &&
                  Throws<ArgumentOutOfRangeException>(() => reader.ReadPixels(sentinel, int.MaxValue, 1)) && stream.ReadCalls == 0,
                "Invalid bulk destinations are rejected before any network bytes are read.", ref passed);
        }

        using (var stream = new CountingStream([1, 2, 3, 4, 5, 6, 7], 2))
        using (var binary = new BinaryReader(stream))
            Check(Throws<EndOfStreamException>(() => new PixelReader32(binary, Frame(2, 1)).ReadPixels(new int[2], 0, 2)),
                "A truncated bulk rectangle is rejected rather than decoding partial pixels.", ref passed);
        using (var stream = new CountingStream([1, 2, 3], 1))
        using (var binary = new BinaryReader(stream))
            Check(Throws<EndOfStreamException>(() => new PixelReader32(binary, Frame(1, 1)).ReadPixel()),
                "A truncated single pixel produces an explicit end-of-stream failure.", ref passed);

        const int fullHdPixels = 1920 * 1080;
        byte[] fullHd = new byte[fullHdPixels * 4];
        for (int index = 0; index < fullHdPixels; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(fullHd.AsSpan(index * 4, 4), 0x00112233);
        using (var stream = new CountingStream(fullHd))
        using (var binary = new BinaryReader(stream))
        {
            var reader = new PixelReader32(binary, Frame(1920, 1080));
            int[] pixels = new int[fullHdPixels];
            reader.ReadPixels(pixels, 0, 1); // Warm the bounded buffer pool without counting setup allocations.
            stream.Position = 0;
            stream.ResetCounts();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            reader.ReadPixels(pixels, 0, pixels.Length);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Check(stream.ReadCalls == (fullHdPixels + 16 * 1024 - 1) / (16 * 1024) && stream.MaxRequested <= 65536 &&
                  pixels.All(pixel => (uint)pixel == 0xFF112233),
                "A Full HD Raw frame uses bounded 64 KiB reads instead of a stream read for every pixel.", ref passed);
            Check(allocated < 150000,
                "A Full HD bulk decode allocates a bounded buffer rather than millions of pixel arrays.", ref passed);
        }

        CheckRawRectangles(ref passed);
        return passed;
    }

    private static void CheckRawRectangles(ref int passed)
    {
        var frame = Frame(9, 7);
        var region = new Rectangle(2, 3, 5, 3);
        uint[] colors = Enumerable.Range(1, region.Width * region.Height).Select(index => (uint)(0x00100000 + index * 0x000101)).ToArray();
        using var stream = new CountingStream(Bytes(colors), 3);
        using var binary = new BinaryReader(stream);
        var protocol = new RfbProtocol();
        typeof(RfbProtocol).GetProperty(nameof(RfbProtocol.Reader))!.SetValue(protocol, binary);
        var rectangle = new RawRectangle(protocol, frame, region);
        rectangle.Decode();
        using var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Magenta);
        rectangle.Draw(bitmap);
        bool matches = true;
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                int expected = region.Contains(x, y) ? (int)(colors[(y - region.Y) * region.Width + x - region.X] | 0xFF000000) : Color.Magenta.ToArgb();
                matches &= bitmap.GetPixel(x, y).ToArgb() == expected;
            }
        Check(matches && stream.Position == colors.Length * 4,
            "Raw drawing preserves odd-width rows, nonzero origins and all pixels outside the update.", ref passed);

        stream.ResetCounts();
        foreach (var invalid in new[]
        {
            new Rectangle(-1, 0, 1, 1), new Rectangle(0, -1, 1, 1), new Rectangle(0, 0, -1, 1),
            new Rectangle(0, 0, 1, -1), new Rectangle(8, 0, 2, 1), new Rectangle(0, 6, 1, 2)
        })
            if (!Throws<InvalidDataException>(() => new RawRectangle(protocol, frame, invalid).Decode()))
                throw new InvalidOperationException("Out-of-bounds Raw rectangle accepted: " + invalid);
        Check(stream.ReadCalls == 0,
            "Raw rectangles outside the framebuffer fail before reading or unsafe bitmap access.", ref passed);

        new RawRectangle(protocol, frame, new Rectangle(3, 2, 0, 3)).Decode();
        Check(stream.ReadCalls == 0, "An empty Raw rectangle consumes no pixel bytes.", ref passed);
        using var truncated = new BinaryReader(new CountingStream([1, 2, 3], 1));
        typeof(RfbProtocol).GetProperty(nameof(RfbProtocol.Reader))!.SetValue(protocol, truncated);
        Check(Throws<EndOfStreamException>(() => new RawRectangle(protocol, frame, new Rectangle(1, 1, 1, 1)).Decode()),
            "Raw rectangle decoding propagates truncated pixel input without drawing it.", ref passed);
    }

    private static Framebuffer Frame(int width, int height, int redShift = 16, int greenShift = 8, int blueShift = 0,
        int redMax = 255, int greenMax = 255, int blueMax = 255)
    {
        byte[] format = [32, 24, 0, 1, (byte)(redMax >> 8), (byte)redMax, (byte)(greenMax >> 8), (byte)greenMax,
            (byte)(blueMax >> 8), (byte)blueMax, (byte)redShift, (byte)greenShift, (byte)blueShift, 0, 0, 0];
        return Framebuffer.FromPixelFormat(format, width, height, 32, 24);
    }

    private static byte[] Bytes(uint[] pixels)
    {
        byte[] bytes = new byte[pixels.Length * 4];
        for (int index = 0; index < pixels.Length; index++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4, 4), pixels[index]);
        return bytes;
    }

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

    private sealed class CountingStream(byte[] bytes, int maximumFragment = int.MaxValue) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, false);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public int ReadCalls { get; private set; }
        public int MaxRequested { get; private set; }
        public void ResetCounts() { ReadCalls = 0; MaxRequested = 0; }
        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            MaxRequested = Math.Max(MaxRequested, buffer.Length);
            return _inner.Read(buffer[..Math.Min(buffer.Length, maximumFragment)]);
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}

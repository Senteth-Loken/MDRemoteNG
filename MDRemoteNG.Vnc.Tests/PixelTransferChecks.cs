using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using RemoteViewing.Vnc;
using VncSharpCore;
using VncSharpCore.Encodings;

namespace MDRemoteNG.Vnc.Tests;

/// <summary>Checks direct pixel transfers using synthetic buffers and memory-only bitmaps.</summary>
internal static class PixelTransferChecks
{
    internal static int Run()
    {
        int passed = 0;
        foreach (int count in new[] { Vector<uint>.Count - 1, Vector<uint>.Count, Vector<uint>.Count + 1,
                     2 * Vector<uint>.Count + 3, 16383, 16384, 16385 })
        {
            uint[] colors = Enumerable.Range(0, count).Select(index => unchecked((uint)(index * 15485863 + 0x12345678))).ToArray();
            using var wire = new FragmentedStream(Bytes(colors), count < 100 ? 3 : 65536);
            using var binary = new BinaryReader(wire);
            int[] destination = Enumerable.Repeat(0x12345678, count + 7).ToArray();
            new PixelReader32(binary, Frame(count, 1)).ReadPixels(destination, 3, count);
            Check(destination.Skip(3).Take(count).Select(pixel => (uint)pixel).SequenceEqual(colors.Select(pixel => pixel | 0xFF000000)) &&
                  destination.Take(3).Concat(destination.Skip(count + 3)).All(pixel => pixel == 0x12345678) && wire.MaximumRead <= 65536,
                $"Direct32 transfer handles {count} pixels, fragmented input, vector tails and an unaligned destination without changing sentinels.", ref passed);
        }

        using (var binary = new BinaryReader(new MemoryStream(Bytes([0x00112233, 0x7FABCDEF]))))
        {
            int[] pixels = new int[2];
            new PixelReader32(binary, Frame(2, 1, swapRedBlue: true)).ReadPixels(pixels, 0, 2);
            Check(pixels.Select(pixel => (uint)pixel).SequenceEqual(new uint[] { 0xFF332211, 0xFFEFCDAB }),
                "A negotiated opposite red/blue layout retains the generic channel-conversion path.", ref passed);
        }

        foreach (bool negativeStride in new[] { false, true })
            CheckPaddedBitmap(negativeStride, ref passed);
        CheckContiguousBitmap(ref passed);

        var invalid = new RawRectangle(new RfbProtocol(), Frame(3, 3), new Rectangle(-1, 0, 2, 2));
        using (var bitmap = new Bitmap(3, 3, PixelFormat.Format32bppArgb))
        {
            Check(Throws<InvalidDataException>(() => invalid.Draw(bitmap)),
                "Bitmap transfer rejects an invalid destination rectangle before unsafe access.", ref passed);
            new RawRectangle(new RfbProtocol(), Frame(3, 3), new Rectangle(3, 0, 0, 3)).Draw(bitmap);
            Check(bitmap.GetPixel(0, 0).ToArgb() == 0, "An empty bitmap transfer leaves every pixel untouched.", ref passed);
        }

        CheckServerCopies(ref passed);
        return passed;
    }

    private static void CheckPaddedBitmap(bool negativeStride, ref int passed)
    {
        const int width = 11, height = 8, stride = 64, guard = 16;
        IntPtr allocation = Marshal.AllocHGlobal(stride * height + guard * 2);
        try
        {
            byte[] initial = Enumerable.Repeat((byte)0xA5, stride * height + guard * 2).ToArray();
            Marshal.Copy(initial, 0, allocation, initial.Length);
            IntPtr scan0 = IntPtr.Add(allocation, guard + (negativeStride ? (height - 1) * stride : 0));
            using var bitmap = new Bitmap(width, height, negativeStride ? -stride : stride, PixelFormat.Format32bppArgb, scan0);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Magenta);
            var region = new Rectangle(3, 2, 5, 3);
            uint[] colors = Enumerable.Range(1, region.Width * region.Height).Select(value => (uint)(0x00112200 + value)).ToArray();
            var rectangle = Decode(width, height, region, colors);
            rectangle.Draw(bitmap);
            bool matches = Matches(bitmap, region, colors);
            byte[] after = new byte[initial.Length];
            Marshal.Copy(allocation, after, 0, after.Length);
            matches &= after.Take(guard).Concat(after.Skip(after.Length - guard)).All(value => value == 0xA5);
            Check(matches, $"Bulk bitmap rows preserve a padded {(negativeStride ? "negative" : "positive")} stride, nonzero origin and outside-ROI guards.", ref passed);
        }
        finally { Marshal.FreeHGlobal(allocation); }
    }

    private static void CheckContiguousBitmap(ref int passed)
    {
        const int width = 17, height = 7;
        var region = new Rectangle(0, 2, width, 3);
        uint[] colors = Enumerable.Range(0, region.Width * region.Height).Select(value => (uint)(0x0012AB00 + value)).ToArray();
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Magenta);
        Decode(width, height, region, colors).Draw(bitmap);
        Check(Matches(bitmap, region, colors), "Contiguous full-width bitmap transfers preserve rows above and below a partial-height update.", ref passed);
    }

    private static void CheckServerCopies(ref int passed)
    {
        MethodInfo copy = typeof(VncPixelFormat).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name == "Copy" && method.GetParameters().Length == 9 && method.GetParameters()[0].ParameterType == typeof(byte[]));
        foreach (var format in new[] { VncPixelFormat.Format8bpp, new VncPixelFormat(16, 16, 5, 11, 6, 5, 5, 0), VncPixelFormat.Format32bpp })
        {
            int bpp = format.BytesPerPixel, sourceStride = 13 * bpp + 8, targetStride = 17 * bpp + 12;
            byte[] source = Enumerable.Range(0, sourceStride * 8).Select(index => (byte)(index * 31)).ToArray();
            byte[] target = Enumerable.Repeat((byte)0xA5, targetStride * 9).ToArray(), expected = target.ToArray();
            var region = new VncRectangle(2, 3, 5, 3);
            for (int row = 0; row < region.Height; row++)
                source.AsSpan((region.Y + row) * sourceStride + region.X * bpp, region.Width * bpp)
                    .CopyTo(expected.AsSpan((2 + row) * targetStride + 4 * bpp));
            copy.Invoke(null, new object[] { source, sourceStride, format, region, target, targetStride, format, 4, 2 });
            Check(target.SequenceEqual(expected), $"Server equal-format {bpp * 8}-bit copies preserve cropped origins, distinct strides and padding.", ref passed);

            byte[] overlapping = source.ToArray(), overlapExpected = source.ToArray();
            Array.Copy(source, sourceStride + bpp, overlapExpected, sourceStride + 3 * bpp, 7 * bpp);
            copy.Invoke(null, new object[] { overlapping, sourceStride, format, new VncRectangle(1, 1, 7, 1), overlapping, sourceStride, format, 3, 1 });
            Check(overlapping.SequenceEqual(overlapExpected), $"Server equal-format {bpp * 8}-bit row copies retain overlapping source bytes.", ref passed);
        }
    }

    private static RawRectangle Decode(int width, int height, Rectangle region, uint[] colors)
    {
        using var binary = new BinaryReader(new MemoryStream(Bytes(colors)));
        var protocol = new RfbProtocol();
        typeof(RfbProtocol).GetProperty(nameof(RfbProtocol.Reader))!.SetValue(protocol, binary);
        var rectangle = new RawRectangle(protocol, Frame(width, height), region);
        rectangle.Decode();
        return rectangle;
    }
    private static bool Matches(Bitmap bitmap, Rectangle region, uint[] colors)
    {
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).ToArgb() != (region.Contains(x, y)
                        ? (int)(colors[(y - region.Y) * region.Width + x - region.X] | 0xFF000000)
                        : Color.Magenta.ToArgb())) return false;
        return true;
    }
    private static VncSharpCore.Framebuffer Frame(int width, int height, bool swapRedBlue = false)
    {
        byte[] format = [32, 24, 0, 1, 0, 255, 0, 255, 0, 255, (byte)(swapRedBlue ? 0 : 16), 8, (byte)(swapRedBlue ? 16 : 0), 0, 0, 0];
        return VncSharpCore.Framebuffer.FromPixelFormat(format, width, height, 32, 24);
    }
    private static byte[] Bytes(uint[] colors)
    {
        byte[] bytes = new byte[colors.Length * 4];
        for (int index = 0; index < colors.Length; index++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), colors[index]);
        return bytes;
    }
    private static bool Throws<T>(Action action) where T : Exception
    { try { action(); return false; } catch (T) { return true; } }
    private static void Check(bool condition, string description, ref int passed)
    { if (!condition) throw new InvalidOperationException(description); passed++; Console.WriteLine("PASS " + description); }
    private sealed class FragmentedStream(byte[] bytes, int fragment) : Stream
    {
        private readonly MemoryStream inner = new(bytes, false);
        internal int MaximumRead;
        public override int Read(Span<byte> buffer) { MaximumRead = Math.Max(MaximumRead, buffer.Length); return inner.Read(buffer[..Math.Min(buffer.Length, fragment)]); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

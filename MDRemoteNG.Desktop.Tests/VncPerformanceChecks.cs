using MDRemoteNG.Desktop;
using RemoteViewing.Vnc;
using RemoteViewing.Vnc.Server;

internal static class VncPerformanceChecks
{
    internal static async Task<int> RunAsync()
    {
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine("PASS " + message);
            passed++;
        }
        static FramebufferUpdateRequest Request(VncFramebuffer frame, bool incremental = true) =>
            new(incremental, new VncRectangle(0, 0, frame.Width, frame.Height));
        static void Paint(VncFramebuffer frame, int x, int y, int width, int height, int color)
        {
            for (int row = y; row < y + height; row++)
                frame.GetPixels().AsSpan(row * frame.Width + x, width).Fill(color);
        }
        static bool Is(VncRectangle value, int x, int y, int width, int height) =>
            value.X == x && value.Y == y && value.Width == width && value.Height == height;

        var cache = new DirtyFramebufferCache();
        var frame = new VncFramebuffer("Synthetic Full HD", 1920, 1080);
        Check(cache.FindChanges(frame, Request(frame, false)) is { Count: 1 } first && Is(first[0], 0, 0, 1920, 1080),
            "The initial Full HD framebuffer is delivered as one rectangle.");
        Check(cache.FindChanges(frame, Request(frame)).Count == 0, "An unchanged frame does not produce viewer callbacks or pixels.");
        Paint(frame, 0, 0, frame.Width, frame.Height, 0x123456);
        var all = cache.FindChanges(frame, Request(frame));
        Check(all.Count == 1 && Is(all[0], 0, 0, frame.Width, frame.Height),
            "A complete Full HD change coalesces all 8,160 dirty tiles into one rectangle.");
        Paint(frame, 32, 48, 64, 64, 0xABCDEF);
        var patch = cache.FindChanges(frame, Request(frame));
        Check(patch.Count == 1 && Is(patch[0], 32, 48, 64, 64), "A 64 by 64 patch merges horizontally and vertically into one rectangle.");
        Paint(frame, 0, 0, 16, 16, 1);
        Paint(frame, 1904, 1056, 16, 16, 2);
        var sparse = cache.FindChanges(frame, Request(frame));
        Check(sparse.Count == 2 && Is(sparse[0], 0, 0, 16, 16) && Is(sparse[1], 1904, 1056, 16, 16),
            "Distant small changes remain separate without sending their unchanged bounding area.");
        Check(cache.FindChanges(frame, Request(frame, false)).Count == 1,
            "A nonincremental request still returns pixels even when the cache matches.");

        var odd = new VncFramebuffer("Odd dimensions", 37, 35);
        cache.FindChanges(odd, Request(odd, false));
        Paint(odd, 0, 0, 37, 35, 99);
        var edge = cache.FindChanges(odd, Request(odd));
        Check(edge.Count == 1 && Is(edge[0], 0, 0, 37, 35), "Odd edge tiles merge without exceeding framebuffer bounds.");
        var cropped = new DirtyFramebufferCache();
        var crop = new FramebufferUpdateRequest(false, new VncRectangle(5, 7, 17, 19));
        var initialCrop = cropped.FindChanges(odd, crop);
        Check(initialCrop.Count == 1 && Is(initialCrop[0], 5, 7, 17, 19), "A nonzero initial crop returns exactly the requested pixels.");
        Check(cropped.FindChanges(odd, new FramebufferUpdateRequest(true, crop.Region)).Count == 0,
            "A repeated unchanged crop is suppressed even when it cuts across tile and bitset boundaries.");
        var unseen = cropped.FindChanges(odd, new FramebufferUpdateRequest(true, new VncRectangle(24, 7, 13, 19)));
        Check(unseen.Count == 1 && Is(unseen[0], 24, 7, 13, 19),
            "Pixels outside a previous crop are sent on their first incremental request.");
        Paint(odd, 5, 7, 17, 19, 101);
        var changedCrop = cropped.FindChanges(odd, new FramebufferUpdateRequest(true, crop.Region));
        Check(changedCrop.Count == 1 && Is(changedCrop[0], 5, 7, 17, 19), "Changed crop output remains clipped to its nonzero request.");
        var clipped = new DirtyFramebufferCache().FindChanges(odd,
            new FramebufferUpdateRequest(false, new VncRectangle(30, 29, 30, 30)));
        Check(clipped.Count == 1 && Is(clipped[0], 30, 29, 7, 6), "Requests crossing the framebuffer edge are safely clipped.");
        Check(cropped.FindChanges(odd, new FramebufferUpdateRequest(true, new VncRectangle(100, 100, 10, 10))).Count == 0,
            "A request wholly outside the framebuffer produces no rectangles.");

        var fragmented = new VncFramebuffer("Fragmented changes", 256, 256);
        var fragmentedCache = new DirtyFramebufferCache();
        fragmentedCache.FindChanges(fragmented, Request(fragmented, false));
        for (int y = 0; y < 256; y += 16)
            for (int x = 0; x < 256; x += 16)
                if (((x / 16 + y / 16) & 1) == 0) Paint(fragmented, x, y, 16, 16, 1);
        var fallback = fragmentedCache.FindChanges(fragmented, Request(fragmented));
        Check(fallback.Count == 1 && Is(fallback[0], 0, 0, 256, 256),
            "More than 64 fragmented regions fall back to a bounded single update.");
        Check(fragmentedCache.FindChanges(fragmented, Request(fragmented)).Count == 0,
            "A fallback update also advances the entire dirty-cache snapshot.");
        var resized = new VncFramebuffer("Resized", 65, 33);
        var changedSize = cache.FindChanges(resized, Request(resized));
        Check(changedSize.Count == 1 && Is(changedSize[0], 0, 0, 65, 33), "A changed framebuffer size resets the cache and sends its new extent.");

        using (var inner = new RecordingStream())
        using (var output = new FramebufferWriteStream(inner))
        {
            output.Write([1, 2, 3]);
            Check(inner.Writes.Count == 1, "Authentication and other nonframe writes pass through immediately.");
            output.BeginFrame();
            for (int i = 0; i < 100; i++) output.Write([4, 5, 6]);
            Check(inner.Writes.Count == 1, "Small framebuffer writes stay buffered until the frame finishes.");
            output.EndFrame();
            Check(inner.Writes.Count == 2 && inner.Writes[1].Length == 300,
                "A completed frame flushes its tiny RFB writes as one TLS write.");
            output.BeginFrame();
            output.EndFrame();
            output.Write([7]);
            Check(inner.Writes.Count == 3 && inner.Writes[2][0] == 7,
                "A no-change frame exits batch mode and cannot hold later messages.");
        }
        using (var inner = new RecordingStream())
        using (var output = new FramebufferWriteStream(inner))
        {
            byte[] expected = Enumerable.Range(0, 140_000).Select(index => (byte)index).ToArray();
            output.BeginFrame();
            output.Write(expected);
            output.EndFrame();
            Check(inner.Writes.Count == 3 && inner.Writes.All(write => write.Length <= 65536) &&
                  inner.Writes.SelectMany(write => write).SequenceEqual(expected),
                "Large frames spill in bounded 64 KiB writes without losing or reordering bytes.");
            output.BeginFrame();
            output.Write([8, 9]);
            byte[] received = new byte[1];
            output.ReadExactly(received);
            Check(inner.Writes.Last().SequenceEqual(new byte[] { 8, 9 }) && received[0] == 42,
                "Reading flushes pending protocol output before delegating to the underlying stream.");
            output.EndFrame();
        }
        using (var inner = new RecordingStream { BlockRead = true })
        using (var output = new FramebufferWriteStream(inner))
        {
            Task reader = Task.Run(() => output.ReadExactly(new byte[1]));
            if (!inner.ReadStarted.Wait(TimeSpan.FromSeconds(2))) throw new Exception("Synthetic read did not start.");
            try
            {
                await Task.Run(() => output.Write(new byte[] { 10 })).WaitAsync(TimeSpan.FromSeconds(2));
                Check(inner.Writes.Count == 1, "A blocked receive does not hold the framebuffer write lock.");
            }
            finally { inner.AllowRead.Set(); }
            await reader;
        }
        using (var inner = new RecordingStream())
        {
            using (var output = new FramebufferWriteStream(inner)) output.Flush();
            inner.Write([11]);
            Check(inner.Writes.Count == 1, "Disposing the write buffer preserves the host's ownership of the TLS stream.");
        }
        return passed;
    }

    private sealed class RecordingStream : Stream
    {
        public List<byte[]> Writes { get; } = [];
        public bool BlockRead;
        public ManualResetEventSlim ReadStarted { get; } = new(false);
        public ManualResetEventSlim AllowRead { get; } = new(false);
        public override void Write(byte[] buffer, int offset, int count) => Writes.Add(buffer.AsSpan(offset, count).ToArray());
        public override void Write(ReadOnlySpan<byte> buffer) => Writes.Add(buffer.ToArray());
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadStarted.Set();
            if (BlockRead) AllowRead.Wait();
            if (count == 0) return 0;
            buffer[offset] = 42;
            return 1;
        }
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

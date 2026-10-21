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
        static bool ApplyAndMatches(VncFramebuffer source, IReadOnlyList<VncRectangle> changes, int[] displayed)
        {
            foreach (var change in changes)
                for (int y = change.Y; y < change.Y + change.Height; y++)
                {
                    int offset = y * source.Width + change.X;
                    source.GetPixels().AsSpan(offset, change.Width).CopyTo(displayed.AsSpan(offset, change.Width));
                }
            return displayed.AsSpan().SequenceEqual(source.GetPixels());
        }

        var pacing = new VncFramePacing();
        Check(pacing.TargetRate == 60, "A new authenticated stream targets 60 updates per second instead of imposing a 125 ms wait.");
        pacing.RecordCapture(TimeSpan.FromMilliseconds(3));
        pacing.RecordScan(TimeSpan.FromMilliseconds(2));
        Check(pacing.TargetRate == 60, "Low-cost capture and dirty scanning retain the responsive update target.");
        pacing.RecordCapture(TimeSpan.FromMilliseconds(12));
        pacing.RecordScan(TimeSpan.FromMilliseconds(8));
        Check(pacing.TargetRate == 20, "An expensive capture plus scan backs off to a 40 percent single-core work budget.");
        pacing.RecordCapture(TimeSpan.FromMilliseconds(2));
        pacing.RecordScan(TimeSpan.FromMilliseconds(1));
        Check(pacing.TargetRate > 20 && pacing.TargetRate < 60, "Faster capture work recovers gradually instead of oscillating between update rates.");
        for (int i = 0; i < 20; i++)
        {
            pacing.RecordCapture(TimeSpan.FromMilliseconds(2));
            pacing.RecordScan(TimeSpan.FromMilliseconds(1));
        }
        Check(pacing.TargetRate == 60, "Sustained inexpensive work restores the maximum interactive rate.");
        pacing.RecordCapture(TimeSpan.FromSeconds(2));
        Check(pacing.TargetRate == 1, "Very slow capture work clamps to a finite supported minimum rate.");

        var timerPacing = new VncFramePacing();
        timerPacing.RecordCapture(TimeSpan.FromMilliseconds(50)); // Eight Hz makes the one-shot checks independent of tiny timer jitter.
        using var timerStop = new CancellationTokenSource();
        var timerSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int signals = 0;
        using (var clock = new VncCaptureClock(timerPacing, timerStop.Token, () =>
               { Interlocked.Increment(ref signals); timerSignal.TrySetResult(); }))
        {
            Check(clock.TryBeginCapture() && clock.Generation == 1 && clock.ScheduledWakeCount == 0,
                "The first capture starts immediately without arming an idle polling timer.");
            bool deferred = true;
            for (int i = 0; i < 100; i++) deferred &= !clock.TryBeginCapture();
            Check(deferred && clock.Generation == 1 && clock.ScheduledWakeCount == 1,
                "Early source calls return immediately and share one scheduled wake instead of blocking the request receiver.");
            await timerSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(signals == 1 && clock.TryBeginCapture() && clock.Generation == 2,
                "A private timer wakes the public framebuffer signal and admits one fresh capture.");
            await Task.Delay(300);
            Check(clock.TryBeginCapture() && clock.Generation == 3 && !clock.TryBeginCapture(),
                "Missed capture deadlines produce one current capture instead of a catch-up queue.");
            clock.Dispose();
            int beforeDispose = Volatile.Read(ref signals);
            await Task.Delay(150);
            Check(signals == beforeDispose, "Disposing the session clock prevents later scheduled framebuffer wakeups.");
        }
        using (var clock = new VncCaptureClock(new VncFramePacing(), timerStop.Token, () => { }))
        {
            timerStop.Cancel();
            bool stopped = false;
            try { clock.TryBeginCapture(); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && clock.Generation == 0, "Host cancellation prevents capture without waiting for a pacing deadline.");
        }

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

        var knownFrame = new VncFramebuffer("Known fast path", 64, 64);
        var knownCache = new DirtyFramebufferCache();
        knownCache.FindChanges(knownFrame, Request(knownFrame, false));
        knownFrame.SetPixel(7, 31, 1);
        var lastRow = knownCache.FindChanges(knownFrame, Request(knownFrame));
        Check(lastRow.Count == 1 && Is(lastRow[0], 0, 16, 16, 16),
            "The known-frame fast path detects a changed last tile row after all earlier rows match.");
        knownFrame.SetPixel(1, 1, 2);
        knownFrame.SetPixel(49, 49, 3);
        var distinct = knownCache.FindChanges(knownFrame, Request(knownFrame));
        Check(distinct.Count == 2 && Is(distinct[0], 0, 0, 16, 16) && Is(distinct[1], 48, 48, 16, 16),
            "The fast path compares separate changed tiles against the same prior snapshot before bulk-copying pixels.");
        knownFrame.SetPixel(7, 31, 0);
        var reverted = knownCache.FindChanges(knownFrame, Request(knownFrame));
        Check(reverted.Count == 1 && Is(reverted[0], 0, 16, 16, 16) && knownCache.FindChanges(knownFrame, Request(knownFrame)).Count == 0,
            "Reverted pixels are delivered once and the bulk snapshot suppresses their next unchanged update.");
        var resizeCrop = new VncFramebuffer("Resize then crop", 32, 32);
        knownCache.FindChanges(resizeCrop, new FramebufferUpdateRequest(false, new VncRectangle(1, 1, 4, 4)));
        var afterResizeCrop = knownCache.FindChanges(resizeCrop, Request(resizeCrop));
        Check(afterResizeCrop.Count == 1 && Is(afterResizeCrop[0], 0, 0, 32, 32),
            "Resizing clears whole-frame knowledge so an initial crop cannot hide unseen pixels in a later full request.");

        var separatedFrame = new VncFramebuffer("Separated changed row bands", 65, 65);
        var separatedCache = new DirtyFramebufferCache();
        var separatedDisplay = new int[65 * 65];
        separatedCache.FindChanges(separatedFrame, Request(separatedFrame, false));
        separatedFrame.SetPixel(33, 1, 7);
        separatedFrame.SetPixel(33, 49, 9);
        var separatedBands = separatedCache.FindChanges(separatedFrame, Request(separatedFrame));
        Check(separatedBands.Count == 2 && Is(separatedBands[0], 32, 0, 16, 16) && Is(separatedBands[1], 32, 48, 16, 16) &&
              ApplyAndMatches(separatedFrame, separatedBands, separatedDisplay),
            "An unchanged row-band gap cannot merge equal-X dirty runs or omit pixels from the reconstructed display.");
        separatedFrame.SetPixel(64, 64, 11);
        var lastBand = separatedCache.FindChanges(separatedFrame, Request(separatedFrame));
        Check(lastBand.Count == 1 && Is(lastBand[0], 64, 64, 1, 1) &&
              ApplyAndMatches(separatedFrame, lastBand, separatedDisplay),
            "The last partial row band and odd right edge remain visible after all preceding bands match.");
        separatedFrame.SetPixel(33, 1, 0);
        var restoredBand = separatedCache.FindChanges(separatedFrame, Request(separatedFrame));
        Check(restoredBand.Count == 1 && ApplyAndMatches(separatedFrame, restoredBand, separatedDisplay) &&
              separatedCache.FindChanges(separatedFrame, Request(separatedFrame)).Count == 0,
            "Remembering only returned rectangles delivers a reverted band once and preserves the rest of the display snapshot.");

        var cropFrame = new VncFramebuffer("Known nonzero crop", 37, 35);
        var cropCache = new DirtyFramebufferCache();
        cropCache.FindChanges(cropFrame, Request(cropFrame, false));
        cropFrame.SetPixel(21, 25, 13);
        cropFrame.SetPixel(30, 8, 17);
        var cropRequest = new FramebufferUpdateRequest(true, new VncRectangle(5, 7, 17, 19));
        var cropOnly = cropCache.FindChanges(cropFrame, cropRequest);
        var outsideCrop = cropCache.FindChanges(cropFrame, Request(cropFrame));
        Check(cropOnly.Count == 1 && Is(cropOnly[0], 21, 23, 1, 3) &&
              outsideCrop.Count == 1 && Is(outsideCrop[0], 16, 0, 16, 16),
            "A known partial crop keeps precise tile geometry and cannot remember an unsent change outside its requested pixels.");

        var random = new Random(20261021);
        var reconstructionCache = new DirtyFramebufferCache();
        var reconstructionFrame = new VncFramebuffer("Seeded reconstructed display", 73, 51);
        int[] displayed = new int[73 * 51];
        bool[] seen = new bool[displayed.Length];
        for (int step = 0; step < 200; step++)
        {
            if (step > 0 && step % 31 == 0)
            {
                int width = reconstructionFrame.Width == 73 ? 37 : 73;
                int height = width == 73 ? 51 : 35;
                reconstructionFrame = new VncFramebuffer("Seeded resized display", width, height);
                displayed = new int[width * height];
                seen = new bool[displayed.Length];
            }
            for (int mutation = 0; mutation < 5; mutation++)
                reconstructionFrame.SetPixel(random.Next(reconstructionFrame.Width), random.Next(reconstructionFrame.Height),
                    step % 17 == 0 ? 0 : step + 1);
            int x = step % 3 == 0 ? 0 : random.Next(reconstructionFrame.Width);
            int y = step % 3 == 0 ? 0 : random.Next(reconstructionFrame.Height);
            int widthRemaining = reconstructionFrame.Width - x, heightRemaining = reconstructionFrame.Height - y;
            var requestRegion = new VncRectangle(x, y, step % 3 == 0 ? widthRemaining : random.Next(1, widthRemaining + 1),
                step % 3 == 0 ? heightRemaining : random.Next(1, heightRemaining + 1));
            var reconstructionRequest = new FramebufferUpdateRequest(step % 11 != 0, requestRegion);
            var regions = reconstructionCache.FindChanges(reconstructionFrame, reconstructionRequest);
            if (regions.Count > 64) throw new Exception("Seeded updates exceeded the rectangle limit.");
            var covered = new bool[displayed.Length];
            foreach (var region in regions)
            {
                if (region.X < requestRegion.X || region.Y < requestRegion.Y ||
                    region.X + region.Width > requestRegion.X + requestRegion.Width ||
                    region.Y + region.Height > requestRegion.Y + requestRegion.Height)
                    throw new Exception("Seeded update escaped its requested crop.");
                for (int row = region.Y; row < region.Y + region.Height; row++)
                    for (int column = region.X; column < region.X + region.Width; column++)
                    {
                        int offset = row * reconstructionFrame.Width + column;
                        covered[offset] = true;
                    }
            }
            for (int row = requestRegion.Y; row < requestRegion.Y + requestRegion.Height; row++)
                for (int column = requestRegion.X; column < requestRegion.X + requestRegion.Width; column++)
                {
                    int offset = row * reconstructionFrame.Width + column;
                    int expectedPixel = reconstructionFrame.GetPixels()[offset];
                    if ((!reconstructionRequest.Incremental || !seen[offset] || displayed[offset] != expectedPixel) && !covered[offset])
                        throw new Exception($"Seeded display reconstruction missed a requested pixel at step {step}.");
                    if (covered[offset]) { displayed[offset] = expectedPixel; seen[offset] = true; }
                }
            if (reconstructionCache.FindChanges(reconstructionFrame, new FramebufferUpdateRequest(true, requestRegion)).Count != 0)
                throw new Exception("A reconstructed region was reported dirty again without any pixel changes.");
        }
        Check(true, "Two hundred seeded incremental, forced, cropped and resized updates reconstruct every requested pixel without forgetting unsent changes.");

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

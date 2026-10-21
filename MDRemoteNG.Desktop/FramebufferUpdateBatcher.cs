using System.Diagnostics;
using RemoteViewing.Vnc;
using RemoteViewing.Vnc.Server;

namespace MDRemoteNG.Desktop;

/// <summary>Combines adjacent changed tiles before encoding and dispatching them to the viewer.</summary>
internal sealed class FramebufferUpdateBatcher(VncFramePacing? pacing = null, VncCaptureClock? clock = null)
{
    private readonly DirtyFramebufferCache _cache = new();
    private long _lastGeneration;

    internal void Send(VncServerSession session, FramebufferUpdatingEventArgs args, FramebufferWriteStream output)
    {
        args.Handled = true;
        VncFramebuffer frame = session.Framebuffer;
        FramebufferUpdateRequest? request = session.FramebufferUpdateRequest;
        if (frame == null || request == null) return;
        long generation = clock?.Generation ?? 0;
        if (clock != null && generation == _lastGeneration) return;
        lock (frame.SyncRoot)
        {
            long scanStarted = Stopwatch.GetTimestamp();
            var changes = _cache.FindChanges(frame, request);
            pacing?.RecordScan(Stopwatch.GetElapsedTime(scanStarted));
            _lastGeneration = generation;
            output.BeginFrame();
            try
            {
                session.FramebufferManualBeginUpdate();
                foreach (var region in changes) session.FramebufferManualInvalidate(region);
                args.SentChanges = session.FramebufferManualEndUpdate();
            }
            finally { output.EndFrame(); }
        }
    }
}

internal sealed class DirtyFramebufferCache
{
    private const int TileSize = 16, MaxRectangles = 64, MaxPixels = 16_777_216;
    private int _width, _height;
    private bool _allKnown;
    private int[] _pixels = [];
    private ulong[] _known = [];

    internal IReadOnlyList<VncRectangle> FindChanges(VncFramebuffer frame, FramebufferUpdateRequest request)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || (long)frame.Width * frame.Height > MaxPixels)
            throw new IOException("The shared display size is unsupported.");
        var region = VncRectangle.Intersect(request.Region, new VncRectangle(0, 0, frame.Width, frame.Height));
        if (region.IsEmpty) return [];
        lock (frame.SyncRoot)
        {
            bool reset = _width != frame.Width || _height != frame.Height;
            if (reset)
            {
                _width = frame.Width; _height = frame.Height;
                _allKnown = false;
                _pixels = new int[checked(_width * _height)];
                _known = new ulong[(_pixels.Length + 63) / 64];
            }
            int[] current = frame.GetPixels();
            if (reset || !request.Incremental)
            {
                Remember(current, region);
                return [region];
            }
            if (_allKnown && IsFullFrame(region) && current.AsSpan().SequenceEqual(_pixels)) return [];

            var rectangles = new List<VncRectangle>();
            var previous = new Dictionary<(int X, int Width), int>();
            VncRectangle bounds = default;
            bool bounded = false, collapse = false;
            int right = region.X + region.Width, bottom = region.Y + region.Height;
            for (int y = region.Y; y < bottom; y += TileSize)
            {
                int height = Math.Min(TileSize, bottom - y), runStart = -1;
                if (_allKnown && region.X == 0 && region.Width == _width &&
                    IsRowBandUnchanged(current, y, height))
                {
                    // Runs on opposite sides of an unchanged band are not adjacent.
                    previous.Clear();
                    continue;
                }
                var next = new Dictionary<(int X, int Width), int>();
                for (int x = region.X; x < right; x += TileSize)
                {
                    int width = Math.Min(TileSize, right - x);
                    var tile = new VncRectangle(x, y, width, height);
                    bool changed = IsChanged(current, tile);
                    if (changed)
                    {
                        bounds = bounded ? VncRectangle.Union(bounds, tile) : tile;
                        bounded = true;
                        if (runStart < 0) runStart = x;
                    }
                    if (runStart >= 0 && (!changed || x + width == right))
                    {
                        int runRight = changed ? x + width : x;
                        if (!collapse)
                        {
                            var key = (runStart, runRight - runStart);
                            if (previous.TryGetValue(key, out int index))
                            {
                                var old = rectangles[index];
                                rectangles[index] = new VncRectangle(old.X, old.Y, old.Width, old.Height + height);
                            }
                            else
                            {
                                index = rectangles.Count;
                                rectangles.Add(new VncRectangle(runStart, y, runRight - runStart, height));
                                if (rectangles.Count > MaxRectangles) collapse = true;
                            }
                            if (!collapse) next[key] = index;
                        }
                        runStart = -1;
                    }
                }
                previous = next;
            }
            IReadOnlyList<VncRectangle> changes = collapse ? [bounds] : rectangles;
            if (_allKnown)
                foreach (var change in changes) Remember(current, change);
            else if (IsFullFrame(region)) _allKnown = true;
            return changes;
        }
    }

    private bool IsRowBandUnchanged(int[] current, int y, int height)
    {
        int offset = y * _width, count = height * _width;
        return current.AsSpan(offset, count).SequenceEqual(_pixels.AsSpan(offset, count));
    }

    private bool IsChanged(int[] current, VncRectangle tile)
    {
        if (_allKnown)
        {
            for (int y = tile.Y; y < tile.Y + tile.Height; y++)
            {
                int offset = y * _width + tile.X;
                if (!current.AsSpan(offset, tile.Width).SequenceEqual(_pixels.AsSpan(offset, tile.Width))) return true;
            }
            return false;
        }
        bool changed = false;
        for (int y = tile.Y; y < tile.Y + tile.Height; y++)
        {
            int offset = y * _width + tile.X;
            if (!IsKnown(offset, tile.Width) ||
                !current.AsSpan(offset, tile.Width).SequenceEqual(_pixels.AsSpan(offset, tile.Width))) changed = true;
            current.AsSpan(offset, tile.Width).CopyTo(_pixels.AsSpan(offset, tile.Width));
            MarkKnown(offset, tile.Width);
        }
        return changed;
    }

    private void Remember(int[] current, VncRectangle region)
    {
        if (IsFullFrame(region))
        {
            current.AsSpan().CopyTo(_pixels);
            if (!_allKnown) Array.Fill(_known, ulong.MaxValue);
            _allKnown = true;
            return;
        }
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            int offset = y * _width + region.X;
            current.AsSpan(offset, region.Width).CopyTo(_pixels.AsSpan(offset, region.Width));
            if (!_allKnown)
                for (int x = 0; x < region.Width; x += TileSize)
                    MarkKnown(offset + x, Math.Min(TileSize, region.Width - x));
        }
    }

    private bool IsFullFrame(VncRectangle region) =>
        region.X == 0 && region.Y == 0 && region.Width == _width && region.Height == _height;

    private bool IsKnown(int offset, int count)
    {
        int bit = offset & 63, first = Math.Min(count, 64 - bit);
        ulong mask = ((1UL << first) - 1) << bit;
        if ((_known[offset >> 6] & mask) != mask) return false;
        return first == count || (_known[(offset >> 6) + 1] & ((1UL << (count - first)) - 1)) == ((1UL << (count - first)) - 1);
    }

    private void MarkKnown(int offset, int count)
    {
        int bit = offset & 63, first = Math.Min(count, 64 - bit);
        _known[offset >> 6] |= ((1UL << first) - 1) << bit;
        if (first != count) _known[(offset >> 6) + 1] |= (1UL << (count - first)) - 1;
    }
}

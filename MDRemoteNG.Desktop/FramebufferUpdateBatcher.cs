using RemoteViewing.Vnc;
using RemoteViewing.Vnc.Server;

namespace MDRemoteNG.Desktop;

/// <summary>Combines adjacent changed tiles before encoding and dispatching them to the viewer.</summary>
internal sealed class FramebufferUpdateBatcher
{
    private readonly DirtyFramebufferCache _cache = new();

    internal void Send(VncServerSession session, FramebufferUpdatingEventArgs args, FramebufferWriteStream output)
    {
        args.Handled = true;
        VncFramebuffer frame = session.Framebuffer;
        FramebufferUpdateRequest? request = session.FramebufferUpdateRequest;
        if (frame == null || request == null) return;
        lock (frame.SyncRoot)
        {
            var changes = _cache.FindChanges(frame, request);
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
                _pixels = new int[checked(_width * _height)];
                _known = new ulong[(_pixels.Length + 63) / 64];
            }
            int[] current = frame.GetPixels();
            if (reset || !request.Incremental)
            {
                Remember(current, region);
                return [region];
            }

            var rectangles = new List<VncRectangle>();
            var previous = new Dictionary<(int X, int Width), int>();
            VncRectangle bounds = default;
            bool bounded = false, collapse = false;
            int right = region.X + region.Width, bottom = region.Y + region.Height;
            for (int y = region.Y; y < bottom; y += TileSize)
            {
                int height = Math.Min(TileSize, bottom - y), runStart = -1;
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
            return collapse ? [bounds] : rectangles;
        }
    }

    private bool IsChanged(int[] current, VncRectangle tile)
    {
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
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            int offset = y * _width + region.X;
            current.AsSpan(offset, region.Width).CopyTo(_pixels.AsSpan(offset, region.Width));
            for (int x = 0; x < region.Width; x += TileSize)
                MarkKnown(offset + x, Math.Min(TileSize, region.Width - x));
        }
    }

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

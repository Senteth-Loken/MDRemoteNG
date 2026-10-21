# RemoteViewing source provenance

This folder vendors [RemoteViewing](https://github.com/SeekHisKingdom/RemoteViewing) at commit
`0a02982ae23f2bbf1c0da18866d256304e0e2f86`, corresponding to the
RemoteViewing 1.1.0 public assembly version `1.1.0.0`. It is built from source;
neither the NuGet package cache nor its downloaded binaries are modified.
The downloaded source archive's SHA-256 is
`CD58F1F344D43776011703865EF603142D1362F87C86B328C62A1005FB66F802`.

The upstream BSD license and the incorporated zlib license are preserved in
`LICENSE.txt` and in the original source headers. The upstream README and
change history are retained as `README.upstream.md` and `CHANGES.upstream.txt`.
All 40 upstream core C# files are retained; only
`Vnc/Server/VncServerSession.cs` and `Vnc/VncPixelFormat.cs` contain local source
changes. The other 38 core C# files are byte-identical to the pinned source.

MDRemoteNG modifications, 2026-10-21:

- Build the original core library with the .NET 10 SDK while preserving its
  public API and assembly identity.
- Replace only the server's encoding-6 compression with the public
  `System.IO.Compression.ZLibStream` API at `CompressionLevel.Fastest`.
  Each connection retains a single compressor across rectangles and flushes
  after every compressed rectangle.
- Reuse a bounded compressed-output buffer, copying the completed compressed
  data back into the existing framebuffer update buffer after input has been
  consumed. This avoids a full-frame temporary allocation on every update.
- Size sync-flushed output conservatively for native fixed-Huffman blocks;
  fall back to Raw when the bounded compressed-frame limit would be exceeded.
- Release compressor resources after worker shutdown and retain a safe
  finalizer fallback. Raw/Hextile and all legacy zlib source files are unchanged.
- Copy equal-format framebuffer rows with the runtime's overlap-safe span copy
  instead of a scalar pixel loop; retain all negotiated channel conversions.

The compression implementation uses the .NET runtime's supported API. It adds
no direct native imports, private runtime reflection, or external codec DLLs.

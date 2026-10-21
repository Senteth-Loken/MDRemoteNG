# VncSharpCore source dependency

This directory contains the official mRemoteNG VncSharpCore source pinned to
[`dc87ba02b0090331d1956cd4a6ba0168ecd301d6`](https://github.com/mRemoteNG/VncSharpCore/commit/dc87ba02b0090331d1956cd4a6ba0168ecd301d6)
("Added back vnccursor.cur"). The upstream repository is
<https://github.com/mRemoteNG/VncSharpCore>.

The NuGet 1.2.1 version commit is
[`7c640d9699a68efaefa8c2fdfcfa8516aea11e10`](https://github.com/mRemoteNG/VncSharpCore/commit/7c640d9699a68efaefa8c2fdfcfa8516aea11e10).
The [comparison](https://github.com/mRemoteNG/VncSharpCore/compare/7c640d9699a68efaefa8c2fdfcfa8516aea11e10...dc87ba02b0090331d1956cd4a6ba0168ecd301d6)
adds only `Resources/vnccursor.cur` and its embedded-resource project entry. The
published 1.2.1 package omits that cursor, so `RemoteDesktop.SetState(Connected)`
passes a missing resource stream to the Windows Forms cursor constructor and
fails after VNC authentication.

The upstream resources and published signing key are unchanged. Local C#
modifications dated 2026-10-21 improve framebuffer decoding:

- `PixelReader`, `PixelReader32`, `RawRectangle` and `Framebuffer` decode Raw
  rectangles in bounded chunks rather than allocating and reading per pixel.
- `PixelReader32` reads the standard little-endian 32-bit format directly into
  destination storage and normalizes opaque alpha in vector-sized chunks.
  `Encodings/EncodedRectangle` copies rows in bulk using the bitmap's actual
  stride, retaining clipped geometry and the generic pixel-format fallbacks.
- `ZlibFramebufferDecoder` and `Encodings/ZlibRectangle` add RFB encoding 6,
  using the public .NET `ZLibStream` API with connection-wide history,
  bounded payload/output sizes, exact pixel counts and cleanup on disconnect.
  A bounded input stream preserves framed sync-flush boundaries; a guarded tail
  prevents native read-ahead from hiding trailing compressed bytes. The legacy
  attributed zlib source remains available to the existing ZRLE decoder.
- `VncClient`, `RfbProtocol`, `EncodedRectangleFactory` and
  `Encodings/EncodedRectangle` negotiate and route the compressed encoding.

Modified files carry dated notices. Synthetic decoder tests cover fragmented
reads, pixel formats, continuous compression history and malformed payloads;
hidden real-viewer checks exercise the TLS tunnel and rendering lifecycle.

`VncSharpCore.csproj` is a local build definition targeting `net10.0-windows`.
It explicitly embeds the screenshot and restored cursor as
`VncSharpCore.Resources.screenshot.png` and
`VncSharpCore.Resources.vnccursor.cur`. It retains the original strong-name
identity (`VncSharpCore`, assembly version `0.0.0.0`, public key token
`61974755c7bfea7c`) by preserving the upstream key and disabled generated assembly
metadata. The signing key is already public in the upstream source repository.

`CompatibilityAssemblyInfo.cs` declares the existing Windows API baseline for
the .NET 10 analyzer. The project suppresses only the pinned source's obsolete
CAS attributes (`SYSLIB0003`), RFB DES-provider construction (`SYSLIB0021`), legacy
exception serialization constructor (`SYSLIB0051`) and designer-only property
serialization diagnostic (`WFO1000`). Authentication, cursor and input behavior
remain those of the pinned upstream source; compression negotiation and
framebuffer decoding include the local modifications described above.

The downloaded commit archive's SHA-256 is
`217e2783ef78075aa8b9cc0ede084f6cdca3688c4d4e339dc612bdc1eabcd9b6`.
The build uses ordinary project references and never modifies the NuGet cache
or patches an assembly at runtime.

Copyright and license notices remain in the source files. See `COPYING.txt`
for the upstream GPL license, `README.upstream.md`, and `zlib.NET/license.txt`
for the separately attributed compression sources.

The project copies the GPL license, zlib license and this provenance document
into the output and publish `Licenses/` directory for redistribution with the
application. The complete corresponding source remains in this directory.

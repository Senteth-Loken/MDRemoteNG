// MDRemoteNG addition (2026-10-21), distributed under this project's GPL-2.0 license.
using System.Drawing;
using System.IO;

namespace VncSharpCore.Encodings
{
    public sealed class ZlibRectangle : EncodedRectangle
    {
        public ZlibRectangle(RfbProtocol rfb, Framebuffer framebuffer, Rectangle rectangle)
            : base(rfb, framebuffer, rectangle, RfbProtocol.ZLIB_ENCODING) { }

        public override void Decode()
        {
            if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width < 0 || rectangle.Height < 0 ||
                rectangle.X > framebuffer.Width || rectangle.Y > framebuffer.Height ||
                rectangle.Width > framebuffer.Width - rectangle.X || rectangle.Height > framebuffer.Height - rectangle.Y)
                throw new InvalidDataException("The compressed VNC rectangle is outside the framebuffer.");
            int pixels = checked(rectangle.Width * rectangle.Height);
            rfb.ZlibReader.Decode(rfb.Reader.BaseStream, checked(pixels * (framebuffer.BitsPerPixel / 8)));
            preader.ReadPixels(framebuffer.Pixels, 0, pixels);
        }
    }
}

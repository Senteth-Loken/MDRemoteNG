// VncSharp - .NET VNC Client Library
// Copyright (C) 2008 David Humphrey
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation; either version 2 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA

using System.Drawing;
using System.IO;

namespace VncSharpCore.Encodings
{
	/// <summary>
	/// Implementation of Raw encoding, as well as drawing support. See RFB Protocol document v. 3.8 section 6.5.1.
	/// </summary>
	public sealed class RawRectangle : EncodedRectangle
	{
		public RawRectangle(RfbProtocol rfb, Framebuffer framebuffer, Rectangle rectangle)
			: base(rfb, framebuffer, rectangle, RfbProtocol.RAW_ENCODING)
		{
		}

		public override void Decode()
		{
            // MDRemoteNG modification (2026-10-21): validate before the unsafe drawing
            // path and decode in blocks rather than one network read per pixel.
            if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width < 0 || rectangle.Height < 0 ||
                rectangle.X > framebuffer.Width || rectangle.Y > framebuffer.Height ||
                rectangle.Width > framebuffer.Width - rectangle.X || rectangle.Height > framebuffer.Height - rectangle.Y)
                throw new InvalidDataException("The Raw VNC rectangle is outside the framebuffer.");
            preader.ReadPixels(framebuffer.Pixels, 0, checked(rectangle.Width * rectangle.Height));
		}
	}
}

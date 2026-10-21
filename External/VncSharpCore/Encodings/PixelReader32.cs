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

using System.IO;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VncSharpCore.Encodings
{
	/// <summary>
	/// A 32-bit PixelReader.
	/// </summary>
	public sealed class PixelReader32 : PixelReader
	{
        // MDRemoteNG modification (2026-10-21): reuse small reads and decode Raw
        // frames in bounded chunks, without an allocation/socket read for every pixel.
        private readonly byte[] pixelBytes = new byte[4];
		public PixelReader32(BinaryReader reader, Framebuffer framebuffer) : base(reader, framebuffer)
		{
		}
	
		public override int ReadPixel()
		{
			// Read the pixel value
			var b = pixelBytes;
            reader.BaseStream.ReadExactly(b);

            var pixel = (uint)b[0] & 0xFF | 
                         (uint)b[1] << 8   | 
                         (uint)b[2] << 16  | 
                         (uint)b[3] << 24;

			// Extract RGB intensities from pixel
			var red   = (byte) ((pixel >> framebuffer.RedShift)   & framebuffer.RedMax);
			var green = (byte) ((pixel >> framebuffer.GreenShift) & framebuffer.GreenMax);
			var blue  = (byte) ((pixel >> framebuffer.BlueShift)  & framebuffer.BlueMax);

			return ToGdiPlusOrder(red, green, blue);			
		}

        public override void ReadPixels(int[] pixels, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(pixels);
            if (offset < 0 || count < 0 || offset > pixels.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return;

            const int maximumPixels = 16 * 1024;
            int redShift = framebuffer.RedShift, greenShift = framebuffer.GreenShift, blueShift = framebuffer.BlueShift;
            uint redMax = (uint)framebuffer.RedMax, greenMax = (uint)framebuffer.GreenMax, blueMax = (uint)framebuffer.BlueMax;
            // MDRemoteNG modification (2026-10-21): normal BGRA pixels can be
            // read straight into their destination, then made opaque in vectors.
            if (BitConverter.IsLittleEndian && redShift == 16 && greenShift == 8 && blueShift == 0 &&
                redMax == 255 && greenMax == 255 && blueMax == 255)
            {
                var opaque = new Vector<uint>(0xFF000000u);
                while (count > 0)
                {
                    int blockPixels = Math.Min(count, maximumPixels);
                    Span<uint> block = MemoryMarshal.Cast<int, uint>(pixels.AsSpan(offset, blockPixels));
                    reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(block));
                    int index = 0;
                    if (Vector.IsHardwareAccelerated)
                        for (; index <= block.Length - Vector<uint>.Count; index += Vector<uint>.Count)
                            (new Vector<uint>(block.Slice(index)) | opaque).CopyTo(block.Slice(index));
                    for (; index < block.Length; index++) block[index] |= 0xFF000000u;
                    offset += blockPixels;
                    count -= blockPixels;
                }
                return;
            }
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(count, maximumPixels) * 4);
            try
            {
                while (count > 0)
                {
                    int blockPixels = Math.Min(count, maximumPixels);
                    reader.BaseStream.ReadExactly(buffer.AsSpan(0, blockPixels * 4));
                    for (int index = 0; index < blockPixels; index++)
                    {
                        uint pixel = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(index * 4, 4));
                        pixels[offset + index] = ToGdiPlusOrder((byte)((pixel >> redShift) & redMax),
                                (byte)((pixel >> greenShift) & greenMax), (byte)((pixel >> blueShift) & blueMax));
                    }
                    offset += blockPixels;
                    count -= blockPixels;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
	}
}

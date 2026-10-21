using System.Buffers.Binary;
using System.Reflection;
using RemoteViewing.Vnc.Server;
using VncSharpCore;

namespace MDRemoteNG.Vnc.Tests;

/// <summary>Exercises the shipped server codec with synthetic bytes and no desktop or input access.</summary>
internal static class ServerCompressionChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type ServerType = typeof(VncServerSession);
    private static readonly MethodInfo Compress = ServerType.GetMethod("DoCompress", PrivateInstance)!;
    private static readonly MethodInfo Init = ServerType.GetMethod("InitFramebufferEncoder", PrivateInstance)!;
    private static readonly MethodInfo Maximum = ServerType.GetMethod("GetZLibUpdateMaxBytes", BindingFlags.NonPublic | BindingFlags.Static)!;

    internal static int Run()
    {
        int passed = 0;
        var server = new VncServerSession();
        Init.Invoke(server, null);
        try
        {
            using var decoder = new ZlibFramebufferDecoder();
            foreach (int length in new[] { 1, 4, 31, 256, 257, 4096, 65536, 3840 * 2160 * 4 })
            {
                byte[] pixels = new byte[length];
                new Random(length).NextBytes(pixels);
                byte[] packet = Encode(server, pixels);
                using var wire = new MemoryStream(packet.Concat(new byte[] { 0x5A }).ToArray());
                decoder.Decode(wire, pixels.Length);
                Check(decoder.Reader.ReadBytes(pixels.Length).SequenceEqual(pixels) && wire.ReadByte() == 0x5A,
                    $"The actual server sync-flushes and bounds an incompressible {length}-byte rectangle with exact RFB framing.", ref passed);
            }

            byte[] repeated = new byte[16384];
            new Random(501).NextBytes(repeated);
            byte[] first = Encode(server, repeated), second = Encode(server, repeated);
            foreach (byte[] packet in new[] { first, second })
            {
                using var wire = new MemoryStream(packet);
                decoder.Decode(wire, repeated.Length);
                if (!decoder.Reader.ReadBytes(repeated.Length).SequenceEqual(repeated)) throw new InvalidDataException("Dictionary continuity failed.");
            }
            Check(second.Length < first.Length / 4,
                "The actual server keeps its compression dictionary across rectangle boundaries.", ref passed);
        }
        finally { server.Close(); }
        server.Close();
        Check(InvocationThrows<IOException>(() => Encode(server, new byte[4])),
            "Closing a server twice releases its compressor and prevents later encoding.", ref passed);

        Type outputType = ServerType.GetNestedType("CompressionOutput", BindingFlags.NonPublic)!;
        using var output = (Stream)Activator.CreateInstance(outputType, nonPublic: true)!;
        MethodInfo reset = outputType.GetMethod("Reset", PrivateInstance)!;
        FieldInfo memoryField = outputType.GetField("_memory", PrivateInstance)!;
        bool bounded = true;
        foreach (int limit in new[] { 1, 255, 256, 257, 65536, 1024 * 1024 + 17 })
        {
            reset.Invoke(output, new object[] { limit });
            output.Write(new byte[limit]);
            var memory = (MemoryStream)memoryField.GetValue(output)!;
            bounded &= memory.Capacity <= 64 * 1024 * 1024 + 64 * 1024 && memory.Capacity <= Math.Max(limit, 65536);
            try { output.WriteByte(0); bounded = false; }
            catch (IOException) { }
        }
        Check(bounded, "The compressed buffer bounds both stored bytes and backing-array capacity while rejecting overflow.", ref passed);
        Check(InvocationThrows<IOException>(() => reset.Invoke(output, new object[] { 64 * 1024 * 1024 + 64 * 1024 + 1 })),
            "The server rejects compressed-output limits above the protocol cap before allocating.", ref passed);
        return passed;
    }

    private static byte[] Encode(VncServerSession server, byte[] pixels)
    {
        int maximum = (int)Maximum.Invoke(null, new object[] { pixels.Length })!;
        byte[] buffer = new byte[maximum];
        pixels.CopyTo(buffer, 0);
        int compressed = (int)Compress.Invoke(server, new object[] { buffer, 0, pixels.Length, maximum - 4 })!;
        BinaryPrimitives.WriteInt32BigEndian(buffer, compressed);
        return buffer.AsSpan(0, compressed + 4).ToArray();
    }

    private static bool InvocationThrows<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (TargetInvocationException exception) when (exception.InnerException is T) { return true; }
    }

    private static void Check(bool condition, string description, ref int passed)
    {
        if (!condition) throw new InvalidOperationException(description);
        passed++;
        Console.WriteLine("PASS " + description);
    }
}

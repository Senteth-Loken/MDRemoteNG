using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using RemoteViewing.Vnc;

namespace MDRemoteNG.Desktop;

public interface IVncDesktopSession : IVncFramebufferSource, IDisposable
{
    void KeyChanged(KeyChangedEventArgs args);
    void PointerChanged(PointerChangedEventArgs args);
}

/// <summary>Shares only this signed-in user's normal primary desktop. No secure-desktop bypass.</summary>
internal sealed class WindowsDesktopSession : IVncDesktopSession
{
    private Bitmap? _bitmap;
    private VncFramebuffer? _frame;
    private readonly HashSet<ushort> _pressed = [];
    private int _buttons;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder name, uint length, out uint needed);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScan(char character);

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }

    private static bool NormalDesktop()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(128);
            return GetUserObjectInformation(desktop, 2, name, 256, out _) && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    public VncFramebuffer Capture()
    {
        if (!NormalDesktop()) throw new IOException("The normal Windows desktop is unavailable or locked.");
        IntPtr prior = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            int width = GetSystemMetrics(0), height = GetSystemMetrics(1);
            if (width <= 0 || height <= 0 || (long)width * height > 16_777_216)
                throw new IOException("The primary display size is unsupported.");
            if (_bitmap == null || _bitmap.Width != width || _bitmap.Height != height)
            {
                _bitmap?.Dispose();
                _bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                _frame = new VncFramebuffer("MDRemoteNG — " + Environment.MachineName, width, height);
            }
            using (var graphics = Graphics.FromImage(_bitmap))
                graphics.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            BitmapData data = _bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                lock (_frame!.SyncRoot)
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(IntPtr.Add(data.Scan0, checked(y * data.Stride)), _frame.GetPixels(), y * width, width);
            }
            finally { _bitmap.UnlockBits(data); }
            return _frame!;
        }
        finally { if (prior != IntPtr.Zero) SetThreadDpiAwarenessContext(prior); }
    }

    private static void Send(Input input) => SendInput(1, [input], Marshal.SizeOf<Input>());
    private static void Keyboard(ushort key, ushort scan, uint flags)
    {
        if (key is 0xA3 or 0xA5 or 0x5B or 0x5C or >= 33 and <= 40 or 45 or 46) flags |= 1;
        Send(new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Key = key, Scan = scan, Flags = flags } } });
    }

    public void KeyChanged(KeyChangedEventArgs args)
    {
        if (!NormalDesktop()) return;
        ushort key = MapKey(args.Keysym);
        bool shortcut = _pressed.Overlaps(new ushort[] { 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C });
        if (key == 0 && shortcut && args.Keysym is >= 0x20 and <= 0x7E)
        {
            short mapped = VkKeyScan((char)args.Keysym);
            if (mapped == -1) return;
            key = (ushort)(mapped & 0xFF);
        }
        if (key != 0)
        {
            if (args.Pressed) _pressed.Add(key); else _pressed.Remove(key);
            Keyboard(key, 0, args.Pressed ? 0u : 2u);
        }
        else
        {
            bool unicode = (args.Keysym & unchecked((int)0xFF000000)) == 0x01000000;
            if (!unicode && args.Keysym is not (>= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF)) return;
            int codepoint = unicode ? args.Keysym & 0xFFFFFF : args.Keysym;
            if (codepoint is < 0x20 or > 0x10FFFF || codepoint is >= 0xD800 and <= 0xDFFF) return;
            foreach (char character in char.ConvertFromUtf32(codepoint)) Keyboard(0, character, 4u | (args.Pressed ? 0u : 2u));
        }
    }

    private static ushort MapKey(int symbol)
    {
        if (symbol is >= 0xFFBE and <= 0xFFD5) return (ushort)(0x70 + symbol - 0xFFBE);
        if (symbol is >= 0xFFB0 and <= 0xFFB9) return (ushort)(0x60 + symbol - 0xFFB0);
        return symbol switch
        {
            0xFF08 => 8, 0xFF09 => 9, 0xFF0D or 0xFF8D => 13, 0xFF1B => 27,
            0xFF50 => 36, 0xFF51 => 37, 0xFF52 => 38, 0xFF53 => 39, 0xFF54 => 40,
            0xFF55 => 33, 0xFF56 => 34, 0xFF57 => 35, 0xFF63 => 45, 0xFFFF => 46,
            0xFFE1 => 0xA0, 0xFFE2 => 0xA1, 0xFFE3 => 0xA2, 0xFFE4 => 0xA3, 0xFFE9 => 0xA4, 0xFFEA => 0xA5,
            0xFFEB => 91, 0xFFEC => 92, 0xFFE5 => 20, 0xFF7F => 144,
            0xFF14 => 145, 0xFF61 => 44, 0xFF13 => 19, _ => 0
        };
    }

    public void PointerChanged(PointerChangedEventArgs args)
    {
        if (_frame == null || !NormalDesktop()) return;
        int x = Math.Clamp(args.X, 0, _frame.Width - 1), y = Math.Clamp(args.Y, 0, _frame.Height - 1);
        uint flags = 0x8001; // Absolute motion, relative to primary display.
        foreach (var button in new[] { (Bit: 1, Down: 2u, Up: 4u), (Bit: 2, Down: 32u, Up: 64u), (Bit: 4, Down: 8u, Up: 16u) })
            if (((args.PressedButtons ^ _buttons) & button.Bit) != 0) flags |= (args.PressedButtons & button.Bit) != 0 ? button.Down : button.Up;
        Send(new Input { Data = new InputData { Mouse = new MouseInput
        { X = (int)(x * 65535L / Math.Max(1, _frame.Width - 1)), Y = (int)(y * 65535L / Math.Max(1, _frame.Height - 1)), Flags = flags } } });
        int wheel = ((args.PressedButtons & 8) != 0 ? 120 : 0) - ((args.PressedButtons & 16) != 0 ? 120 : 0);
        if (wheel != 0) Send(new Input { Data = new InputData { Mouse = new MouseInput { Flags = 0x800, Data = unchecked((uint)wheel) } } });
        _buttons = args.PressedButtons & 7;
    }

    public void Dispose()
    {
        if (NormalDesktop())
        {
            foreach (ushort key in _pressed) Keyboard(key, 0, 2);
            uint release = ((_buttons & 1) != 0 ? 4u : 0u) | ((_buttons & 2) != 0 ? 64u : 0u) | ((_buttons & 4) != 0 ? 16u : 0u);
            if (release != 0) Send(new Input { Data = new InputData { Mouse = new MouseInput { Flags = release } } });
        }
        _pressed.Clear();
        _bitmap?.Dispose();
    }
}

using System.Runtime.InteropServices;

namespace OICQStickerManager.Services;

internal static class NativeInput
{
    internal static bool TryPaste()
    {
        if (Down(0x10) || Down(0x12) || Down(0x5B) || Down(0x5C)) return false;
        bool controlHeld = Down(0x11);
        var keys = controlHeld ? new[] { Key(0x56), Key(0x56, true) }
            : new[] { Key(0x11), Key(0x56), Key(0x56, true), Key(0x11, true) };
        uint sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>());
        if (sent == keys.Length) return true;
        if (sent > 0)
        {
            var release = controlHeld ? new[] { Key(0x56, true) } : new[] { Key(0x56, true), Key(0x11, true) };
            SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
        }
        return false;
    }

    internal static bool TryClick(int x, int y)
    {
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        if (width <= 1 || height <= 1) return false;
        var inputs = new[]
        {
            new Input { Data = new Union { Mouse = new Mouse { X = (int)((long)(x - left) * 65535 / (width - 1)), Y = (int)((long)(y - top) * 65535 / (height - 1)), Flags = 0xC001 } } },
            new Input { Data = new Union { Mouse = new Mouse { Flags = 0x0002 } } },
            new Input { Data = new Union { Mouse = new Mouse { Flags = 0x0004 } } },
        };
        uint sent = SendInput(3, inputs, Marshal.SizeOf<Input>());
        if (sent == 2) SendInput(1, [inputs[2]], Marshal.SizeOf<Input>());
        return sent == 3;
    }

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    private static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new Union { Keyboard = new Keyboard { Key = key, Flags = up ? 2u : 0u } } };
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Union Data; }
    [StructLayout(LayoutKind.Explicit)] private struct Union { [FieldOffset(0)] public Mouse Mouse; [FieldOffset(0)] public Keyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort Key, Scan; public uint Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}

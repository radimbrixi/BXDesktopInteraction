using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BX.DesktopInteraction;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nuint Extra;
    }
    // INPUT's union must be large enough for KEYBDINPUT and HARDWAREINPUT too.
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Union; }
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);

    internal static void Send(uint flags, int x = 0, int y = 0, uint data = 0)
    {
        Input[] input = [new() { Type = 0, Union = new() { Mouse = new() { X = x, Y = y, Data = data, Flags = flags } } }];
        if (SendInput(1, input, Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Mouse input was not accepted. Windows integrity/desktop restrictions may apply.");
    }
    internal static void SendUnicode(char character)
    {
        Input[] inputs = [
            new() { Type = 1, Union = new() { Keyboard = new() { Scan = character, Flags = 4 } } },
            new() { Type = 1, Union = new() { Keyboard = new() { Scan = character, Flags = 6 } } }
        ];
        if (SendInput(2, inputs, Marshal.SizeOf<Input>()) != 2)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Text input was not accepted.");
    }
}

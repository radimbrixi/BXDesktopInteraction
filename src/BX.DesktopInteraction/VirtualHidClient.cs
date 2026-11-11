using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BX.DesktopInteraction;

// Optional transport for the separately built/signed driver. Not used by SendInput tools.
internal sealed class VirtualHidClient : IDisposable
{
    private readonly SafeFileHandle handle;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
    internal VirtualHidClient()
    {
        handle = CreateFile(@"\\.\BxVirtualMouse", 0x40000000, 0, 0, 3, 0, 0);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "BX virtual HID driver is not installed, unavailable, or access was denied."); }
    }
    internal void Report(int dx, int dy, int buttons, int wheel, int horizontalWheel)
    {
        if (dx is < -32767 or > 32767 || dy is < -32767 or > 32767 || buttons is < 0 or > 31 || wheel is < -127 or > 127 || horizontalWheel is < -127 or > 127)
            throw new ArgumentOutOfRangeException(nameof(dx), "HID report is outside descriptor limits.");
        byte[] data = new byte[8]; data[0] = 1; data[1] = (byte)buttons;
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2), (short)dx);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(4), (short)dy);
        data[6] = unchecked((byte)wheel); data[7] = unchecked((byte)horizontalWheel);
        if (!DeviceIoControl(handle, 0x0022A000, data, 8, 0, 0, out _, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose() { handle.Dispose(); }
}

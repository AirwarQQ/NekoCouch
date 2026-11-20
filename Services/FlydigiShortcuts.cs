using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NekoCouch
{
    // Steam's native Apex 5 mode sends button states on a vendor HID collection while
    // the standard Xbox endpoint can stay connected and idle. Listen without claiming
    // the device or sending commands that could change its mode.
    internal sealed class FlydigiShortcuts : IDisposable
    {
        private const ushort UsagePage = 0xffa0, Usage = 1;
        private readonly Action<bool> _command;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<IntPtr, Pad> _pads = new Dictionary<IntPtr, Pad>();
        private readonly int _headerSize = 8 + 2 * IntPtr.Size;
        private byte[] _buffer = new byte[128];

        private sealed class Pad
        {
            public GamepadShortcuts.Hold Hold;
            public int Input;
            public long LastReport = -1;
        }

        public FlydigiShortcuts(IntPtr window, Action<bool> command)
        {
            _command = command;
            if (!Register(window, 0x100 | 0x2000)) // INPUTSINK | DEVNOTIFY
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public void Dispose() => Register(IntPtr.Zero, 1); // RIDEV_REMOVE

        public void DeviceRemoved(IntPtr device) => _pads.Remove(device);

        public void Read(IntPtr message)
        {
            uint size = (uint)_buffer.Length;
            uint read = GetRawInputData(message, 0x10000003, _buffer, ref size, (uint)_headerSize);
            if (read == uint.MaxValue && size > _buffer.Length && size <= 65536)
            {
                _buffer = new byte[size];
                read = GetRawInputData(message, 0x10000003, _buffer, ref size, (uint)_headerSize);
            }
            if (read == uint.MaxValue || read < _headerSize + 8 || BitConverter.ToUInt32(_buffer, 0) != 2) return;
            var device = IntPtr.Size == 8 ? new IntPtr(BitConverter.ToInt64(_buffer, 8)) : new IntPtr(BitConverter.ToInt32(_buffer, 8));
            if (!_pads.TryGetValue(device, out var pad))
            {
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf(typeof(DeviceInfo)) };
                uint infoSize = info.Size;
                if (GetRawInputDeviceInfo(device, 0x2000000b, ref info, ref infoSize) == uint.MaxValue) return;
                pad = info.Type == 2 && info.Vendor == 0x37d7 && info.Product == 0x2501 &&
                      info.Page == UsagePage && info.Usage == Usage ? new Pad() : null;
                _pads[device] = pad;
            }
            if (pad == null) return;
            int width = BitConverter.ToInt32(_buffer, _headerSize);
            int count = BitConverter.ToInt32(_buffer, _headerSize + 4);
            int start = _headerSize + 8;
            if (width != 32 || count <= 0 || count > (read - start) / width) return;
            long now = _clock.ElapsedMilliseconds;
            for (int i = 0; i < count; i++)
            {
                int input = Decode(_buffer, start + i * width);
                if (input < 0) continue;
                pad.LastReport = now;
                pad.Input = input;
                var command = pad.Hold.Update((ushort)input, now);
                if (command.HasValue) _command(command.Value);
            }
        }

        // 32-byte Flydigi V2 input report from collection 1, including report ID.
        internal static int Decode(byte[] packet, int start)
        {
            if (packet[start] != 4 || packet[start + 1] != 0x5a || packet[start + 2] != 0xa5 || packet[start + 3] != 0xef) return -1;
            int face = packet[start + 12], shoulder = packet[start + 13];
            int buttons = (face & 1) | ((face & 4) >> 1) | ((face & 8) >> 1) | ((face & 2) << 2);
            buttons |= ((face & 0x30) << 8) | ((face & 0x80) << 7) | ((face & 0x40) >> 1);
            buttons |= ((shoulder & 1) << 15) | ((shoulder & 2) << 3) | ((shoulder & 0x0c) << 6) | (shoulder & 0xc0);
            return buttons | (packet[start + 16] > 30 ? 0x20000 : 0) | (packet[start + 17] > 30 ? 0x40000 : 0);
        }

        public void Describe(List<string> lines)
        {
            foreach (var pad in _pads.Values)
                if (pad != null && pad.LastReport >= 0 && _clock.ElapsedMilliseconds - pad.LastReport < 1000)
                    lines.Add("Flydigi APEX 5 (HID) · " + GamepadShortcuts.DescribeButtons(pad.Input));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Registration { public ushort Page, Usage; public uint Flags; public IntPtr Target; }

        [StructLayout(LayoutKind.Sequential, Size = 32)]
        private struct DeviceInfo { public uint Size, Type, Vendor, Product, Version; public ushort Page, Usage; }

        private static bool Register(IntPtr window, uint flags)
        {
            var registration = new Registration { Page = UsagePage, Usage = Usage, Flags = flags, Target = window };
            return RegisterRawInputDevices(ref registration, 1, (uint)Marshal.SizeOf(typeof(Registration)));
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(ref Registration device, uint count, uint size);
        [DllImport("user32.dll")]
        private static extern uint GetRawInputData(IntPtr message, uint command, [Out] byte[] data, ref uint size, uint headerSize);
        [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
        private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, ref DeviceInfo info, ref uint size);
    }
}

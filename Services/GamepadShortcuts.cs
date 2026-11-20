using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace NekoCouch
{
    internal sealed class GamepadShortcuts : IDisposable
    {
        private readonly Timer _timer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Hold[] _holds = new Hold[4];
        private readonly long[] _nextProbe = new long[4];
        // Zero is an empty slot. Bit 16 distinguishes a connected, idle pad from an empty slot.
        private readonly int[] _inputs = new int[4];
        private readonly Action<bool> _command;
        private int _polling;

        public GamepadShortcuts(Action<bool> command)
        {
            _command = command;
            _timer = new Timer(Poll, null, 0, 100);
        }

        private void Poll(object unused)
        {
            if (Interlocked.Exchange(ref _polling, 1) != 0) return;
            try
            {
                long now = _clock.ElapsedMilliseconds;
                for (int slot = 0; slot < _holds.Length; slot++)
                {
                    if (now < _nextProbe[slot]) continue;
                    if (XInputGetState(slot, out var state) != 0)
                    {
                        Volatile.Write(ref _inputs[slot], 0);
                        _holds[slot] = default;
                        _nextProbe[slot] = now + 1000;
                        continue;
                    }
                    int input = 0x10000 | state.Buttons;
                    if (state.LeftTrigger > 30) input |= 0x20000;
                    if (state.RightTrigger > 30) input |= 0x40000;
                    Volatile.Write(ref _inputs[slot], input);
                    bool? activate = _holds[slot].Update(state.Buttons, now);
                    if (activate.HasValue) _command(activate.Value);
                }
            }
            finally { Volatile.Write(ref _polling, 0); }
        }

        private static readonly string[] ButtonNames = { "↑", "↓", "←", "→", "Menu", "View", "LS", "RS",
            "LB", "RB", null, null, "A", "B", "X", "Y", null, "LT", "RT" };

        internal static string DescribeButtons(int input)
        {
            var pressed = new List<string>();
            for (int bit = 0; bit < ButtonNames.Length; bit++)
                if (ButtonNames[bit] != null && (input & (1 << bit)) != 0)
                    pressed.Add(ButtonNames[bit]);
            return pressed.Count == 0 ? Loc.T("Set.Pad.Idle") : string.Join(" + ", pressed);
        }

        public string DescribeInput(FlydigiShortcuts flydigi)
        {
            var pads = new List<string>();
            flydigi?.Describe(pads);
            for (int slot = 0; slot < _inputs.Length; slot++)
            {
                int input = Volatile.Read(ref _inputs[slot]);
                if (input == 0) continue;
                pads.Add(Loc.T("Set.Pad.Input", slot + 1, DescribeButtons(input)));
            }
            return pads.Count == 0 ? Loc.T("Set.Pad.NoInput") : string.Join("\n", pads);
        }

        internal struct Hold
        {
            private const ushort Shoulders = 0x0300, Up = 0x0001, Down = 0x0002;
            private ushort _chord;
            private long _since, _lastSample;
            private bool _fired;

            public bool? Update(ushort buttons, long now)
            {
                ushort chord = (ushort)(buttons & 0xf3ff); // ignore reserved XInput bits
                if (chord != (Shoulders | Up) && chord != (Shoulders | Down)) chord = 0;
                if (chord != _chord || (!_fired && now - _lastSample > 500))
                {
                    _chord = chord;
                    _since = now;
                    _fired = false;
                }
                _lastSample = now;
                if (_chord == 0 || _fired || now - _since < 2000) return null;
                _fired = true;
                return _chord == (Shoulders | Up);
            }
        }

        public void Dispose() => _timer.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct State
        {
            public uint Packet;
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short LeftX, LeftY, RightX, RightY;
        }

        [DllImport("xinput1_4.dll", ExactSpelling = true)]
        private static extern uint XInputGetState(int slot, out State state);
    }
}

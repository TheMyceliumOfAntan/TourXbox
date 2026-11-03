using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TourXbox.Core.Input;

/// <summary>
/// XInput 手柄轮询器。以约 4ms 间隔在独立线程上读取手柄状态，
/// 通过 StatePolled 事件把每一帧状态交出去（引擎需要稳定时间片来换算档位）。
/// </summary>
public sealed class XInputSource : IDisposable
{
    private const int PollIntervalMs = 4;
    private const int ReconnectIntervalMs = 750;

    private readonly int _controllerIndex;
    private Thread? _thread;
    private volatile bool _running;
    private bool _connected;

    public XInputSource(int controllerIndex = 0)
    {
        _controllerIndex = controllerIndex;
    }

    /// <summary>每帧回调：(状态, 距上一帧的秒数)。在轮询线程上触发。</summary>
    public event Action<PadState, double>? StatePolled;

    /// <summary>连接状态变化时触发。</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _connected;

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "TourXbox.XInput",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
    }

    private void Loop()
    {
        NativeXInput.TimeBeginPeriod(1);

        var clock = Stopwatch.StartNew();
        double lastSeconds = 0;
        var state = new XINPUT_STATE();

        try
        {
            while (_running)
            {
                int result = NativeXInput.XInputGetState(_controllerIndex, ref state);
                bool connected = result == 0;

                if (connected != _connected)
                {
                    _connected = connected;
                    ConnectionChanged?.Invoke(connected);
                    if (!connected)
                    {
                        StatePolled?.Invoke(PadState.Disconnected, 0);
                    }
                }

                if (connected)
                {
                    double now = clock.Elapsed.TotalSeconds;
                    double dt = now - lastSeconds;
                    lastSeconds = now;

                    var pad = new PadState(
                        true,
                        state.Gamepad.wButtons,
                        state.Gamepad.bLeftTrigger,
                        state.Gamepad.bRightTrigger,
                        state.Gamepad.sThumbLX,
                        state.Gamepad.sThumbLY,
                        state.Gamepad.sThumbRX,
                        state.Gamepad.sThumbRY,
                        state.dwPacketNumber);

                    StatePolled?.Invoke(pad, Math.Clamp(dt, 0.001, 0.1));
                }

                Thread.Sleep(connected ? PollIntervalMs : ReconnectIntervalMs);
            }
        }
        finally
        {
            NativeXInput.TimeEndPeriod(1);
        }
    }

    public void Dispose() => Stop();

    // ---------------- XInput P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    private static class NativeXInput
    {
        // xinput1_4 在 Windows 8 及以上存在；缺失时回退到 1_3 / 9_1_0
        private static readonly string? Dll = ResolveDll();

        private static string? ResolveDll()
        {
            foreach (var name in new[] { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" })
            {
                try
                {
                    if (NativeLibrary.TryLoad(name, out _))
                    {
                        return name;
                    }
                }
                catch
                {
                    // 忽略
                }
            }

            return null;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState14(int index, ref XINPUT_STATE state);

        [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState13(int index, ref XINPUT_STATE state);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState910(int index, ref XINPUT_STATE state);

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        public static extern uint TimeBeginPeriod(uint period);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        public static extern uint TimeEndPeriod(uint period);

        public static int XInputGetState(int index, ref XINPUT_STATE state)
        {
            try
            {
                return Dll switch
                {
                    "xinput1_4.dll" => XInputGetState14(index, ref state),
                    "xinput1_3.dll" => XInputGetState13(index, ref state),
                    "xinput9_1_0.dll" => XInputGetState910(index, ref state),
                    _ => 1167, // ERROR_DEVICE_NOT_CONNECTED
                };
            }
            catch
            {
                return 1167;
            }
        }
    }
}

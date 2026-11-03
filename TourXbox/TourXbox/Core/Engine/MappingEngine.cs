using System;
using System.Collections.Generic;
using TourXbox.Core.Actions;
using TourXbox.Core.Input;
using TourXbox.Interop;

namespace TourXbox.Core.Engine;

/// <summary>
/// 映射引擎：把一帧手柄状态翻译成键鼠动作。
/// 在轮询线程上每 4ms 调用一次 Update。
///
/// 设计前提：右手握笔，所以所有画图必需的操作都必须能用左手完成。
/// 左手可达区 = 左摇杆 + 十字键 + LB + LT + L3。
/// 右手区（ABXY / 右摇杆 / RB / RT）只放"停笔调整"用的低频功能。
/// </summary>
public sealed class MappingEngine
{
    private const float AxisDeadzone = 0.16f;
    private const float PanDeadzone = 0.12f;
    private const float RotateDeadzone = 0.26f;
    private const float TriggerPressThreshold = 0.55f;

    private readonly ActionExecutor _executor;
    private readonly KnobEmulator _leftX = new();
    private readonly KnobEmulator _leftY = new();
    private readonly KnobEmulator _rightX = new();
    private readonly KnobEmulator _rightY = new();

    private readonly HeldMacro _leftStickMacro;
    private readonly HeldMacro _rightStickMacro;
    private readonly Dictionary<string, KeyChord> _buttonHolds = new(StringComparer.OrdinalIgnoreCase);

    private volatile Profile? _profile;
    private ushort _previousButtons;
    private bool _wasActive;
    private int _dominantAxis;
    private int _rightDominantAxis;
    private bool _stickMacroActive;
    private float _rotateAngle;
    private double _rotatePixels;
    private double _orbitPhi;
    private double _orbitBase;
    private int _orbitPivotX;
    private int _orbitPivotY;
    private int _orbitRadius = 220;
    private int _stillFrames;
    private int _rotateWarmupFrames;

    public MappingEngine(ActionExecutor executor)
    {
        _executor = executor;
        _leftStickMacro = new HeldMacro(executor);
        _rightStickMacro = new HeldMacro(executor);
    }

    /// <summary>当前生效的配置。null 表示不做任何映射。</summary>
    public Profile? Profile
    {
        get => _profile;
        set
        {
            if (!ReferenceEquals(_profile, value))
            {
                _profile = value;
                ResetState();
            }
        }
    }

    /// <summary>总开关：暂停时或前台不是目标程序时为 false。</summary>
    public bool Active { get; set; }

    /// <summary>摇杆轴锁定：斜推时只响应偏移更大的那个轴。</summary>
    public bool AxisLock { get; set; } = true;

    /// <summary>每次真正发出动作时触发：(输入名, 按键文本, 中文名称)。</summary>
    public event Action<string, string, string?>? Fired;

    /// <summary>诊断输出（写日志用），可能每帧调用，需自行节流。</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>
    /// 旋转绕圈的圆心（画布中心）。由上层用前台窗口的客户区中心提供；
    /// 拿不到时退回光标位置。
    /// </summary>
    public Func<(int X, int Y)?>? PivotProvider { get; set; }

    private double _traceAccumulator;

    public void Update(PadState state, double dt)
    {
        Profile? profile = _profile;

        if (!Active || !state.Connected || profile == null)
        {
            if (_wasActive)
            {
                ResetState();
                _wasActive = false;
            }

            _previousButtons = state.Buttons;
            return;
        }

        _wasActive = true;
        _traceAccumulator += dt;

        ushort buttons = state.Buttons;
        bool leftTriggerHeld = state.Lt >= TriggerPressThreshold;

        // ---- 1. 按键（先处理，保证层键生效） ----
        ushort pressed = (ushort)(buttons & ~_previousButtons);
        ushort released = (ushort)(_previousButtons & ~buttons);

        foreach (PadButton button in EnumerateButtons(pressed))
        {
            HandleButtonDown(button, profile, buttons, leftTriggerHeld);
        }

        foreach (PadButton button in EnumerateButtons(released))
        {
            HandleButtonUp(button);
        }

        _previousButtons = buttons;

        // ---- 2. 左摇杆：可能被 L3（平移）或 LT（旋转）接管 ----
        bool consumed = HandleLeftStickOverride(profile, state, dt, buttons, leftTriggerHeld);

        if (!consumed)
        {
            HandleLeftStickTicks(profile, state, dt, buttons, leftTriggerHeld);
        }
        else
        {
            _leftX.Reset();
            _leftY.Reset();
        }

        // ---- 3. 右摇杆：基础层平移，RB 层可改成粗调旋钮 ----
        HandleRightStick(profile, state, dt, buttons, leftTriggerHeld);
    }

    /// <summary>暂停 / 失焦时调用，确保没有键被卡住。</summary>
    public void ResetState()
    {
        _executor.ReleaseAll();
        _buttonHolds.Clear();
        _leftStickMacro.End();
        _rightStickMacro.End();
        _leftX.Reset();
        _leftY.Reset();
        _rightX.Reset();
        _rightY.Reset();
        _dominantAxis = 0;
        _rightDominantAxis = 0;
        _stickMacroActive = false;
        _rotateAngle = 0;
        _rotatePixels = 0;
    }

    // ---------------- 按键 ----------------

    private void HandleButtonDown(PadButton button, Profile profile, ushort buttons, bool leftTriggerHeld)
    {
        string baseName = ButtonKey(button);
        string key = ResolveLayerKey(baseName, profile, buttons, leftTriggerHeld);
        Binding? binding = profile.Get(key) ?? profile.Get(baseName);
        if (binding == null)
        {
            return;
        }

        switch (binding.ResolvedMode)
        {
            case BindingMode.Tap:
            {
                KeyChord chord = KeyChord.Parse(binding.Action);
                if (!chord.IsEmpty)
                {
                    _executor.Tap(chord);
                    Fired?.Invoke(PadState.ButtonName(button), chord.ToString(), binding.Note);
                }

                break;
            }

            case BindingMode.Hold:
            {
                KeyChord chord = KeyChord.Parse(binding.Action);
                if (!chord.IsEmpty && !_buttonHolds.ContainsKey(baseName))
                {
                    _executor.Hold(chord);
                    _buttonHolds[baseName] = chord;
                    Fired?.Invoke(PadState.ButtonName(button), chord.ToString(), binding.Note);
                }

                break;
            }
        }
    }

    private void HandleButtonUp(PadButton button)
    {
        string baseName = ButtonKey(button);
        if (_buttonHolds.TryGetValue(baseName, out KeyChord? chord))
        {
            _executor.Release(chord);
            _buttonHolds.Remove(baseName);
        }
    }

    private static string ButtonKey(PadButton button) => button switch
    {
        PadButton.LeftShoulder => "LB",
        PadButton.RightShoulder => "RB",
        PadButton.LeftThumb => "L3",
        PadButton.RightThumb => "R3",
        PadButton.Back => "View",
        PadButton.Start => "Menu",
        PadButton.DPadUp => "DPadUp",
        PadButton.DPadDown => "DPadDown",
        PadButton.DPadLeft => "DPadLeft",
        PadButton.DPadRight => "DPadRight",
        PadButton.A => "A",
        PadButton.B => "B",
        PadButton.X => "X",
        PadButton.Y => "Y",
        _ => button.ToString(),
    };

    private static IEnumerable<PadButton> EnumerateButtons(ushort mask)
    {
        if (mask == 0)
        {
            yield break;
        }

        foreach (PadButton candidate in AllButtons)
        {
            if ((mask & (ushort)candidate) != 0)
            {
                yield return candidate;
            }
        }
    }

    private static readonly PadButton[] AllButtons =
    {
        PadButton.DPadUp, PadButton.DPadDown, PadButton.DPadLeft, PadButton.DPadRight,
        PadButton.Start, PadButton.Back,
        PadButton.LeftThumb, PadButton.RightThumb,
        PadButton.LeftShoulder, PadButton.RightShoulder,
        PadButton.A, PadButton.B, PadButton.X, PadButton.Y,
    };

    /// <summary>
    /// 解析"按住某键时的替代映射"。顺序即优先级：
    /// LB（第二层）→ L3（平移模式）→ LT（旋转模式）→ RB（右手层）→ 基础层
    /// </summary>
    private static string ResolveLayerKey(string input, Profile profile, ushort buttons, bool leftTriggerHeld)
    {
        foreach (string prefix in EnumeratePrefixes(buttons, leftTriggerHeld))
        {
            string layered = prefix + "+" + input;
            if (profile.Bindings.ContainsKey(layered))
            {
                return layered;
            }
        }

        return input;
    }

    private static IEnumerable<string> EnumeratePrefixes(ushort buttons, bool leftTriggerHeld)
    {
        if ((buttons & (ushort)PadButton.LeftShoulder) != 0)
        {
            yield return "LB";
        }

        if ((buttons & (ushort)PadButton.LeftThumb) != 0)
        {
            yield return "L3";
        }

        if (leftTriggerHeld)
        {
            yield return "LT";
        }

        if ((buttons & (ushort)PadButton.RightShoulder) != 0)
        {
            yield return "RB";
        }
    }

    // ---------------- 左摇杆：被 L3 / LT 接管时 ----------------

    private bool HandleLeftStickOverride(
        Profile profile,
        PadState state,
        double dt,
        ushort buttons,
        bool leftTriggerHeld)
    {
        // 旋转优先（LT 按住）
        string rotateKey = ResolveLayerKey("ROTATE", profile, buttons, leftTriggerHeld);
        Binding? rotate = profile.Get(rotateKey);
        if (rotateKey != "ROTATE" && rotate is { ResolvedMode: BindingMode.Rotate })
        {
            float magnitude = MathF.Sqrt((state.Lx * state.Lx) + (state.Ly * state.Ly));
            TraceThrottled($"旋转层激活 幅度={magnitude:F2} 阈值={RotateDeadzone:F2} 宏={(string.IsNullOrWhiteSpace(rotate.Positive) ? "拖拽" : "步进")}");
            RotateWithLeftStick(rotate, state, dt);
            return true;
        }

        // 平移次之（L3 按住时左摇杆变太空键拖拽）
        string panKey = ResolveLayerKey("PAN", profile, buttons, leftTriggerHeld);
        Binding? pan = profile.Get(panKey);
        if (panKey != "PAN" && pan is { ResolvedMode: BindingMode.Drag })
        {
            PanWithStick(_leftStickMacro, pan, state.Lx, state.Ly, dt);
            return true;
        }

        if (_leftStickMacro.IsHeld)
        {
            _leftStickMacro.End();
        }

        return false;
    }

    // ---------------- 左摇杆：档位 ----------------

    private void HandleLeftStickTicks(
        Profile profile,
        PadState state,
        double dt,
        ushort buttons,
        bool leftTriggerHeld)
    {
        HandleStickTicks(
            _leftX, _leftY, "LX", "LY",
            state.Lx, state.Ly, dt, profile, buttons, leftTriggerHeld, ref _dominantAxis);
    }

    /// <summary>左右摇杆共用的档位处理（含轴锁定）。</summary>
    private void HandleStickTicks(
        KnobEmulator knobX,
        KnobEmulator knobY,
        string axisX,
        string axisY,
        float x,
        float y,
        double dt,
        Profile profile,
        ushort buttons,
        bool leftTriggerHeld,
        ref int dominant)
    {
        if (!AxisLock)
        {
            HandleTickAxis(knobX, axisX, x, dt, profile, buttons, leftTriggerHeld);
            HandleTickAxis(knobY, axisY, y, dt, profile, buttons, leftTriggerHeld);
            return;
        }

        float ax = Math.Abs(x);
        float ay = Math.Abs(y);

        if (ax < AxisDeadzone && ay < AxisDeadzone)
        {
            dominant = 0;
        }
        else if (dominant == 0)
        {
            dominant = ax >= ay ? 1 : 2;
        }
        else if (ax > ay * 1.35f)
        {
            dominant = 1;
        }
        else if (ay > ax * 1.35f)
        {
            dominant = 2;
        }

        if (dominant == 1)
        {
            HandleTickAxis(knobX, axisX, x, dt, profile, buttons, leftTriggerHeld);
            knobY.Reset();
        }
        else if (dominant == 2)
        {
            HandleTickAxis(knobY, axisY, y, dt, profile, buttons, leftTriggerHeld);
            knobX.Reset();
        }
        else
        {
            knobX.Reset();
            knobY.Reset();
        }
    }

    private void HandleTickAxis(
        KnobEmulator knob,
        string axis,
        float value,
        double dt,
        Profile profile,
        ushort buttons,
        bool leftTriggerHeld)
    {
        string key = ResolveLayerKey(axis, profile, buttons, leftTriggerHeld);
        Binding? binding = profile.Get(key) ?? profile.Get(axis);

        if (binding == null || binding.ResolvedMode != BindingMode.Tick)
        {
            knob.Reset();
            return;
        }

        knob.Curve = binding.Curve ?? 1.8;
        knob.MaxTicksPerSecond = binding.Speed ?? 13.0;

        int ticks = knob.Update(value, dt, out int direction);
        if (ticks <= 0 || direction == 0)
        {
            return;
        }

        string? text = direction > 0 ? binding.Positive : binding.Negative;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        KeyChord chord = KeyChord.Parse(text);
        if (chord.IsEmpty)
        {
            return;
        }

        for (int i = 0; i < ticks; i++)
        {
            _executor.Tap(chord);
        }

        Fired?.Invoke(AxisLabel(axis), text!, binding.Note);
    }

    private static string AxisLabel(string axis) => axis switch
    {
        "LX" => "左摇杆 ←→",
        "LY" => "左摇杆 ↑↓",
        "RX" => "右摇杆 ←→",
        "RY" => "右摇杆 ↑↓",
        _ => axis,
    };

    // ---------------- 平移 / 旋转 ----------------

    /// <summary>
    /// 右摇杆：默认用于平移画布；如果当前层给 RX/RY 配了 tick 绑定
    /// （例如 RB + 右摇杆 = 画笔大小粗调 / 硬度），则改成档位旋钮。
    /// </summary>
    private void HandleRightStick(
        Profile profile,
        PadState state,
        double dt,
        ushort buttons,
        bool leftTriggerHeld)
    {
        Binding? rx = profile.Get(ResolveLayerKey("RX", profile, buttons, leftTriggerHeld));
        Binding? ry = profile.Get(ResolveLayerKey("RY", profile, buttons, leftTriggerHeld));

        bool tickMode = rx is { ResolvedMode: BindingMode.Tick }
            || ry is { ResolvedMode: BindingMode.Tick };

        if (tickMode)
        {
            if (_rightStickMacro.IsHeld)
            {
                _rightStickMacro.End();
            }

            HandleStickTicks(
                _rightX, _rightY, "RX", "RY",
                state.Rx, state.Ry, dt, profile, buttons, leftTriggerHeld, ref _rightDominantAxis);
            return;
        }

        _rightX.Reset();
        _rightY.Reset();

        Binding? pan = profile.Get(ResolveLayerKey("PAN", profile, buttons, leftTriggerHeld));
        if (pan is { ResolvedMode: BindingMode.Drag })
        {
            PanWithStick(_rightStickMacro, pan, state.Rx, state.Ry, dt);
        }
        else if (_rightStickMacro.IsHeld)
        {
            _rightStickMacro.End();
        }
    }

    private void PanWithStick(HeldMacro macro, Binding binding, float x, float y, double dt)
    {
        bool moving = Math.Abs(x) > PanDeadzone || Math.Abs(y) > PanDeadzone;

        if (!moving)
        {
            if (macro.IsHeld)
            {
                macro.End();
            }

            return;
        }

        if (!macro.IsHeld)
        {
            macro.Begin(KeyChord.Parse(binding.Modifier), KeyChord.Parse(binding.MouseButton));
        }

        if (!_executor.AllowCursorMove(binding.CursorRadius ?? 300, binding.Wrap, macro.Button))
        {
            return;
        }

        _executor.MoveMouse(x, y, dt);
    }

    private void RotateWithLeftStick(Binding binding, PadState state, double dt)
    {
        float x = state.Lx;
        float y = state.Ly;
        float magnitude = MathF.Sqrt((x * x) + (y * y));

        bool stepMode = !string.IsNullOrWhiteSpace(binding.Positive)
            && !string.IsNullOrWhiteSpace(binding.Negative);

        if (magnitude < RotateDeadzone)
        {
            if (_stickMacroActive)
            {
                _leftStickMacro.End();
                _stickMacroActive = false;
                _rotateAngle = 0;
                _rotatePixels = 0;
                _orbitPhi = 0;
            }

            return;
        }

        float angle = MathF.Atan2(y, x);

        if (!_stickMacroActive)
        {
            _stickMacroActive = true;
            _rotateAngle = angle;
            _rotatePixels = 0;
            _orbitPhi = 0;
            _stillFrames = 0;
            _rotateWarmupFrames = stepMode ? 0 : 15; // 拖拽模式先让软件吃到修饰键，约 60ms

            if (!stepMode && IsOrbit(binding))
            {
                // 先记住原始位置，再把光标摆到绕圈轨道上，最后才按下鼠标键
                _executor.BeginCursorAnchor();
                PrepareOrbit(binding);
            }

            if (!stepMode)
            {
                _leftStickMacro.Begin(
                    KeyChord.Parse(binding.Modifier),
                    KeyChord.Parse(binding.MouseButton),
                    captureAnchor: !IsOrbit(binding));
            }

            Fired?.Invoke("左摇杆 打圈", "旋转画布", binding.Note ?? "旋转画布");

            return;
        }

        if (_rotateWarmupFrames > 0)
        {
            _rotateWarmupFrames--;
            _rotateAngle = angle;
            return;
        }

        float delta = NormalizeAngle(angle - _rotateAngle);
        _rotateAngle = angle;

        // 光标绕圈：以画布中心为轴，摇杆转多少光标转多少。
        // 软件按"光标相对画布中心的夹角"计算旋转，所以必须绕中心转，
        // 绕鼠标落点转会变成在中心两侧来回摆。
        if (IsOrbit(binding))
        {
            double spin = -delta; // 屏幕角度顺时针为正

            _orbitPhi += spin;

            // 摇杆停在左右两侧时，持续旋转（按住不动也能转）
            bool still = Math.Abs(delta) < 0.012f;
            _stillFrames = still ? _stillFrames + 1 : 0;

            if (_stillFrames > 6 && Math.Abs(x) > 0.55f)
            {
                double rate = 2.4; // 弧度/秒（推满约 0.4 圈/秒）
                _orbitPhi += (binding.Invert ? -x : x) * rate * dt;
            }

            double screenAngle = _orbitBase + _orbitPhi;
            _executor.OrbitCursor(_orbitPivotX, _orbitPivotY, _orbitRadius, screenAngle);
            return;
        }

        double degrees = delta * (180.0 / Math.PI);

        // 步进模式：不依赖软件的拖拽旋转，按角度发"顺时针/逆时针"按键
        if (stepMode)
        {
            double step = binding.StepDegrees is > 0 and double s ? s : 5.0;

            // XInput 的 Y 轴向上为正，atan2 增大是逆时针；这里让 Positive = 顺时针
            double signed = binding.Invert ? degrees : -degrees;
            _rotatePixels += signed;

            while (_rotatePixels >= step)
            {
                _rotatePixels -= step;
                TapAction(binding.Positive!, "旋转 顺时针");
            }

            while (_rotatePixels <= -step)
            {
                _rotatePixels += step;
                TapAction(binding.Negative!, "旋转 逆时针");
            }

            return;
        }

        double pixelsPerDegree = binding.DegreesToPixels ?? 4.0;
        double pixels = degrees * pixelsPerDegree;
        if (binding.Invert)
        {
            pixels = -pixels;
        }

        _rotatePixels += pixels;
        int whole = (int)_rotatePixels;
        if (whole != 0)
        {
            _rotatePixels -= whole;

            if (_executor.AllowCursorMove(binding.CursorRadius ?? 260, binding.Wrap, _leftStickMacro.Button))
            {
                _executor.MoveMousePixels(whole, 0);
            }
        }
    }

    private static bool IsOrbit(Binding binding) =>
        string.Equals(binding.Path, "orbit", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 开始绕圈前先把光标摆到"以画布中心为圆心"的圆上，这样起手不会有跳变。
    /// 光标离中心太近时半径会兜底到最小值，此时才需要挪一下（按键还没按下，不会产生拖拽）。
    /// </summary>
    private void PrepareOrbit(Binding binding)
    {
        int radius = binding.CursorRadius is > 0 and int r ? r : 220;

        if (PivotProvider?.Invoke() is (int pivotX, int pivotY))
        {
            _orbitPivotX = pivotX;
            _orbitPivotY = pivotY;
        }
        else
        {
            // 拿不到窗口信息时退回用光标位置，至少行为可预期
            NativeMethods.GetCursorPos(out NativeMethods.POINT p);
            _orbitPivotX = p.X;
            _orbitPivotY = p.Y;
        }

        NativeMethods.GetCursorPos(out NativeMethods.POINT cursor);

        double dx = cursor.X - _orbitPivotX;
        double dy = cursor.Y - _orbitPivotY;
        double distance = Math.Sqrt((dx * dx) + (dy * dy));

        _orbitBase = distance > 1 ? Math.Atan2(dy, dx) : -Math.PI / 2;

        // 固定半径：太小的话圆心（画布中心）落在圈外，角度就会来回摆
        _orbitRadius = radius;

        if (Math.Abs(_orbitRadius - distance) > 6)
        {
            int targetX = _orbitPivotX + (int)Math.Round(_orbitRadius * Math.Cos(_orbitBase));
            int targetY = _orbitPivotY + (int)Math.Round(_orbitRadius * Math.Sin(_orbitBase));
            _executor.SetCursorPosition(targetX, targetY);
        }
    }


    private void TapAction(string text, string label)
    {
        KeyChord chord = KeyChord.Parse(text);
        if (chord.IsEmpty)
        {
            return;
        }

        _executor.Tap(chord);
        Fired?.Invoke("左摇杆 打圈", chord.ToString(), label);
    }

    private void TraceThrottled(string message)
    {
        if (_traceAccumulator < 0.5)
        {
            return;
        }

        _traceAccumulator = 0;
        Trace?.Invoke(message);
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle > MathF.PI)
        {
            angle -= MathF.Tau;
        }

        while (angle < -MathF.PI)
        {
            angle += MathF.Tau;
        }

        return angle;
    }

    // ---------------- 内部小工具 ----------------

    /// <summary>"按住某键 + 某鼠标键 + 移动"的宏，平移和旋转共用。</summary>
    private sealed class HeldMacro
    {
        private readonly ActionExecutor _executor;
        private KeyChord? _key;
        private KeyChord? _button;

        public HeldMacro(ActionExecutor executor) => _executor = executor;

        public bool IsHeld => _key != null || _button != null;

        /// <summary>当前按住的鼠标键（归位时需要临时松开）。</summary>
        public KeyChord? Button => _button;

        public void Begin(KeyChord? key, KeyChord? button, bool captureAnchor = true)
        {
            _key = key is { IsEmpty: false } ? key : null;
            _button = button is { IsEmpty: false } ? button : null;

            if (captureAnchor)
            {
                _executor.BeginCursorAnchor();
            }

            if (_key != null)
            {
                _executor.Hold(_key);
            }

            if (_button != null)
            {
                _executor.Hold(_button);
            }
        }

        public void End()
        {
            if (_button != null)
            {
                _executor.Release(_button);
                _button = null;
            }

            if (_key != null)
            {
                _executor.Release(_key);
                _key = null;
            }

            // 松手后把光标放回原处，用户不用满屏找鼠标
            _executor.EndCursorAnchor();
        }
    }
}

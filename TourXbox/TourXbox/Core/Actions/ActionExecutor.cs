using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TourXbox.Interop;

namespace TourXbox.Core.Actions;

/// <summary>
/// 通过 SendInput 把按键/鼠标动作注入到当前前台窗口。
/// 所有方法都是线程安全的，但建议只从轮询线程调用以保证时序。
/// </summary>
public sealed class ActionExecutor
{
    private readonly object _gate = new();
    private readonly List<KeyChord> _held = new();
    private readonly int _inputSize = Marshal.SizeOf<NativeMethods.INPUT>();

    /// <summary>相对移动鼠标时的像素换算系数。</summary>
    public double MovePixelsPerAxisUnit { get; set; } = 26.0;

    /// <summary>发送一次完整按键（按下并松开）。</summary>
    public void Tap(KeyChord chord)
    {
        if (chord.IsEmpty)
        {
            return;
        }

        Press(chord, 1);
        Press(chord, -1);
    }

    /// <summary>按住不放。重复调用会被忽略。</summary>
    public void Hold(KeyChord chord)
    {
        if (chord.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_held.Exists(c => ReferenceEquals(c, chord) || c.Raw == chord.Raw))
            {
                return;
            }

            _held.Add(chord);
        }

        Press(chord, 1);
    }

    /// <summary>松开之前按住的键。</summary>
    public void Release(KeyChord chord)
    {
        if (chord.IsEmpty)
        {
            return;
        }

        bool wasHeld;
        lock (_gate)
        {
            int index = _held.FindIndex(c => ReferenceEquals(c, chord) || c.Raw == chord.Raw);
            wasHeld = index >= 0;
            if (wasHeld)
            {
                _held.RemoveAt(index);
            }
        }

        if (wasHeld)
        {
            Press(chord, -1);
        }
    }

    /// <summary>松开所有按住的键（暂停、失焦、退出时调用）。</summary>
    public void ReleaseAll()
    {
        KeyChord[] pending;
        lock (_gate)
        {
            pending = _held.ToArray();
            _held.Clear();
        }

        // 逆序释放更安全
        for (int i = pending.Length - 1; i >= 0; i--)
        {
            Press(pending[i], -1);
        }
    }

    /// <summary>相对移动鼠标（按轴偏移量换算，供平移使用）。</summary>
    public void MoveMouse(float axisX, float axisY, double dt)
    {
        int dx = (int)Math.Round(axisX * MovePixelsPerAxisUnit * dt * 60.0);
        int dy = (int)Math.Round(-axisY * MovePixelsPerAxisUnit * dt * 60.0);
        MoveMousePixels(dx, dy);
    }

    /// <summary>相对移动鼠标（精确像素，供旋转使用）。</summary>
    public void MoveMousePixels(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }

        // 拆成小步发送：Windows 的指针加速会放大单次大位移，
        // 导致"先慢后快再慢"的速度失真。小步走可以让每帧位移接近线性。
        const int maxStep = 8;

        while (dx != 0 || dy != 0)
        {
            int stepX = Math.Clamp(dx, -maxStep, maxStep);
            int stepY = Math.Clamp(dy, -maxStep, maxStep);

            var input = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.INPUTUNION
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = stepX,
                        dy = stepY,
                        dwFlags = NativeMethods.MOUSEEVENTF_MOVE,
                    },
                },
            };

            Send(new[] { input });

            dx -= stepX;
            dy -= stepY;
        }
    }

    // ---------------- 光标锚点：拖拽宏期间不让光标跑出画布 ----------------

    private NativeMethods.POINT _cursorAnchor;
    private bool _cursorAnchored;

    /// <summary>记录当前光标位置，作为拖拽宏的锚点。</summary>
    public void BeginCursorAnchor()
    {
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT point))
        {
            _cursorAnchor = point;
            _cursorAnchored = true;
        }
    }

    /// <summary>把光标放回宏开始前的位置。</summary>
    public void EndCursorAnchor()
    {
        if (!_cursorAnchored)
        {
            return;
        }

        NativeMethods.SetCursorPos(_cursorAnchor.X, _cursorAnchor.Y);
        _cursorAnchored = false;
    }

    /// <summary>
    /// 检查光标是否还在锚点附近。返回 false 表示这一帧不应该再移动
    /// （已经到边界且配置为不回绕）。
    /// </summary>
    public bool AllowCursorMove(int radius, bool wrap, KeyChord? dragButton)
    {
        if (!_cursorAnchored)
        {
            return true;
        }

        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT current))
        {
            return true;
        }

        int dx = Math.Abs(current.X - _cursorAnchor.X);
        int dy = Math.Abs(current.Y - _cursorAnchor.Y);
        if (dx <= radius && dy <= radius)
        {
            return true;
        }

        if (!wrap)
        {
            return false;
        }

        // 关键：先松开鼠标键再归位，最后重新按下。
        // 归位那一下发生在"没有拖拽"的状态，目标软件收不到反向位移，
        // 所以不会出现画面跳回，只是无缝地开始新的一段拖拽。
        if (dragButton != null)
        {
            Release(dragButton);
        }

        NativeMethods.SetCursorPos(_cursorAnchor.X, _cursorAnchor.Y);

        if (dragButton != null)
        {
            Hold(dragButton);
        }

        return true;
    }

    private void Press(KeyChord chord, int direction)
    {
        var inputs = new List<NativeMethods.INPUT>(8);
        bool up = direction < 0;
        uint flags = up ? NativeMethods.KEYEVENTF_KEYUP : 0;

        if (!up)
        {
            foreach (ushort modifier in chord.Modifiers)
            {
                inputs.Add(Keyboard(modifier, 0));
            }
        }

        switch (chord.Mouse)
        {
            case MouseAction.WheelUp:
            case MouseAction.WheelDown:
            case MouseAction.WheelLeft:
            case MouseAction.WheelRight:
                if (!up)
                {
                    bool horizontal = chord.Mouse is MouseAction.WheelLeft or MouseAction.WheelRight;
                    int delta = chord.Mouse is MouseAction.WheelDown or MouseAction.WheelLeft
                        ? -NativeMethods.WHEEL_DELTA
                        : NativeMethods.WHEEL_DELTA;
                    inputs.Add(Mouse(0, 0, unchecked((uint)delta),
                        horizontal ? NativeMethods.MOUSEEVENTF_HWHEEL : NativeMethods.MOUSEEVENTF_WHEEL));
                }

                break;

            case MouseAction.Left:
            case MouseAction.Right:
            case MouseAction.Middle:
                inputs.Add(MouseButton(chord.Mouse, up));
                break;

            default:
                if (chord.VirtualKey != 0)
                {
                    inputs.Add(Keyboard(chord.VirtualKey, flags));
                }

                break;
        }

        if (up)
        {
            for (int i = chord.Modifiers.Count - 1; i >= 0; i--)
            {
                inputs.Add(Keyboard(chord.Modifiers[i], NativeMethods.KEYEVENTF_KEYUP));
            }
        }

        if (inputs.Count > 0)
        {
            Send(inputs.ToArray());
        }
    }

    private static NativeMethods.INPUT Keyboard(ushort vk, uint flags)
    {
        // 同时填 wVk 和扫描码：只填 wVk 时，部分软件（尤其靠扫描码判断修饰键的）
        // 收到的事件会被忽略，表现为"按键没反应"。
        ushort scan = (ushort)NativeMethods.MapVirtualKey(vk, 0);

        if (IsExtendedKey(vk))
        {
            flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        }

        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    dwFlags = flags,
                },
            },
        };
    }

    /// <summary>需要 EXTENDEDKEY 标志的键（否则方向键/编辑键会被识别成小键盘键）。</summary>
    private static bool IsExtendedKey(ushort vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 => true, // PgUp PgDn End Home Arrow*
        0x2D or 0x2E or 0x2C => true,                                       // Insert Delete PrintScreen
        0xA3 or 0xA5 => true,                                               // RControl RMenu
        0x5B or 0x5C => true,                                               // Win
        0x6F => true,                                                       // 小键盘 /
        _ => false,
    };

    private static NativeMethods.INPUT Mouse(int dx, int dy, uint data, uint flags) => new()
    {
        type = NativeMethods.INPUT_MOUSE,
        u = new NativeMethods.INPUTUNION
        {
            mi = new NativeMethods.MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                mouseData = data,
                dwFlags = flags,
            },
        },
    };

    private static NativeMethods.INPUT MouseButton(MouseAction action, bool up) => action switch
    {
        MouseAction.Left => Mouse(0, 0, 0, up ? NativeMethods.MOUSEEVENTF_LEFTUP : NativeMethods.MOUSEEVENTF_LEFTDOWN),
        MouseAction.Right => Mouse(0, 0, 0, up ? NativeMethods.MOUSEEVENTF_RIGHTUP : NativeMethods.MOUSEEVENTF_RIGHTDOWN),
        _ => Mouse(0, 0, 0, up ? NativeMethods.MOUSEEVENTF_MIDDLEUP : NativeMethods.MOUSEEVENTF_MIDDLEDOWN),
    };

    private void Send(NativeMethods.INPUT[] inputs)
    {
        try
        {
            NativeMethods.SendInput((uint)inputs.Length, inputs, _inputSize);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TourXbox] SendInput 失败: {ex.Message}");
        }
    }
}

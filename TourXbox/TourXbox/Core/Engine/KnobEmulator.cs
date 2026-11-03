using System;

namespace TourXbox.Core.Engine;

/// <summary>
/// 虚拟旋钮。把摇杆的连续偏移量换算成"档位"（每个档位 = 一次按键），
/// 这是让手柄接近 TourBox 旋钮手感的核心理法：
///
///   偏移量 → 径向死区 → 重标定到 0~1 → tick/秒 = Max × t^Curve → 累加器出档
///
/// 轻推 ≈ 每秒 3~4 档（微调 1px），推满 ≈ 每秒 13 档（快速扫），
/// 换向时立刻清空累加器，避免来回抖出多余档位。
/// </summary>
public sealed class KnobEmulator
{
    /// <summary>触发所需的最小偏移（径向死区）。</summary>
    public double Deadzone { get; set; } = 0.16;

    /// <summary>回中判定阈值（低于死区，用于滞回，防止在死区边缘抖动）。</summary>
    public double ReleaseThreshold { get; set; } = 0.09;

    /// <summary>响应曲线指数。&gt;1 表示轻推更细腻、推满更快。</summary>
    public double Curve { get; set; } = 1.8;

    /// <summary>推满时每秒产生的档位数。</summary>
    public double MaxTicksPerSecond { get; set; } = 13.0;

    /// <summary>触发后每秒最少产生的档位数（避免肉眼可见的卡顿）。</summary>
    public double MinTicksPerSecond { get; set; } = 3.0;

    private double _accumulator;
    private int _direction;

    /// <summary>当前方向：-1 / 0 / +1。</summary>
    public int Direction => _direction;

    public void Reset()
    {
        _accumulator = 0;
        _direction = 0;
    }

    /// <summary>
    /// 推进一帧。
    /// </summary>
    /// <param name="raw">轴原始值 -1~+1。</param>
    /// <param name="dt">距上一帧的秒数。</param>
    /// <param name="direction">本帧出档的方向（-1 / 0 / +1）。</param>
    /// <returns>本帧应触发的档位数量（0 或更多）。</returns>
    public int Update(float raw, double dt, out int direction)
    {
        direction = _direction;

        if (dt <= 0)
        {
            return 0;
        }

        double magnitude = Math.Abs(raw);

        // 滞回：已经在推 → 用更低的阈值判断是否回中
        if (_direction != 0)
        {
            if (magnitude < ReleaseThreshold)
            {
                Reset();
                direction = 0;
                return 0;
            }
        }
        else if (magnitude < Deadzone)
        {
            _accumulator = 0;
            direction = 0;
            return 0;
        }

        int sign = raw > 0 ? 1 : -1;

        if (_direction == 0)
        {
            _direction = sign;
        }
        else if (_direction != sign)
        {
            // 换向：清空累加器，从新方向重新起步
            _direction = sign;
            _accumulator = 0;
        }

        double normalized = (magnitude - Deadzone) / Math.Max(1e-6, 1.0 - Deadzone);
        normalized = Math.Clamp(normalized, 0, 1);

        double ticksPerSecond = MaxTicksPerSecond * Math.Pow(normalized, Math.Max(0.1, Curve));
        if (ticksPerSecond < MinTicksPerSecond)
        {
            ticksPerSecond = MinTicksPerSecond;
        }

        _accumulator += ticksPerSecond * dt;

        int ticks = (int)_accumulator;
        if (ticks > 0)
        {
            _accumulator -= ticks;
        }

        // 防止长时间卡顿后一次性喷出大量按键
        if (ticks > 6)
        {
            ticks = 6;
        }

        direction = _direction;
        return ticks;
    }
}

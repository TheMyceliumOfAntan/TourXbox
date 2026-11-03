using System;
using System.Collections.Generic;

namespace TourXbox.Core.Engine;

public enum BindingMode
{
    /// <summary>未设置。</summary>
    None = 0,

    /// <summary>按下瞬间触发一次。</summary>
    Tap,

    /// <summary>按住期间保持按键按下，松开时释放。</summary>
    Hold,

    /// <summary>摇杆/扳机专用：按偏移量持续产生档位。</summary>
    Tick,

    /// <summary>摇杆专用：按住修饰键并相对移动鼠标（例如按住空格拖动画面）。</summary>
    Drag,

    /// <summary>摇杆专用：把摇杆打圈的角度换算成鼠标位移，用于旋转画布。</summary>
    Rotate,
}

/// <summary>一条映射：某个输入 → 某个动作。</summary>
public sealed class Binding
{
    public string Mode { get; set; } = "tap";

    /// <summary>tap / hold 使用的动作，例如 "Ctrl+Z"。</summary>
    public string? Action { get; set; }

    /// <summary>tick 正向（右 / 上）使用的动作。</summary>
    public string? Positive { get; set; }

    /// <summary>tick 负向（左 / 下）使用的动作。</summary>
    public string? Negative { get; set; }

    /// <summary>tick 推满时的档位速度（档/秒）。为空则用默认值。</summary>
    public double? Speed { get; set; }

    /// <summary>tick 的响应曲线。</summary>
    public double? Curve { get; set; }

    /// <summary>drag / rotate 模式下按住不放的键盘键，例如 "Space"、"R"。</summary>
    public string? Modifier { get; set; }

    /// <summary>drag / rotate 模式下按住不放的鼠标键，例如 "MouseLeft"。</summary>
    public string? MouseButton { get; set; }

    /// <summary>rotate 模式下，每旋转 1 度对应的鼠标水平位移（像素）。</summary>
    public double? DegreesToPixels { get; set; }

    /// <summary>
    /// rotate 步进模式：摇杆每转过这么多度，就按一次 Positive/Negative 键。
    /// 配合软件里手动分配的"顺时针/逆时针旋转"快捷键使用。
    /// </summary>
    public double? StepDegrees { get; set; }

    /// <summary>rotate 模式下反转旋转方向。</summary>
    public bool Invert { get; set; }

    /// <summary>
    /// 拖拽宏（平移/旋转）期间，鼠标离开锚点多少像素就停止或回绕。
    /// 防止光标跑出画布导致拖拽失效。
    /// </summary>
    public int? CursorRadius { get; set; }

    /// <summary>
    /// true = 到达半径后把光标拉回锚点继续（可无限拖，但画面会瞬移一下）；
    /// false = 到达半径就停下，等用户松手重推（无瞬移）。
    /// </summary>
    public bool Wrap { get; set; }

    /// <summary>界面上显示的人类可读说明。</summary>
    public string? Note { get; set; }

    public BindingMode ResolvedMode => Mode?.ToLowerInvariant() switch
    {
        "tap" => BindingMode.Tap,
        "hold" => BindingMode.Hold,
        "tick" => BindingMode.Tick,
        "drag" => BindingMode.Drag,
        "rotate" => BindingMode.Rotate,
        _ => BindingMode.None,
    };

    public Binding Clone() => new()
    {
        Mode = Mode,
        Action = Action,
        Positive = Positive,
        Negative = Negative,
        Speed = Speed,
        Curve = Curve,
        Modifier = Modifier,
        MouseButton = MouseButton,
        DegreesToPixels = DegreesToPixels,
        StepDegrees = StepDegrees,
        Invert = Invert,
        CursorRadius = CursorRadius,
        Wrap = Wrap,
        Note = Note,
    };
}

/// <summary>一套映射配置（对应一个软件）。</summary>
public sealed class Profile
{
    public string Name { get; set; } = "新配置";

    /// <summary>匹配的进程名（不含 .exe，小写，支持包含匹配）。</summary>
    public List<string> Processes { get; set; } = new();

    /// <summary>额外的窗口标题关键字（可空）。</summary>
    public List<string> TitleContains { get; set; } = new();

    /// <summary>输入名 → 映射。</summary>
    public Dictionary<string, Binding> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>来源文件（不落盘）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? FilePath { get; set; }

    /// <summary>是否为用户可编辑的内置预设。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBuiltIn { get; set; }

    public bool MatchesProcess(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        foreach (string pattern in Processes)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (processName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool MatchesTitle(string? title)
    {
        if (TitleContains.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(title))
        {
            return false;
        }

        foreach (string pattern in TitleContains)
        {
            if (!string.IsNullOrWhiteSpace(pattern) &&
                title.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public Binding? Get(string input)
        => Bindings.TryGetValue(input, out Binding? binding) ? binding : null;
}

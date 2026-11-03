using System;
using System.Collections.Generic;
using System.Linq;
using TourXbox.Interop;

namespace TourXbox.Core.Actions;

/// <summary>一个按键组合，例如 "Ctrl+Shift+Z"、"Alt+WheelUp"、"[", "Space"。</summary>
public sealed class KeyChord
{
    public static readonly KeyChord Empty = new(Array.Empty<ushort>(), 0, MouseAction.None, 0, string.Empty);

    private KeyChord(ushort[] modifiers, ushort virtualKey, MouseAction mouse, int wheelDelta, string raw)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
        Mouse = mouse;
        WheelDelta = wheelDelta;
        Raw = raw;
    }

    /// <summary>修饰键 VK 列表，按 Ctrl → Shift → Alt → Win 顺序排列。</summary>
    public IReadOnlyList<ushort> Modifiers { get; }

    /// <summary>主键 VK，0 表示没有键盘主键（纯鼠标动作）。</summary>
    public ushort VirtualKey { get; }

    public MouseAction Mouse { get; }

    public int WheelDelta { get; }

    /// <summary>原始配置文本。</summary>
    public string Raw { get; }

    public bool IsEmpty => VirtualKey == 0 && Mouse == MouseAction.None;

    public bool IsMouse => Mouse != MouseAction.None;

    public override string ToString() => string.IsNullOrEmpty(Raw) ? "（未设置）" : Raw;

    public static KeyChord Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        string raw = text.Trim();
        var modifiers = new List<ushort>();
        ushort vk = 0;
        var mouse = MouseAction.None;
        int wheelDelta = 0;

        foreach (string partRaw in raw.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = partRaw.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers.Add(Vk.Control);
                    continue;
                case "shift":
                    modifiers.Add(Vk.Shift);
                    continue;
                case "alt":
                case "menu":
                    modifiers.Add(Vk.Alt);
                    continue;
                case "win":
                case "lwin":
                    modifiers.Add(Vk.LWin);
                    continue;
            }

            if (TryParseMouse(part, out var mouseAction, out int delta))
            {
                mouse = mouseAction;
                wheelDelta = delta;
                continue;
            }

            if (TryParseKey(part, out ushort parsed))
            {
                vk = parsed;
            }
        }

        // 规范化修饰键顺序，避免按下/释放顺序抖动
        var ordered = modifiers
            .Distinct()
            .OrderBy(static m => Array.IndexOf(ModifierOrder, m))
            .ToArray();

        return new KeyChord(ordered, vk, mouse, wheelDelta, raw);
    }

    private static readonly ushort[] ModifierOrder =
    {
        Vk.Control, Vk.Shift, Vk.Alt, Vk.LWin,
    };

    private static bool TryParseMouse(string token, out MouseAction action, out int delta)
    {
        delta = NativeMethods.WHEEL_DELTA;
        switch (token.ToLowerInvariant())
        {
            case "wheelup":
            case "scrollup":
                action = MouseAction.WheelUp;
                return true;
            case "wheeldown":
            case "scrolldown":
                action = MouseAction.WheelDown;
                delta = -NativeMethods.WHEEL_DELTA;
                return true;
            case "wheelleft":
                action = MouseAction.WheelLeft;
                delta = -NativeMethods.WHEEL_DELTA;
                return true;
            case "wheelright":
                action = MouseAction.WheelRight;
                return true;
            case "mouseleft":
            case "lmb":
                action = MouseAction.Left;
                return true;
            case "mouseright":
            case "rmb":
                action = MouseAction.Right;
                return true;
            case "mousemiddle":
            case "mmb":
                action = MouseAction.Middle;
                return true;
            default:
                action = MouseAction.None;
                return false;
        }
    }

    private static bool TryParseKey(string token, out ushort vk)
    {
        vk = 0;

        if (token.Length == 1)
        {
            char c = token[0];
            if (c is >= 'a' and <= 'z')
            {
                vk = (ushort)(0x41 + (c - 'a'));
                return true;
            }

            if (c is >= 'A' and <= 'Z')
            {
                vk = (ushort)(0x41 + (c - 'A'));
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                vk = (ushort)(0x30 + (c - '0'));
                return true;
            }
        }

        if (Punctuation.TryGetValue(token, out vk) ||
            Punctuation.TryGetValue(token.ToLowerInvariant(), out vk))
        {
            return true;
        }

        if (token.StartsWith('F') || token.StartsWith('f'))
        {
            if (int.TryParse(token.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
            {
                vk = (ushort)(0x70 + fn - 1);
                return true;
            }
        }

        if (NamedKeys.TryGetValue(token.ToLowerInvariant(), out vk))
        {
            return true;
        }

        vk = 0;
        return false;
    }

    /// <summary>供录制界面反查键名使用的只读视图。</summary>
    public static IReadOnlyDictionary<string, ushort> PunctuationEntries => Punctuation;

    public static IReadOnlyDictionary<string, ushort> NamedKeyEntries => NamedKeys;

    private static readonly Dictionary<string, ushort> Punctuation = new(StringComparer.Ordinal)    {
        ["["] = 0xDB, ["bracketleft"] = 0xDB,
        ["]"] = 0xDD, ["bracketright"] = 0xDD,
        [","] = 0xBC, ["comma"] = 0xBC,
        ["."] = 0xBE, ["period"] = 0xBE,
        [";"] = 0xBA, ["semicolon"] = 0xBA,
        ["'"] = 0xDE, ["quote"] = 0xDE,
        ["/"] = 0xBF, ["slash"] = 0xBF,
        ["\\"] = 0xDC, ["backslash"] = 0xDC,
        ["-"] = 0xBD, ["minus"] = 0xBD,
        ["="] = 0xBB, ["equals"] = 0xBB,
        ["`"] = 0xC0, ["grave"] = 0xC0,
    };

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.Ordinal)
    {
        ["space"] = 0x20,
        ["enter"] = 0x0D,
        ["return"] = 0x0D,
        ["tab"] = 0x09,
        ["esc"] = 0x1B,
        ["escape"] = 0x1B,
        ["backspace"] = 0x08,
        ["delete"] = 0x2E,
        ["del"] = 0x2E,
        ["insert"] = 0x2D,
        ["home"] = 0x24,
        ["end"] = 0x23,
        ["pageup"] = 0x21,
        ["pagedown"] = 0x22,
        ["up"] = 0x26,
        ["down"] = 0x28,
        ["left"] = 0x25,
        ["right"] = 0x27,
        ["capslock"] = 0x14,
        ["printscreen"] = 0x2C,
        ["pause"] = 0x13,
    };
}

public enum MouseAction
{
    None = 0,
    WheelUp,
    WheelDown,
    WheelLeft,
    WheelRight,
    Left,
    Right,
    Middle,
}

/// <summary>虚拟键码常量。</summary>
public static class Vk
{
    public const ushort Shift = 0x10;
    public const ushort Control = 0x11;
    public const ushort Alt = 0x12;
    public const ushort LWin = 0x5B;
}

/// <summary>虚拟键码 → 可读名称（录制按键时用）。</summary>
public static class VkNames
{
    private static readonly Dictionary<ushort, string> Map = Build();

    public static string? Describe(ushort vk) => Map.TryGetValue(vk, out string? name) ? name : null;

    private static Dictionary<ushort, string> Build()
    {
        var map = new Dictionary<ushort, string>();

        for (int vk = 0x41; vk <= 0x5A; vk++)
        {
            map[(ushort)vk] = ((char)vk).ToString();
        }

        for (int vk = 0x30; vk <= 0x39; vk++)
        {
            map[(ushort)vk] = ((char)vk).ToString();
        }

        for (int i = 1; i <= 24; i++)
        {
            map[(ushort)(0x6F + i)] = "F" + i;
        }

        foreach (KeyValuePair<string, ushort> pair in KeyChord.PunctuationEntries)
        {
            string name = pair.Key.Length <= 2 ? pair.Key : Capitalize(pair.Key);
            if (!map.TryGetValue(pair.Value, out string? existing) || name.Length < existing.Length)
            {
                map[pair.Value] = name;
            }
        }

        foreach (KeyValuePair<string, ushort> pair in KeyChord.NamedKeyEntries)
        {
            if (!map.ContainsKey(pair.Value))
            {
                map[pair.Value] = Capitalize(pair.Key);
            }
        }

        return map;
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

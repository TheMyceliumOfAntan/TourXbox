using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TourXbox.Core.Actions;
using TourXbox.Core.Engine;
using TourXbox.Interop;

namespace TourXbox.SelfTest;

/// <summary>
/// 无手柄也能跑的核心逻辑自检：
///   1. 键名解析（键盘快捷键的字符串 → VK）
///   2. 虚拟旋钮算法（死区 / 档位频率 / 换向清零）
///   3. 全部预设 JSON 是否能解析、字段是否完整
/// 用法：dotnet run --project Tests/TourXbox.SelfTest
/// </summary>
internal static class Program
{
    private static int _failures;
    private static int _passes;

    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            return RunDiagnostics(args);
        }

        TestKeyChord();
        TestKnobEmulator();
        TestPresets();

        Console.WriteLine();
        Console.WriteLine($"通过 {_passes} 项，失败 {_failures} 项。");

        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 注入诊断：不经手柄，直接调用 ActionExecutor 往当前前台程序发动作，
    /// 用来单独验证"按键注入"和"旋转宏"在目标软件里是否真的有效。
    ///   --info                打印当前前台进程
    ///   --key "Ctrl+Z"        发送一次按键
    ///   --rotate [像素]       按住 Alt+Space + 左键横向拖拽（SAI 旋转画布）
    /// </summary>
    private static int RunDiagnostics(string[] args)
    {
        string mode = args[0].ToLowerInvariant();

        if (mode == "--info")
        {
            using var watcher = new ForegroundWatcher();
            Thread.Sleep(400);
            Console.WriteLine($"前台进程: {watcher.ProcessName ?? "未知"} | 标题: {watcher.WindowTitle}");
            Console.WriteLine($"本程序 PID: {Environment.ProcessId} | 是否自身在前台: {watcher.IsSelfForeground}");
            return 0;
        }

        var executor = new ActionExecutor();

        if (mode == "--key" && args.Length >= 2)
        {
            KeyChord chord = KeyChord.Parse(args[1]);
            Console.WriteLine($"发送: {args[1]} -> vk=0x{chord.VirtualKey:X2} 修饰={chord.Modifiers.Count}");
            executor.Tap(chord);
            return 0;
        }

        if (mode == "--orbitc")
        {
            // 绕"前台窗口客户区中心"转圈，用来验证软件是不是按光标相对中心的夹角算旋转
            int radius = args.Length >= 2 && int.TryParse(args[1], out int rad) ? rad : 240;
            int turns = args.Length >= 3 && int.TryParse(args[2], out int tn) ? tn : 1;

            using var watcher = new ForegroundWatcher();

            string? process = null;
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(200);
                process = watcher.ProcessName;
                if (process != null && process.Contains("sai", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            Console.WriteLine($"前台进程: {process ?? "未知"}");
            if (process == null || !process.Contains("sai", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("前台不是 SAI，已中止");
                return 2;
            }

            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT rc);
            var center = new NativeMethods.POINT { X = rc.Width / 2, Y = rc.Height / 2 };
            NativeMethods.ClientToScreen(hwnd, ref center);
            Console.WriteLine($"客户区 {rc.Width}x{rc.Height} 中心 ({center.X},{center.Y}) 半径 {radius} 圈 {turns}");

            NativeMethods.SetCursorPos(center.X + radius, center.Y);
            Thread.Sleep(150);

            KeyChord modifier = KeyChord.Parse("Alt+Space");
            KeyChord button = KeyChord.Parse("MouseLeft");
            executor.Hold(modifier);
            Thread.Sleep(80);
            executor.Hold(button);
            Thread.Sleep(100);

            int steps = 72 * turns;
            for (int i = 1; i <= steps; i++)
            {
                double a = i * 2 * Math.PI / 72;
                NativeMethods.SetCursorPos(
                    center.X + (int)Math.Round(radius * Math.Cos(a)),
                    center.Y + (int)Math.Round(radius * Math.Sin(a)));
                Thread.Sleep(12);
            }

            Thread.Sleep(60);
            executor.Release(button);
            executor.Release(modifier);
            Console.WriteLine("完成");
            return 0;
        }

        if (mode == "--orbit")
        {
            // 验证目标软件是否按"光标角度"旋转：按住修饰键+左键后让光标绕圈
            int radius = args.Length >= 2 && int.TryParse(args[1], out int rr) ? rr : 200;
            int turns = args.Length >= 3 && int.TryParse(args[2], out int tt) ? tt : 2;

            using var watcher = new ForegroundWatcher();

            string? process = null;
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(200);
                process = watcher.ProcessName;
                if (process != null && process.Contains("sai", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            Console.WriteLine($"前台进程: {process ?? "未知"}");
            if (process == null || !process.Contains("sai", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("前台不是 SAI，已中止");
                return 2;
            }

            NativeMethods.GetCursorPos(out NativeMethods.POINT origin);
            Console.WriteLine($"绕圈中心 ({origin.X},{origin.Y}) 半径 {radius} 圈数 {turns}");

            KeyChord modifier = KeyChord.Parse("Alt+Space");
            KeyChord button = KeyChord.Parse("MouseLeft");

            executor.Hold(modifier);
            Thread.Sleep(80);
            executor.Hold(button);
            Thread.Sleep(80);

            int steps = 36 * turns;
            for (int i = 1; i <= steps; i++)
            {
                double angle = i * 2 * Math.PI / 36;
                int x = origin.X + (int)(radius * Math.Cos(angle));
                int y = origin.Y + (int)(radius * Math.Sin(angle));
                NativeMethods.SetCursorPos(x, y);
                Thread.Sleep(16);
            }

            Thread.Sleep(80);
            executor.Release(button);
            executor.Release(modifier);
            NativeMethods.SetCursorPos(origin.X, origin.Y);

            Console.WriteLine("完成");
            return 0;
        }

        if (mode == "--drag" && args.Length >= 3)
        {
            // --drag "Space,Alt" 360  → 依次按住这些键，再按住左键横向拖拽
            string[] keys = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int pixels = int.TryParse(args[2], out int d) ? d : 360;

            Thread.Sleep(1200);

            using var watcher = new ForegroundWatcher();
            Thread.Sleep(400);
            string? process = watcher.ProcessName;
            Console.WriteLine($"前台进程: {process ?? "未知"}");

            if (process == null || !process.Contains("sai", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("前台不是 SAI，已中止");
                return 2;
            }

            KeyChord[] chords = keys.Select(KeyChord.Parse).ToArray();
            foreach (KeyChord chord in chords)
            {
                Console.WriteLine($"按住: {chord}");
                executor.Hold(chord);
                Thread.Sleep(50);
            }

            executor.Hold(KeyChord.Parse("MouseLeft"));
            Thread.Sleep(50);

            int moved = 0;
            while (moved < pixels)
            {
                executor.MoveMousePixels(12, 0);
                moved += 12;
                Thread.Sleep(16);
            }

            Thread.Sleep(60);
            executor.Release(KeyChord.Parse("MouseLeft"));

            for (int i = chords.Length - 1; i >= 0; i--)
            {
                executor.Release(chords[i]);
            }

            Console.WriteLine("完成");
            return 0;
        }

        if (mode == "--rotate")
        {
            int pixels = args.Length >= 2 && int.TryParse(args[1], out int p) ? p : 360;

            // 给调用方一点时间切回目标软件
            Thread.Sleep(1200);

            using var watcher = new ForegroundWatcher();
            Thread.Sleep(400);
            string? process = watcher.ProcessName;
            Console.WriteLine($"前台进程: {process ?? "未知"}");

            if (process == null || !process.Contains("sai", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("前台不是 SAI，已中止（避免误注入到别的窗口）");
                return 2;
            }

            KeyChord modifier = KeyChord.Parse("Alt+Space");
            KeyChord button = KeyChord.Parse("MouseLeft");

            Console.WriteLine($"按住 Alt+Space → 按下左键 → 横向移动 {pixels}px → 全部释放");
            executor.Hold(modifier);
            Thread.Sleep(60);
            executor.Hold(button);
            Thread.Sleep(60);

            const int step = 12;
            int moved = 0;
            while (moved < pixels)
            {
                executor.MoveMousePixels(step, 0);
                moved += step;
                Thread.Sleep(16);
            }

            Thread.Sleep(60);
            executor.Release(button);
            executor.Release(modifier);

            Console.WriteLine("完成");
            return 0;
        }

        Console.WriteLine("未知参数。可用: --info | --key <组合键> | --rotate [像素]");
        return 1;
    }

    // ---------------- 1. 键名解析 ----------------

    private static void TestKeyChord()
    {
        Section("键名解析");

        var chord = KeyChord.Parse("Ctrl+Shift+Z");
        Check("Ctrl+Shift+Z 解析出 2 个修饰键", chord.Modifiers.Count == 2);
        Check("Ctrl+Shift+Z 主键为 Z(0x5A)", chord.VirtualKey == 0x5A);
        Check("修饰键顺序为 Ctrl→Shift",
            chord.Modifiers[0] == Vk.Control && chord.Modifiers[1] == Vk.Shift);

        Check("单字符 '[' = 0xDB", KeyChord.Parse("[").VirtualKey == 0xDB);
        Check("别名 BracketRight = 0xDD", KeyChord.Parse("BracketRight").VirtualKey == 0xDD);
        Check("逗号 ',' = 0xBC", KeyChord.Parse(",").VirtualKey == 0xBC);
        Check("Space = 0x20", KeyChord.Parse("Space").VirtualKey == 0x20);
        Check("F5 = 0x74", KeyChord.Parse("F5").VirtualKey == 0x74);

        var wheel = KeyChord.Parse("Alt+WheelUp");
        Check("Alt+WheelUp 识别为鼠标滚轮",
            wheel.IsMouse && wheel.Mouse == MouseAction.WheelUp && wheel.WheelDelta == 120);

        Check("WheelDown 是负向滚动", KeyChord.Parse("WheelDown").WheelDelta == -120);
        Check("空字符串解析为空动作", KeyChord.Parse("").IsEmpty);
        Check("未知按键不崩溃", KeyChord.Parse("某某键").IsEmpty);
    }

    // ---------------- 2. 虚拟旋钮 ----------------

    private static void TestKnobEmulator()
    {
        Section("虚拟旋钮");

        var knob = new KnobEmulator { MaxTicksPerSecond = 13, Curve = 1.8 };

        // 死区内不出档
        int deadTicks = 0;
        for (int i = 0; i < 250; i++)
        {
            deadTicks += knob.Update(0.1f, 0.004, out _);
        }

        Check("死区(0.1)内不出档", deadTicks == 0, $"实际 {deadTicks}");

        // 推满 1 秒 ≈ 13 档
        knob.Reset();
        int fullTicks = 0;
        for (int i = 0; i < 250; i++)
        {
            fullTicks += knob.Update(1.0f, 0.004, out _);
        }

        Check("推满 1 秒约 13 档", fullTicks is >= 12 and <= 14, $"实际 {fullTicks}");

        // 轻推 1 秒 ≈ 3~5 档（微调区）
        knob.Reset();
        int lightTicks = 0;
        for (int i = 0; i < 250; i++)
        {
            lightTicks += knob.Update(0.25f, 0.004, out _);
        }

        Check("轻推(0.25) 1 秒 3~5 档", lightTicks is >= 3 and <= 5, $"实际 {lightTicks}");

        // 换向必须立刻清空累加器
        knob.Reset();
        knob.Update(1.0f, 0.004, out _);
        knob.Update(1.0f, 0.004, out _);
        int reverse = knob.Update(-1.0f, 0.004, out int dir);
        Check("换向当帧不出档（防抖）", reverse == 0 && dir == -1);

        // 回中后复位
        knob.Reset();
        for (int i = 0; i < 50; i++)
        {
            knob.Update(1.0f, 0.004, out _);
        }

        knob.Update(0.02f, 0.004, out _);
        Check("回中后方向归零", knob.Direction == 0);
    }

    // ---------------- 3. 预设文件 ----------------

    private static void TestPresets()
    {
        Section("预设配置");

        string directory = ResolvePresetDirectory();
        if (directory == null)
        {
            Check("找到 Presets 目录", false, "未找到，跳过");
            return;
        }

        Console.WriteLine($"  目录: {directory}");

        string[] files = Directory.GetFiles(directory, "*.json");
        Check("预设文件数量 >= 5", files.Length >= 5, $"实际 {files.Length}");

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            Profile? profile;
            try
            {
                profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(file), options);
            }
            catch (Exception ex)
            {
                Check($"{name} JSON 可解析", false, ex.Message);
                continue;
            }

            Check($"{name} JSON 可解析", profile != null);
            if (profile == null)
            {
                continue;
            }

            Check($"{name} 有名称和映射", !string.IsNullOrWhiteSpace(profile.Name) && profile.Bindings.Count > 0);

            foreach (KeyValuePair<string, Binding> pair in profile.Bindings)
            {
                Binding binding = pair.Value;
                string where = $"{name}:{pair.Key}";

                // 防回归：备注/动作里出现问号基本就是把中文写坏了
                foreach (string? text in new[]
                         {
                             binding.Action, binding.Positive, binding.Negative,
                             binding.Modifier, binding.MouseButton, binding.Note,
                         })
                {
                    Check($"{where} 文本无乱码", text == null || !text.Contains('?'), text);
                }

                switch (binding.ResolvedMode)
                {
                    case BindingMode.None:
                        Check($"{where} 模式合法", false, $"未知模式 '{binding.Mode}'");
                        break;
                    case BindingMode.Tap:
                    case BindingMode.Hold:
                        Check($"{where} 有动作", !string.IsNullOrWhiteSpace(binding.Action));
                        Check($"{where} 动作可解析", !KeyChord.Parse(binding.Action).IsEmpty, binding.Action);
                        break;
                    case BindingMode.Tick:
                        Check($"{where} 有正负向",
                            !string.IsNullOrWhiteSpace(binding.Positive) &&
                            !string.IsNullOrWhiteSpace(binding.Negative));
                        Check($"{where} 正向可解析", !KeyChord.Parse(binding.Positive).IsEmpty, binding.Positive);
                        Check($"{where} 负向可解析", !KeyChord.Parse(binding.Negative).IsEmpty, binding.Negative);
                        break;
                    case BindingMode.Drag:
                    {
                        Check($"{where} 有修饰键或鼠标键",
                            !string.IsNullOrWhiteSpace(binding.Modifier) ||
                            !string.IsNullOrWhiteSpace(binding.MouseButton));
                        break;
                    }

                    case BindingMode.Rotate:
                    {
                        bool dragMode = !string.IsNullOrWhiteSpace(binding.Modifier) ||
                                        !string.IsNullOrWhiteSpace(binding.MouseButton);
                        bool stepMode = !string.IsNullOrWhiteSpace(binding.Positive) &&
                                        !string.IsNullOrWhiteSpace(binding.Negative);

                        Check($"{where} 有拖拽参数或步进按键", dragMode || stepMode);

                        if (stepMode)
                        {
                            Check($"{where} 顺时针键可解析", !KeyChord.Parse(binding.Positive).IsEmpty, binding.Positive);
                            Check($"{where} 逆时针键可解析", !KeyChord.Parse(binding.Negative).IsEmpty, binding.Negative);
                        }

                        break;
                    }
                }
            }
        }
    }

    private static string? ResolvePresetDirectory()
    {
        string cwd = Directory.GetCurrentDirectory();
        foreach (string candidate in new[]
                 {
                     Path.Combine(cwd, "TourXbox", "TourXbox", "Presets"),
                     Path.Combine(cwd, "Presets"),
                 })
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        string? current = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (current == null)
            {
                break;
            }

            string candidate = Path.Combine(current, "Presets");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            string nested = Path.Combine(current, "TourXbox", "TourXbox", "Presets");
            if (Directory.Exists(nested))
            {
                return nested;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    // ---------------- 断言 ----------------

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title} ==");
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passes++;
            return;
        }

        _failures++;
        Console.WriteLine($"  [失败] {name}" + (detail != null ? $"  ({detail})" : string.Empty));
    }
}

using System;
using System.Collections.Generic;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TourXbox.Core.Actions;
using Windows.System;
using Windows.UI.Core;

namespace TourXbox;

/// <summary>
/// 按键录制对话框：进入后直接按键盘，捕获到的组合键实时显示，
/// 确认后返回给调用方。用户不需要手动输入 "Ctrl+Z" 这种文本。
/// </summary>
public sealed class KeyCaptureDialog : ContentDialog
{
    private readonly TextBlock _chordText;
    private readonly TextBlock _hint;
    private readonly Border _captureBox;

    private static readonly (string Label, string Value)[] Specials =
    {
        ("滚轮 上", "WheelUp"),
        ("滚轮 下", "WheelDown"),
        ("滚轮 左", "WheelLeft"),
        ("滚轮 右", "WheelRight"),
        ("鼠标 左键", "MouseLeft"),
        ("鼠标 中键", "MouseMiddle"),
        ("鼠标 右键", "MouseRight"),
    };

    /// <summary>录制结果；null 表示没录到就关闭了。</summary>
    public string? Captured { get; private set; }

    public KeyCaptureDialog(XamlRoot root, string title, string? initial)
    {
        XamlRoot = root;
        Title = title;
        PrimaryButtonText = "确认";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.None;
        IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(initial);

        _hint = new TextBlock
        {
            Text = "请在键盘上直接按下要绑定的按键（组合键也可以）",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontSize = 13,
        };

        _chordText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(initial) ? "等待输入…" : initial,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _captureBox = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 18, 16, 18),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
            Child = _chordText,
            IsTabStop = true,
        };

        var special = new ComboBox
        {
            Header = "或者选择鼠标 / 滚轮动作",
            PlaceholderText = "不使用",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        foreach ((string label, string _) in Specials)
        {
            special.Items.Add(label);
        }

        special.SelectionChanged += (_, _) =>
        {
            int index = special.SelectedIndex;
            if (index < 0 || index >= Specials.Length)
            {
                return;
            }

            Captured = Specials[index].Value;
            _chordText.Text = Captured;
            IsPrimaryButtonEnabled = true;
        };

        Content = new StackPanel
        {
            Spacing = 12,
            Width = 360,
            Children = { _hint, _captureBox, special },
        };

        Opened += (_, _) => _captureBox.Focus(FocusState.Programmatic);
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        ushort vk = (ushort)e.Key;

        if (IsModifier(vk))
        {
            e.Handled = true;
            _chordText.Text = PreviewModifiers() + " + …";
            return;
        }

        string? name = VkNames.Describe(vk);
        if (name == null)
        {
            return;
        }

        string chord = BuildChord(name);
        Captured = chord;
        _chordText.Text = chord;
        IsPrimaryButtonEnabled = true;
        e.Handled = true;
    }

    private static bool IsModifier(ushort vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static string PreviewModifiers()
    {
        var parts = new List<string>();

        if (IsDown(VirtualKey.Control))
        {
            parts.Add("Ctrl");
        }

        if (IsDown(VirtualKey.Shift))
        {
            parts.Add("Shift");
        }

        if (IsDown(VirtualKey.Menu))
        {
            parts.Add("Alt");
        }

        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            parts.Add("Win");
        }

        return parts.Count == 0 ? "" : string.Join(" + ", parts);
    }

    private static string BuildChord(string keyName)
    {
        var parts = new List<string>();

        if (IsDown(VirtualKey.Control))
        {
            parts.Add("Ctrl");
        }

        if (IsDown(VirtualKey.Shift))
        {
            parts.Add("Shift");
        }

        if (IsDown(VirtualKey.Menu))
        {
            parts.Add("Alt");
        }

        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            parts.Add("Win");
        }

        parts.Add(keyName);
        return string.Join("+", parts);
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}

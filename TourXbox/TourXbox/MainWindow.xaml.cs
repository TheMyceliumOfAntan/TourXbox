using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TourXbox.Core;
using TourXbox.Core.Engine;
using TourXbox.Core.Input;
using Windows.Graphics;

namespace TourXbox;

/// <summary>映射表里的一行。</summary>
public sealed class BindingRow : INotifyPropertyChanged
{
    private readonly Binding _binding;

    public BindingRow(string input, Binding binding)
    {
        Input = input;
        _binding = binding;
        InputLabel = InputLabels.TryGetValue(input, out string? label) ? label : input;
        ModeLabel = binding.ResolvedMode switch
        {
            BindingMode.Tick => "档位",
            BindingMode.Hold => "按住",
            BindingMode.Drag => "拖拽",
            BindingMode.Rotate => "打圈",
            BindingMode.Tap => "单击",
            _ => "未设置",
        };

        bool isTick = binding.ResolvedMode == BindingMode.Tick;
        ActionVisibility = isTick ? Visibility.Collapsed : Visibility.Visible;
        NegVisibility = isTick ? Visibility.Visible : Visibility.Collapsed;
        PosVisibility = isTick ? Visibility.Visible : Visibility.Collapsed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Input { get; }

    public string InputLabel { get; }

    public string ModeLabel { get; }

    public Visibility ActionVisibility { get; }

    public Visibility NegVisibility { get; }

    public Visibility PosVisibility { get; }

    public Binding Binding => _binding;

    /// <summary>列表里显示的当前绑定，例如 "Ctrl+Z" 或 "← [  → ]"。</summary>
    public string DisplayText => _binding.ResolvedMode switch
    {
        BindingMode.Tick => $"← {Show(_binding.Negative)}    → {Show(_binding.Positive)}",
        BindingMode.Drag => $"{Show(_binding.Modifier)} + 左键拖拽",
        BindingMode.Rotate => string.IsNullOrWhiteSpace(_binding.Positive)
            ? $"{Show(_binding.Modifier)} + 左键拖拽"
            : $"← {Show(_binding.Negative)}    → {Show(_binding.Positive)}",
        _ => Show(_binding.Action),
    };

    private static string Show(string? text) => string.IsNullOrWhiteSpace(text) ? "（未设置）" : text!;

    /// <summary>把录制到的按键写进对应字段。</summary>
    public void ApplyCaptured(string side, string value)
    {
        switch (side)
        {
            case "negative":
                _binding.Negative = value;
                break;
            case "positive":
                _binding.Positive = value;
                break;
            case "modifier":
                _binding.Modifier = value;
                break;
            default:
                _binding.Action = value;
                break;
        }

        Raise(nameof(DisplayText));
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>输入名 → 人类可读标签。</summary>
    public static int OrderOf(string input) => Array.IndexOf(Order, input) is int index and >= 0
        ? index
        : 200 + Math.Abs(input.GetHashCode() % 100);

    /// <summary>映射表的显示顺序：先左手区，再右手区，最后组合层。</summary>
    public static string LabelOf(string input) =>
        InputLabels.TryGetValue(input, out string? label) ? label : input;

    private static readonly string[] Order =
    {
        "LX", "LY", "PAN", "L3+PAN",
        "LB+ROTATE", "LT+ROTATE",
        "DPadLeft", "DPadRight", "DPadUp", "DPadDown",
        "View", "Menu",
        "A", "B", "X", "Y",
        "L3", "R3",
        "LB+LX", "LB+LY", "LB+DPadLeft", "LB+DPadRight", "LB+DPadUp", "LB+DPadDown",
        "LB+A", "LB+B", "LB+X", "LB+Y",
        "RB+A", "RB+B", "RB+X", "RB+Y", "RB+RX", "RB+RY",
    };

    private static readonly Dictionary<string, string> InputLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LX"] = "左摇杆 ←→",
        ["LY"] = "左摇杆 ↑↓",
        ["RX"] = "右摇杆 ←→",
        ["RY"] = "右摇杆 ↑↓",
        ["PAN"] = "右摇杆（平移）",
        ["L3+PAN"] = "按住 L3 + 左摇杆",
        ["ROTATE"] = "左摇杆（旋转）",
        ["LB+ROTATE"] = "LB + 左摇杆打圈",
        ["LT+ROTATE"] = "LT + 左摇杆打圈",
        ["DPadUp"] = "十字键 ↑",
        ["DPadDown"] = "十字键 ↓",
        ["DPadLeft"] = "十字键 ←",
        ["DPadRight"] = "十字键 →",
        ["View"] = "View",
        ["Menu"] = "Menu",
        ["A"] = "A",
        ["B"] = "B",
        ["X"] = "X",
        ["Y"] = "Y",
        ["L3"] = "L3（按下左摇杆）",
        ["R3"] = "R3（按下右摇杆）",
        ["LB+LX"] = "LB + 左摇杆 ←→",
        ["LB+LY"] = "LB + 左摇杆 ↑↓",
        ["LB+DPadUp"] = "LB + 十字键 ↑",
        ["LB+DPadDown"] = "LB + 十字键 ↓",
        ["LB+DPadLeft"] = "LB + 十字键 ←",
        ["LB+DPadRight"] = "LB + 十字键 →",
        ["LB+A"] = "LB + A",
        ["LB+B"] = "LB + B",
        ["LB+X"] = "LB + X",
        ["LB+Y"] = "LB + Y",
        ["RB+A"] = "RB + A",
        ["RB+B"] = "RB + B",
        ["RB+X"] = "RB + X",
        ["RB+Y"] = "RB + Y",
    };}

public sealed partial class MainWindow : Window
{
    private const string AutoProfileLabel = "自动（跟随前台程序）";

    private readonly MappingService _service;
    private readonly ObservableCollection<BindingRow> _rows = new();
    private readonly DispatcherQueueTimer? _statusTimer;

    private bool _suppressCombo;
    private bool _exitRequested;
    private Profile? _shownProfile;

    public MainWindow(MappingService service)
    {
        _service = service;
        InitializeComponent();

        AppWindow.Resize(new SizeInt32(1180, 760));
        Title = "TourXbox";

        ApplyWindowIcon();

        BindingList.ItemsSource = _rows;
        ExtendsContentIntoTitleBar = false;

        AppWindow.Closing += OnAppWindowClosing;

        BuildProfileCombo();
        LoadSettingsIntoUi();
        RebuildRows();

        _service.StatusChanged += OnStatusChanged;
        _service.StateUpdated += OnStateUpdated;
        _service.Profiles.Changed += OnProfilesChanged;

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromMilliseconds(500);
        _statusTimer.Tick += (_, _) => UpdateStatusText();
        _statusTimer.Start();

        UpdateStatusText();
    }

    /// <summary>设置窗口图标（任务栏 / Alt+Tab 用它，不是 exe 内嵌图标）。</summary>
    private void ApplyWindowIcon()
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(path))
            {
                AppWindow.SetIcon(path);
            }
        }
        catch (Exception ex)
        {
            Core.Log.Write($"设置窗口图标失败: {ex.Message}");
        }
    }

    /// <summary>从托盘恢复窗口。</summary>
    public void ShowFromTray(){
        AppWindow.Show();
        Activate();
    }

    /// <summary>真正退出（绕过"关闭即最小化到托盘"）。</summary>
    public void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    private void OnAppWindowClosing(object sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            return;
        }

        // 关闭按钮 = 收回托盘，映射继续工作
        args.Cancel = true;
        AppWindow.Hide();
    }

    // ---------------- 顶栏 / 设置 ----------------

    private void BuildProfileCombo()
    {
        _suppressCombo = true;
        ProfileCombo.Items.Clear();
        ProfileCombo.Items.Add(AutoProfileLabel);

        foreach (Profile profile in _service.Profiles.Profiles)
        {
            ProfileCombo.Items.Add(profile.Name);
        }

        string? pinned = _service.Settings.Current.PinnedProfile;
        ProfileCombo.SelectedIndex = string.IsNullOrWhiteSpace(pinned)
            ? 0
            : Math.Max(0, ProfileCombo.Items.IndexOf(pinned));

        _suppressCombo = false;
    }

    public void RefreshSettingsUi() => LoadSettingsIntoUi();

    private void LoadSettingsIntoUi()
    {
        OsdCheck.IsChecked = _service.Settings.Current.OsdEnabled;
        AxisLockCheck.IsChecked = _service.Settings.Current.AxisLock;
        AutoStartCheck.IsChecked = _service.Settings.Current.AutoStart;
        PauseToggle.IsChecked = _service.Paused;
        PauseToggleText.Text = _service.Paused ? "继续映射" : "暂停映射";
        PausedBadge.Visibility = _service.Paused ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCombo || ProfileCombo.SelectedItem is not string selected)
        {
            return;
        }

        _service.SetPinnedProfile(selected == AutoProfileLabel ? null : selected);
        RebuildRows();
        UpdateStatusText();
    }

    private void PauseToggle_Click(object sender, RoutedEventArgs e) => TogglePause();

    public void TogglePause()
    {
        _service.TogglePause();
        PauseToggle.IsChecked = _service.Paused;
        PauseToggleText.Text = _service.Paused ? "继续映射" : "暂停映射";
        PausedBadge.Visibility = _service.Paused ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusText();
    }

    private void OsdCheck_Click(object sender, RoutedEventArgs e)
    {
        _service.Settings.Current.OsdEnabled = OsdCheck.IsChecked == true;
        _service.Settings.Save();

        if (OsdCheck.IsChecked == true && Application.Current is App app)
        {
            app.Osd?.ShowAction("示例 · 左摇杆 ←→", "]");
        }
    }

    private void AxisLockCheck_Click(object sender, RoutedEventArgs e)
    {
        _service.Settings.Current.AxisLock = AxisLockCheck.IsChecked == true;
        _service.Engine.AxisLock = _service.Settings.Current.AxisLock;
        _service.Settings.Save();
    }

    private void AutoStartCheck_Click(object sender, RoutedEventArgs e)
    {
        _service.Settings.Current.AutoStart = AutoStartCheck.IsChecked == true;
        _service.Settings.Save();
        _service.Settings.ApplyAutoStart();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        _service.Profiles.Load();
        BuildProfileCombo();
        RebuildRows();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_shownProfile == null)
        {
            StatusText.Text = "当前没有匹配到配置，无法保存。";
            return;
        }

        _service.Profiles.Save(_shownProfile);
        StatusText.Text = $"已保存：{_shownProfile.Name} → {_shownProfile.FilePath}";
    }

    // ---------------- 映射表 ----------------

    private void RebuildRows()
    {
        Profile? active = _service.ActiveProfile;

        // 即使当前没有匹配到程序，也让用户能看到/编辑配置
        Profile? profile = active ?? ResolveDisplayProfile();

        if (ReferenceEquals(profile, _shownProfile) && _rows.Count > 0)
        {
            return;
        }

        _shownProfile = profile;
        _rows.Clear();

        if (profile == null)
        {
            MappingHeader.Text = "映射（没有找到任何配置）";
            return;
        }

        string suffix = active == null ? "（未激活 · 仅编辑）" : string.Empty;
        MappingHeader.Text = $"映射 · {profile.Name}{suffix}";

        foreach (KeyValuePair<string, Binding> pair in profile.Bindings
                     .OrderBy(p => BindingRow.OrderOf(p.Key)))
        {
            _rows.Add(new BindingRow(pair.Key, pair.Value));
        }

        ApplyTooltips(profile);
    }

    /// <summary>
    /// 手柄示意图上的悬停提示：鼠标停在某个控件上就能看到它当前的绑定。
    /// </summary>
    private void ApplyTooltips(Profile? profile)
    {
        if (profile == null)
        {
            return;
        }

        ToolTipService.SetToolTip(StickLeftHost, BuildTip(profile, "左摇杆", ("←→", "LX"), ("↑↓", "LY")));
        ToolTipService.SetToolTip(StickRightHost, BuildTip(profile, "右摇杆", ("", "PAN")));
        ToolTipService.SetToolTip(DpadUp, BuildTip(profile, "十字键 ↑", ("", "DPadUp")));
        ToolTipService.SetToolTip(DpadDown, BuildTip(profile, "十字键 ↓", ("", "DPadDown")));
        ToolTipService.SetToolTip(DpadLeft, BuildTip(profile, "十字键 ←", ("", "DPadLeft")));
        ToolTipService.SetToolTip(DpadRight, BuildTip(profile, "十字键 →", ("", "DPadRight")));
        ToolTipService.SetToolTip(ButtonA, BuildTip(profile, "A", ("", "A")));
        ToolTipService.SetToolTip(ButtonB, BuildTip(profile, "B", ("", "B")));
        ToolTipService.SetToolTip(ButtonX, BuildTip(profile, "X", ("", "X")));
        ToolTipService.SetToolTip(ButtonY, BuildTip(profile, "Y", ("", "Y")));
        ToolTipService.SetToolTip(ButtonView, BuildTip(profile, "View", ("", "View")));
        ToolTipService.SetToolTip(ButtonMenu, BuildTip(profile, "Menu", ("", "Menu")));
        ToolTipService.SetToolTip(ShoulderLb, BuildLayerTip(profile, "LB"));
        ToolTipService.SetToolTip(ShoulderRb, BuildLayerTip(profile, "RB"));
        ToolTipService.SetToolTip(TriggerLt, BuildTip(profile, "LT（扳机 · 备用）", ("", "LT+ROTATE")));
        ToolTipService.SetToolTip(TriggerRt, "RT（扳机）\n默认未绑定，可在配置里自定义");
    }

    /// <summary>一个控件一行：标签 + 动作 + 备注。</summary>
    private static string BuildTip(
        Profile profile,
        string title,
        params (string Label, string Key)[] rows)
    {
        var text = new System.Text.StringBuilder(title);

        foreach ((string label, string key) in rows)
        {
            text.Append('\n');
            if (label.Length > 0)
            {
                text.Append(label).Append("  ");
            }

            text.Append(DescribeInline(profile.Get(key)));
        }

        return text.ToString();
    }

    private static string BuildLayerTip(Profile profile, string prefix)
    {
        var entries = profile.Bindings
            .Where(pair => pair.Key.StartsWith(prefix + "+", StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => BindingRow.OrderOf(pair.Key))
            .Take(9)
            .ToList();

        if (entries.Count == 0)
        {
            return $"{prefix}（按住）\n这一层没有绑定";
        }

        var text = new System.Text.StringBuilder();
        text.Append(prefix).Append("（按住）");

        foreach (KeyValuePair<string, Binding> pair in entries)
        {
            text.Append('\n')
                .Append(BindingRow.LabelOf(pair.Key))
                .Append("：")
                .Append(Summarize(pair.Value));
        }

        return text.ToString();
    }

    private static string DescribeInline(Binding? binding)
    {
        if (binding == null)
        {
            return "未绑定";
        }

        string body = Summarize(binding);
        return string.IsNullOrWhiteSpace(binding.Note) ? body : $"{body} · {binding.Note}";
    }

    private static string Summarize(Binding binding) => binding.ResolvedMode switch
    {
        BindingMode.Tick => $"档位  ← {binding.Negative}   → {binding.Positive}",
        BindingMode.Hold => $"按住 {binding.Action}",
        BindingMode.Drag => $"拖拽（{binding.Modifier}+{binding.MouseButton}+移动）",
        BindingMode.Rotate => !string.IsNullOrWhiteSpace(binding.Positive)
            ? $"步进旋转  逆 {binding.Negative} / 顺 {binding.Positive}"
            : $"打圈旋转（{binding.Modifier}+{binding.MouseButton}）",
        _ => binding.Action ?? "未设置",
    };

    // ---------------- 改键 / 帮助 ----------------

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not BindingRow row)
        {
            return;
        }

        if (Content.XamlRoot == null)
        {
            return;
        }

        string side = button.Tag as string ?? "action";
        if (side == "action" && row.Binding.ResolvedMode is BindingMode.Drag or BindingMode.Rotate)
        {
            side = "modifier";
        }

        string initial = side switch
        {
            "negative" => row.Binding.Negative ?? string.Empty,
            "positive" => row.Binding.Positive ?? string.Empty,
            "modifier" => row.Binding.Modifier ?? string.Empty,
            _ => row.Binding.Action ?? string.Empty,
        };

        string suffix = side switch
        {
            "negative" => " · 负向（← / ↓）",
            "positive" => " · 正向（→ / ↑）",
            "modifier" => " · 修饰键",
            _ => string.Empty,
        };

        var dialog = new KeyCaptureDialog(Content.XamlRoot, row.InputLabel + suffix, initial);
        ContentDialogResult result = await dialog.ShowAsync();

        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(dialog.Captured))
        {
            return;
        }

        row.ApplyCaptured(side, dialog.Captured!);

        if (_shownProfile != null)
        {
            _service.Profiles.Save(_shownProfile);
            StatusText.Text = $"已保存：{_shownProfile.Name}";
        }
    }

    private async void Help_Click(object sender, RoutedEventArgs e)
    {
        if (Content.XamlRoot == null)
        {
            return;
        }

        Profile? profile = _shownProfile;

        var dialog = new ContentDialog
        {
            Title = "使用方法",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };

        var panel = new StackPanel { Spacing = 14, Width = 560 };

        panel.Children.Add(new TextBlock
        {
            Text =
                $"· 只有在你指定的绘图软件位于前台时才会生效（当前前台：{_service.Foreground.ProcessName ?? "未知"}）。\n" +
                $"· 任何时候按 {TrayIcon.ActiveHotkeyLabel} 暂停/恢复映射，或点右上角「暂停映射」。\n" +
                $"· 关掉窗口会收进托盘继续工作，右键托盘图标可退出。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            FontSize = 13,
        });

        if (profile == null)
        {
            panel.Children.Add(new TextBlock { Text = "当前没有匹配到任何配置。", Opacity = 0.7 });
        }
        else
        {
            if (profile.Name.Contains("Photoshop", StringComparison.OrdinalIgnoreCase))
            {
                panel.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 12, 14, 12),
                    Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                    Child = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Text =
                            "Photoshop 的「翻转画布」默认没有快捷键，需要设置一次（30 秒）：\n" +
                            "① 在 PS 里按 Ctrl+Shift+Alt+K 打开键盘快捷键\n" +
                            "② 展开「视图」，选中「水平翻转视图」\n" +
                            "③ 点它的快捷键栏，按 Ctrl+Alt+Shift+= ，点「接受」\n" +
                            "④ 「垂直翻转视图」同样设成 Ctrl+Alt+Shift+-\n" +
                            "设完直接按 Y 就能翻转；液化（Ctrl+Shift+X）和反相（Ctrl+I）本来就有，不用设。",
                    },
                });
            }

            foreach ((string title, List<KeyValuePair<string, Binding>> items) in GroupForHelp(profile))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = title,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 14,
                    Margin = new Thickness(0, 6, 0, 0),
                });

                foreach (KeyValuePair<string, Binding> pair in items)
                {
                    var line = new Grid { ColumnSpacing = 12 };
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var name = new TextBlock
                    {
                        Text = BindingRow.LabelOf(pair.Key),
                        FontSize = 13,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                    };

                    var desc = new TextBlock
                    {
                        Text = DescribeForHelp(pair.Value),
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    };

                    Grid.SetColumn(desc, 1);
                    line.Children.Add(name);
                    line.Children.Add(desc);
                    panel.Children.Add(line);
                }
            }
        }

        dialog.Content = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        await dialog.ShowAsync();
    }

    private static IEnumerable<(string Title, List<KeyValuePair<string, Binding>> Items)> GroupForHelp(Profile profile)
    {
        var basic = new List<KeyValuePair<string, Binding>>();
        var stick = new List<KeyValuePair<string, Binding>>();
        var lb = new List<KeyValuePair<string, Binding>>();
        var rb = new List<KeyValuePair<string, Binding>>();

        foreach (KeyValuePair<string, Binding> pair in profile.Bindings.OrderBy(p => BindingRow.OrderOf(p.Key)))
        {
            string key = pair.Key;

            if (key is "PAN" or "L3+PAN" || key.EndsWith("+ROTATE", StringComparison.OrdinalIgnoreCase))
            {
                stick.Add(pair);
            }
            else if (key.StartsWith("LB+", StringComparison.OrdinalIgnoreCase))
            {
                lb.Add(pair);
            }
            else if (key.StartsWith("RB+", StringComparison.OrdinalIgnoreCase))
            {
                rb.Add(pair);
            }
            else
            {
                basic.Add(pair);
            }
        }

        if (stick.Count > 0)
        {
            yield return ("摇杆拖拽 / 旋转", stick);
        }

        yield return ("基础层（直接操作）", basic);

        if (lb.Count > 0)
        {
            yield return ("LB 层（按住 LB）", lb);
        }

        if (rb.Count > 0)
        {
            yield return ("RB 层（按住 RB）", rb);
        }
    }

    private static string DescribeForHelp(Binding binding)
    {
        string body = Summarize(binding);
        return string.IsNullOrWhiteSpace(binding.Note) ? body : $"{binding.Note}（{body}）";
    }

    private Profile? ResolveDisplayProfile()
    {
        string? pinned = _service.Settings.Current.PinnedProfile;
        if (!string.IsNullOrWhiteSpace(pinned))
        {
            Profile? pinnedProfile = _service.Profiles.FindByName(pinned);
            if (pinnedProfile != null)
            {
                return pinnedProfile;
            }
        }

        return _service.Profiles.Profiles.FirstOrDefault();
    }

    private void OnProfilesChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            BuildProfileCombo();
            _shownProfile = null;
            RebuildRows();
        });
    }

    private void OnStatusChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _shownProfile = null;
            RebuildRows();
            UpdateStatusText();
        });
    }

    private void UpdateStatusText()
    {
        FooterHint.Text = $"{TrayIcon.ActiveHotkeyLabel} 暂停 / 恢复 · View 撤销 · Menu 重做";

        bool connected = _service.Input.IsConnected;
        ConnectionDot.Fill = new SolidColorBrush(connected
            ? Windows.UI.Color.FromArgb(255, 76, 195, 138)
            : Windows.UI.Color.FromArgb(255, 196, 66, 59));
        ConnectionText.Text = connected ? "手柄已连接" : "未连接手柄";

        string process = _service.Foreground.ProcessName ?? "未知";
        Profile? active = _service.ActiveProfile;

        if (_service.Paused)
        {
            StatusText.Text = $"已暂停 · 前台：{process}";
            return;
        }

        StatusText.Text = active == null
            ? $"前台：{process} · 未匹配任何配置（不会注入按键）"
            : $"前台：{process} · 生效配置：{active.Name}";
    }

    // ---------------- 手柄实时状态 ----------------

    private void OnStateUpdated(PadState state)
    {
        DispatcherQueue.TryEnqueue(() => UpdateVisual(state));
    }

    private void UpdateVisual(PadState state)
    {
        StickLeftShift.X = state.Lx * 14;
        StickLeftShift.Y = -state.Ly * 14;
        StickLeftKnob.Opacity = state.IsDown(PadButton.LeftThumb) ? 1.0 : 0.55;

        StickRightShift.X = state.Rx * 14;
        StickRightShift.Y = -state.Ry * 14;
        StickRightKnob.Opacity = state.IsDown(PadButton.RightThumb) ? 1.0 : 0.55;

        SetOn(ShoulderLb, state.IsDown(PadButton.LeftShoulder), 0.4);
        SetOn(ShoulderRb, state.IsDown(PadButton.RightShoulder), 0.4);
        SetOn(ButtonA, state.IsDown(PadButton.A), 0.35);
        SetOn(ButtonB, state.IsDown(PadButton.B), 0.35);
        SetOn(ButtonX, state.IsDown(PadButton.X), 0.35);
        SetOn(ButtonY, state.IsDown(PadButton.Y), 0.35);
        SetOn(ButtonView, state.IsDown(PadButton.Back), 0.35);
        SetOn(ButtonMenu, state.IsDown(PadButton.Start), 0.35);
        SetOn(DpadUp, state.IsDown(PadButton.DPadUp), 0.3);
        SetOn(DpadDown, state.IsDown(PadButton.DPadDown), 0.3);
        SetOn(DpadLeft, state.IsDown(PadButton.DPadLeft), 0.3);
        SetOn(DpadRight, state.IsDown(PadButton.DPadRight), 0.3);

        TriggerLt.Opacity = 0.22 + state.Lt * 0.7;
        TriggerRt.Opacity = 0.22 + state.Rt * 0.7;

        if (!state.Connected)
        {
            ConnectionDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 66, 59));
            ConnectionText.Text = "未连接手柄";
        }
    }

    private static void SetOn(FrameworkElement element, bool on, double idleOpacity)
    {
        element.Opacity = on ? 1.0 : idleOpacity;
    }
}

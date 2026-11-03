using System;
using System.Runtime.InteropServices;
using System.Threading;
using TourXbox.Interop;

namespace TourXbox;

/// <summary>
/// 托盘图标 + 全局热键。WinUI 没有内置托盘，这里用一条独立线程
/// 跑传统 Win32 消息循环，避免和 XAML 的 UI 线程互相干扰。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int MenuShow = 101;
    private const int MenuPause = 102;
    private const int MenuAutoStart = 103;
    private const int MenuExit = 104;
    private const int HotkeyPause = 0x5458;

    /// <summary>按顺序尝试的暂停热键，前一个被占用就换下一个。</summary>
    private static readonly (uint Modifiers, uint VirtualKey, string Label)[] HotkeyCandidates =
    {
        (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x50, "Ctrl+Alt+P"),
        (NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_ALT, 0x50, "Ctrl+Shift+Alt+P"),
        (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x79, "Ctrl+Alt+F10"),
    };

    private static string _activeHotkeyLabel = "Ctrl+Alt+P";

    /// <summary>实际注册成功的暂停热键文本，界面用它显示提示。</summary>
    public static string ActiveHotkeyLabel => _activeHotkeyLabel;

    private delegate IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _gate = new();

    private WndProc? _wndProc;
    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _disposed;

    private volatile bool _paused;
    private volatile bool _autoStart;
    private volatile string _tooltip = "TourXbox - 手柄画图控制器";

    /// <summary>双击托盘图标 / 菜单"显示窗口"。</summary>
    public event Action? ShowRequested;

    /// <summary>菜单"暂停映射"或全局热键 Ctrl+Alt+P。</summary>
    public event Action? TogglePauseRequested;

    /// <summary>菜单"开机自启"。</summary>
    public event Action? ToggleAutoStartRequested;

    /// <summary>菜单"退出"。</summary>
    public event Action? ExitRequested;

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "TourXbox.Tray",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(2000);
    }

    public void UpdateState(bool paused, bool autoStart, string tooltip)
    {
        _paused = paused;
        _autoStart = autoStart;
        _tooltip = tooltip;
        ApplyTooltip();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        IntPtr hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(1500);
        _ready.Dispose();
    }

    // ---------------- Win32 消息循环 ----------------

    private void ThreadProc()
    {
        _wndProc = WindowProc;

        IntPtr hInstance = NativeMethods.GetModuleHandle(null);
        string className = "TourXbox.TrayHost." + Environment.ProcessId.ToString("X");

        var wc = new NativeMethods.WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = className,
        };

        if (NativeMethods.RegisterClass(ref wc) == 0)
        {
            Core.Log.Write($"托盘窗口类注册失败 (win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return;
        }

        _hwnd = NativeMethods.CreateWindowEx(
            0, className, "TourXbox", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            Core.Log.Write($"托盘隐藏窗口创建失败 (win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return;
        }

        _icon = LoadAppIcon();
        AddIcon();

        RegisterPauseHotkey();

        _ready.Set();

        while (NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_TRAYICON:
            {
                int mouseEvent = lParam.ToInt32() & 0xFFFF;
                if (mouseEvent is NativeMethods.WM_LBUTTONDBLCLK)
                {
                    ShowRequested?.Invoke();
                }
                else if (mouseEvent is NativeMethods.WM_RBUTTONUP or NativeMethods.WM_CONTEXTMENU)
                {
                    ShowMenu();
                }

                return IntPtr.Zero;
            }

            case NativeMethods.WM_HOTKEY:
            {
                if (wParam.ToInt32() == HotkeyPause)
                {
                    TogglePauseRequested?.Invoke();
                }

                return IntPtr.Zero;
            }

            case NativeMethods.WM_COMMAND:
            {
                switch (wParam.ToInt32() & 0xFFFF)
                {
                    case MenuShow:
                        ShowRequested?.Invoke();
                        break;
                    case MenuPause:
                        TogglePauseRequested?.Invoke();
                        break;
                    case MenuAutoStart:
                        ToggleAutoStartRequested?.Invoke();
                        break;
                    case MenuExit:
                        ExitRequested?.Invoke();
                        break;
                }

                return IntPtr.Zero;
            }

            case NativeMethods.WM_CLOSE:
            {
                NativeMethods.UnregisterHotKey(hWnd, HotkeyPause);
                var data = BuildIconData();
                NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
                NativeMethods.DestroyWindow(hWnd);
                return IntPtr.Zero;
            }

            case NativeMethods.WM_DESTROY:
            {
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
            }
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void RegisterPauseHotkey()
    {
        foreach ((uint modifiers, uint virtualKey, string label) in HotkeyCandidates)
        {
            if (NativeMethods.RegisterHotKey(_hwnd, HotkeyPause, modifiers, virtualKey))
            {
                _activeHotkeyLabel = label;
                Core.Log.Write($"托盘已启动，暂停热键 {label} 注册成功");
                return;
            }

            Core.Log.Write($"暂停热键 {label} 注册失败 (win32={Marshal.GetLastWin32Error()})，尝试下一个");
        }

        _activeHotkeyLabel = "（无，仅托盘菜单可用）";
        Core.Log.Write("所有候选暂停热键均注册失败，只能通过托盘/界面暂停");
    }

    // ---------------- 图标 / 菜单 ----------------

    private void AddIcon()
    {
        var data = BuildIconData();
        data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
        data.uCallbackMessage = NativeMethods.WM_TRAYICON;
        data.hIcon = _icon;
        data.szTip = Truncate(_tooltip, 120);
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data))
        {
            Core.Log.Write("托盘图标添加失败");
        }
    }

    private void ApplyTooltip()
    {
        if (_hwnd == IntPtr.Zero || _disposed)
        {
            return;
        }

        lock (_gate)
        {
            var data = BuildIconData();
            data.uFlags = NativeMethods.NIF_TIP;
            data.szTip = Truncate(_tooltip, 120);
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
        }
    }

    private NativeMethods.NOTIFYICONDATA BuildIconData() => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = Truncate(_tooltip, 120),
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void ShowMenu()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, MenuShow, "显示窗口");
        NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.AppendMenu(
            menu,
            NativeMethods.MF_STRING | (_paused ? NativeMethods.MF_CHECKED : 0),
            MenuPause,
            $"暂停映射\t{_activeHotkeyLabel}");
        NativeMethods.AppendMenu(
            menu,
            NativeMethods.MF_STRING | (_autoStart ? NativeMethods.MF_CHECKED : 0),
            MenuAutoStart,
            "开机自启");
        NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, MenuExit, "退出");

        NativeMethods.GetCursorPos(out NativeMethods.POINT point);
        NativeMethods.SetForegroundWindow(_hwnd);
        NativeMethods.TrackPopupMenu(
            menu,
            NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_BOTTOMALIGN,
            point.X,
            point.Y,
            0,
            _hwnd,
            IntPtr.Zero);
        NativeMethods.PostMessage(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        NativeMethods.DestroyMenu(menu);
    }

    private static IntPtr LoadAppIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                IntPtr icon = NativeMethods.ExtractIcon(IntPtr.Zero, exe, 0);
                if (icon != IntPtr.Zero && icon != (IntPtr)1)
                {
                    return icon;
                }
            }
        }
        catch
        {
            // 忽略，走默认图标
        }

        // IDI_APPLICATION
        return NativeMethods.LoadIcon(IntPtr.Zero, (IntPtr)32512);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];
}

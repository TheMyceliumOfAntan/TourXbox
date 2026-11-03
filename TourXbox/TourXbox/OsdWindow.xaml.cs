using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using TourXbox.Interop;
using Windows.Graphics;

namespace TourXbox;

/// <summary>
/// 屏幕下方的轻量动作提示。无边框、置顶、永不抢焦点，
/// 这样画图时弹出来不会打断笔触。
/// </summary>
public sealed partial class OsdWindow : Window
{
    private readonly DispatcherQueueTimer _hideTimer;
    private IntPtr _hwnd;

    public OsdWindow()
    {
        InitializeComponent();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        AppWindow.IsShownInSwitchers = false;

        _hideTimer = DispatcherQueue.CreateTimer();
        int durationMs = int.TryParse(
            Environment.GetEnvironmentVariable("TOURXBOX_OSD_MS"), out int custom) && custom > 0
            ? custom
            : 950;
        _hideTimer.Interval = TimeSpan.FromMilliseconds(durationMs);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => AppWindow.Hide();

        ApplyWindowStyles();
        AppWindow.Hide();
    }

    private void ApplyWindowStyles()
    {
        try
        {
            _hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);

            int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);

            int round = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(
                _hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            int dark = 1;
            NativeMethods.DwmSetWindowAttribute(
                _hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TourXbox] OSD 样式设置失败: {ex.Message}");
        }
    }

    /// <summary>在屏幕下方显示一个动作提示。</summary>
    public void ShowAction(string input, string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        try
        {
            InputText.Text = input;
            ActionText.Text = action;

            if (AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: false } p)
            {
                p.IsAlwaysOnTop = true;
            }

            RectInt32 area = DisplayArea.Primary.WorkArea;
            const int width = 400;
            const int height = 96;
            int x = area.X + ((area.Width - width) / 2);
            int y = area.Y + area.Height - height - 8;

            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            AppWindow.Show();

            _hideTimer.Stop();
            _hideTimer.Start();
        }
        catch (Exception ex)
        {
            Core.Log.Write($"OSD 显示失败: {ex.Message}");
        }
    }
}

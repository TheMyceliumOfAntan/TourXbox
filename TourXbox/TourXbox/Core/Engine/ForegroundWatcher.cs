using System;
using System.Diagnostics;
using System.Threading;
using TourXbox.Interop;

namespace TourXbox.Core.Engine;

/// <summary>每 150ms 采样一次前台窗口，给映射引擎判断"当前该用哪套配置"。</summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly Timer _timer;
    private readonly object _gate = new();
    private string? _processName;
    private string? _windowTitle;
    private IntPtr _hwnd;
    private uint _processId;
    private bool _isSelf;

    public ForegroundWatcher()
    {
        _timer = new Timer(_ => Poll(), null, 100, 150);
    }

    /// <summary>前台进程名（不带 .exe，小写）。</summary>
    public string? ProcessName
    {
        get { lock (_gate) { return _processName; } }
    }

    public string? WindowTitle
    {
        get { lock (_gate) { return _windowTitle; } }
    }

    public IntPtr WindowHandle
    {
        get { lock (_gate) { return _hwnd; } }
    }

    /// <summary>前台窗口是不是本程序自己（是的话绝不能注入按键）。</summary>
    public bool IsSelfForeground
    {
        get { lock (_gate) { return _isSelf; } }
    }

    /// <summary>前台发生变化时触发（在定时器线程上）。</summary>
    public event Action? ForegroundChanged;

    private void Poll()
    {
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);

            bool changed;
            string? processName = null;
            string? title = null;
            bool self = pid == (uint)Environment.ProcessId;

            if (pid != 0)
            {
                try
                {
                    using Process process = Process.GetProcessById((int)pid);
                    processName = process.ProcessName.ToLowerInvariant();
                    title = process.MainWindowTitle;
                }
                catch
                {
                    processName = null;
                    title = null;
                }
            }

            lock (_gate)
            {
                changed = pid != _processId
                    || !string.Equals(processName, _processName, StringComparison.Ordinal)
                    || !string.Equals(title, _windowTitle, StringComparison.Ordinal);

                _hwnd = hwnd;
                _processId = pid;
                _processName = processName;
                _windowTitle = title;
                _isSelf = self;
            }

            if (changed)
            {
                ForegroundChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TourXbox] 前台窗口采样失败: {ex.Message}");
        }
    }

    public void Dispose() => _timer.Dispose();
}

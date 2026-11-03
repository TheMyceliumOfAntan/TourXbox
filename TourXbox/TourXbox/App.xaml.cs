using System;
using System.Linq;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TourXbox.Core;
using TourXbox.Core.Engine;

namespace TourXbox
{
    /// <summary>
    /// 应用入口。负责装配：输入轮询 → 映射引擎 → 配置 → 界面 / 托盘 / OSD。
    /// </summary>
    public partial class App : Application
    {
        private const string MutexName = "TourXbox.SingleInstance.v1";
        private const string ShowEventName = "TourXbox.ShowWindow.v1";

        private Mutex? _mutex;
        private EventWaitHandle? _showEvent;

        private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
        private bool _lastPaused;
        private string? _lastProfileName;
        private TrayIcon? _tray;

        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
        }

        public MappingService Service { get; } = new();

        public MainWindow? Main { get; private set; }

        public OsdWindow? Osd { get; private set; }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                // 已有实例在跑：把它的窗口叫出来，然后自己退出（不要静默消失）
                Core.Log.Write("检测到已有 TourXbox 在运行，唤出它的窗口后退出本次启动");
                try
                {
                    EventWaitHandle.OpenExisting(ShowEventName).Set();
                }
                catch (Exception ex)
                {
                    Core.Log.Write($"唤出已有窗口失败: {ex.Message}");
                }

                Exit();
                return;
            }

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            StartShowListener();

            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            Service.Start();

            Osd = new OsdWindow();
            Main = new MainWindow(Service);

            _tray = new TrayIcon();
            _tray.ShowRequested += () => _dispatcher?.TryEnqueue(() => Main?.ShowFromTray());
            _tray.TogglePauseRequested += () => _dispatcher?.TryEnqueue(() => Main?.TogglePause());
            _tray.ToggleAutoStartRequested += () => _dispatcher?.TryEnqueue(ToggleAutoStart);
            _tray.ExitRequested += () => _dispatcher?.TryEnqueue(ExitApp);
            _tray.Start();

            Service.ActionFired += OnActionFired;
            Service.StatusChanged += OnStatusChanged;

            RefreshTrayState();

            bool startMinimized = Service.Settings.Current.StartMinimized
                || Environment.GetCommandLineArgs().Any(a =>
                    a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

            if (!startMinimized)
            {
                Main.Activate();
            }
        }

        /// <summary>监听"第二个实例请求显示窗口"的信号。</summary>
        private void StartShowListener()
        {
            var listener = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        if (_showEvent?.WaitOne() != true)
                        {
                            return;
                        }

                        _dispatcher?.TryEnqueue(() => Main?.ShowFromTray());
                    }
                    catch
                    {
                        return;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "TourXbox.ShowListener",
            };

            listener.Start();
        }

        private void OnActionFired(string input, string action, string? label)
        {
            if (!Service.Settings.Current.OsdEnabled)
            {
                return;
            }

            // 主标题用中文名（如“撤销”），副标题显示实际发送的按键
            string main = string.IsNullOrWhiteSpace(label) ? action : label!;
            string sub = string.IsNullOrWhiteSpace(label) ? input : $"{input} · {action}";

            _dispatcher?.TryEnqueue(() => Osd?.ShowAction(sub, main));
        }

        private void OnStatusChanged()
        {
            _dispatcher?.TryEnqueue(() =>
            {
                RefreshTrayState();
                ShowStateOsd();
            });
        }

        /// <summary>暂停/恢复、切换配置时用 OSD 给个即时反馈。</summary>
        private void ShowStateOsd()
        {
            if (Osd == null || !Service.Settings.Current.OsdEnabled)
            {
                return;
            }

            if (Service.Paused != _lastPaused)
            {
                _lastPaused = Service.Paused;
                Osd.ShowAction("映射状态", Service.Paused ? "已暂停" : "已恢复");
                return;
            }

            string? name = Service.ActiveProfile?.Name;
            if (!string.Equals(name, _lastProfileName, StringComparison.Ordinal))
            {
                _lastProfileName = name;
                Osd.ShowAction("配置", name ?? "无匹配 · 不注入按键");
            }
        }

        private void RefreshTrayState()
        {
            if (_tray == null)
            {
                return;
            }

            Profile? active = Service.ActiveProfile;
            string tooltip = Service.Paused
                ? "TourXbox - 已暂停"
                : active != null
                    ? $"TourXbox - {active.Name}"
                    : "TourXbox - 等待绘图软件";

            _tray.UpdateState(Service.Paused, Service.Settings.Current.AutoStart, tooltip);
        }

        private void ToggleAutoStart()
        {
            Service.Settings.Current.AutoStart = !Service.Settings.Current.AutoStart;
            Service.Settings.Save();
            Service.Settings.ApplyAutoStart();
            RefreshTrayState();
            Main?.RefreshSettingsUi();
        }

        private void ExitApp()
        {
            try
            {
                _tray?.Dispose();
                _tray = null;
            }
            catch
            {
                // 忽略
            }

            try
            {
                Service.Dispose();
            }
            catch
            {
                // 忽略
            }

            try
            {
                Osd?.Close();
            }
            catch
            {
                // 忽略
            }

            Main?.RequestExit();
            Exit();
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[TourXbox] 未处理异常: {e.Exception}");
        }
    }
}

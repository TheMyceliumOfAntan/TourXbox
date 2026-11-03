using System;
using System.Collections.Generic;
using System.Diagnostics;
using TourXbox.Core.Actions;
using TourXbox.Core.Engine;
using TourXbox.Core.Input;
namespace TourXbox.Core;

/// <summary>
/// 把输入、配置、前台检测和映射引擎串起来的总服务。
/// 除了事件回调之外，所有成员都可以跨线程访问。
/// </summary>
public sealed class MappingService : IDisposable
{
    private readonly ActionExecutor _executor = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _lastUiUpdateSecond;
    private ushort _previousLogButtons;
    private volatile Profile? _activeProfile;
    private volatile bool _paused;
    private volatile string? _lastInjectionError;

    public MappingService()
    {
        Profiles = new ProfileStore();
        Settings = new SettingsStore();
        Input = new XInputSource();
        Foreground = new ForegroundWatcher();
        Engine = new MappingEngine(_executor);

        Engine.Fired += (input, action, label) =>
        {
            LogThrottled(input, action);
            ActionFired?.Invoke(input, action, label);
        };

        Engine.Trace = message => Log.Write("引擎 " + message);
    }

    public ProfileStore Profiles { get; }

    public SettingsStore Settings { get; }

    public XInputSource Input { get; }

    public ForegroundWatcher Foreground { get; }

    public MappingEngine Engine { get; }

    /// <summary>高频手柄状态，UI 用（已限制在约 30fps）。</summary>
    public event Action<PadState>? StateUpdated;

    /// <summary>连接状态 / 当前配置发生变化。</summary>
    public event Action? StatusChanged;

    /// <summary>发出一个动作（用于 OSD 显示）：输入名、按键文本、中文名称。</summary>
    public event Action<string, string, string?>? ActionFired;

    public Profile? ActiveProfile => _activeProfile;

    /// <summary>暂停映射（不会影响手柄本身）。</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value)
            {
                return;
            }

            _paused = value;
            if (value)
            {
                Engine.ResetState();
            }

            StatusChanged?.Invoke();
        }
    }

    public void Start()
    {
        Settings.Load();
        Settings.ApplyAutoStart();
        Profiles.Load();

        Engine.AxisLock = Settings.Current.AxisLock;

        Input.StatePolled += OnStatePolled;
        Input.ConnectionChanged += _ => StatusChanged?.Invoke();
        Foreground.ForegroundChanged += OnForegroundChanged;
        Profiles.Changed += OnProfilesChanged;

        Input.Start();
    }

    public void SetPinnedProfile(string? profileName)
    {
        Settings.Current.PinnedProfile = profileName;
        Settings.Save();
        Engine.ResetState();
        OnForegroundChanged();
    }

    public void TogglePause() => Paused = !Paused;

    public void Dispose()
    {
        Input.StatePolled -= OnStatePolled;
        Foreground.ForegroundChanged -= OnForegroundChanged;
        Profiles.Changed -= OnProfilesChanged;
        _executor.ReleaseAll();
        Input.Dispose();
        Foreground.Dispose();
        Profiles.Dispose();
    }

    private void OnStatePolled(PadState state, double dt)
    {
        Profile? target = ResolveProfile();
        bool canInject = !Paused
            && target != null
            && !Foreground.IsSelfForeground
            && target.MatchesTitle(Foreground.WindowTitle);

        Engine.Profile = target;
        Engine.Active = canInject;

        LogButtonEdges(state, target, canInject, Foreground.ProcessName);

        try
        {
            Engine.Update(state, dt);
            _lastInjectionError = null;
        }
        catch (Exception ex)
        {
            _lastInjectionError = ex.Message;
            Debug.WriteLine($"[TourXbox] 映射执行异常: {ex}");
        }

        if (!ReferenceEquals(_activeProfile, target))
        {
            _activeProfile = target;
            StatusChanged?.Invoke();
        }

        double now = _clock.Elapsed.TotalSeconds;
        if (now - _lastUiUpdateSecond >= 0.033)
        {
            _lastUiUpdateSecond = now;
            StateUpdated?.Invoke(state);
        }
    }

    /// <summary>记录按键按下/松开，用于排查"按键有没有进来"。</summary>
    private readonly Dictionary<string, double> _lastLogSecond = new(StringComparer.Ordinal);

    private void LogThrottled(string input, string action)
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (_lastLogSecond.TryGetValue(input, out double last) && now - last < 0.2)
        {
            return;
        }

        _lastLogSecond[input] = now;
        Log.Write($"动作 {input} → {action}");
    }

    private void LogButtonEdges(PadState state, Profile? target, bool canInject, string? process)
    {
        ushort pressed = (ushort)(state.Buttons & ~_previousLogButtons);
        ushort released = (ushort)(_previousLogButtons & ~state.Buttons);
        _previousLogButtons = state.Buttons;

        if (pressed == 0 && released == 0)
        {
            return;
        }

        foreach (PadButton button in EnumButtons(pressed))
        {
            Log.Write(
                $"按键 {PadState.ButtonName(button)} 按下 | 生效={canInject} | 配置={target?.Name ?? "无"} | 前台={process ?? "?"}" +
                $" | 摇杆=({state.Lx:F2},{state.Ly:F2})");
        }

        foreach (PadButton button in EnumButtons(released))
        {
            Log.Write($"按键 {PadState.ButtonName(button)} 松开");
        }
    }

    private static IEnumerable<PadButton> EnumButtons(ushort mask)
    {
        foreach (PadButton candidate in new[]
                 {
                     PadButton.DPadUp, PadButton.DPadDown, PadButton.DPadLeft, PadButton.DPadRight,
                     PadButton.Start, PadButton.Back, PadButton.LeftThumb, PadButton.RightThumb,
                     PadButton.LeftShoulder, PadButton.RightShoulder,
                     PadButton.A, PadButton.B, PadButton.X, PadButton.Y,
                 })
        {
            if ((mask & (ushort)candidate) != 0)
            {
                yield return candidate;
            }
        }
    }

    private Profile? ResolveProfile()
    {
        string? pinned = Settings.Current.PinnedProfile;
        if (!string.IsNullOrWhiteSpace(pinned))
        {
            return Profiles.FindByName(pinned);
        }

        return Profiles.FindByProcess(Foreground.ProcessName, Foreground.WindowTitle);
    }

    private void OnForegroundChanged()
    {
        Profile? target = ResolveProfile();
        if (!ReferenceEquals(_activeProfile, target))
        {
            _activeProfile = target;
            Log.Write($"生效配置 → {target?.Name ?? "无（不注入按键）"} | 前台 {Foreground.ProcessName ?? "未知"}");
            StatusChanged?.Invoke();
        }
    }

    private void OnProfilesChanged()
    {
        Engine.ResetState();
        OnForegroundChanged();
    }
}

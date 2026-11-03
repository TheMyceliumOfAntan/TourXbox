using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32;

namespace TourXbox.Core;

/// <summary>程序设置（与映射配置分开存放）。</summary>
public sealed class AppSettings
{
    /// <summary>是否显示右下角的动作提示。</summary>
    public bool OsdEnabled { get; set; } = true;

    /// <summary>开机自启（写 HKCU Run 键）。</summary>
    public bool AutoStart { get; set; }

    /// <summary>摇杆轴锁定。</summary>
    public bool AxisLock { get; set; } = true;

    /// <summary>固定使用的配置名。null 表示按前台程序自动切换。</summary>
    public string? PinnedProfile { get; set; }

    /// <summary>启动时最小化到托盘。</summary>
    public bool StartMinimized { get; set; }
}

public sealed class SettingsStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "TourXbox";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SettingsStore()
    {
        SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TourXbox");
        FilePath = Path.Combine(SettingsDirectory, "settings.json");
    }

    public string SettingsDirectory { get; }

    public string FilePath { get; }

    public AppSettings Current { get; private set; } = new();

    public event Action? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TourXbox] 读取设置失败: {ex.Message}");
            Current = new AppSettings();
        }

        Changed?.Invoke();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TourXbox] 保存设置失败: {ex.Message}");
        }

        Changed?.Invoke();
    }

    /// <summary>按设置值同步注册表自启项。</summary>
    public void ApplyAutoStart()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                return;
            }

            if (Current.AutoStart)
            {
                string exe = Environment.ProcessPath ?? string.Empty;
                if (!string.IsNullOrEmpty(exe))
                {
                    key.SetValue(RunValueName, $"\"{exe}\" --tray");
                }
            }
            else if (key.GetValue(RunValueName) != null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TourXbox] 设置开机自启失败: {ex.Message}");
        }
    }

    public static string? ReadAutoStartExecutable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) as string;
        }
        catch
        {
            return null;
        }
    }
}

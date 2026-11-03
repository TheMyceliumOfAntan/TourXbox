using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace TourXbox.Core.Engine;

/// <summary>
/// 配置仓库。内置预设随程序发布在 Presets 目录，首次运行复制到
/// %APPDATA%\TourXbox\profiles 供用户自由修改；用户目录优先。
/// </summary>
public sealed class ProfileStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly List<Profile> _profiles = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public ProfileStore()
    {
        UserDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TourXbox",
            "profiles");

        BuiltInDirectory = Path.Combine(AppContext.BaseDirectory, "Presets");
    }

    public string UserDirectory { get; }

    public string BuiltInDirectory { get; }

    public event Action? Changed;

    public IReadOnlyList<Profile> Profiles
    {
        get
        {
            lock (_gate)
            {
                return _profiles.ToList();
            }
        }
    }

    public void Load()
    {
        Directory.CreateDirectory(UserDirectory);
        SeedUserDirectory();

        List<Profile> profiles = LoadProfiles();
        ApplyLoadedProfiles(profiles);

        foreach (Profile profile in profiles)
        {
            Log.Write($"已加载配置 {profile.Name}（{profile.Bindings.Count} 项）: {string.Join(",", profile.Bindings.Keys)}");
        }

        StartWatcher();
        Changed?.Invoke();
    }

    /// <summary>
    /// 合并内置预设与用户副本：
    /// 用户改过的（与内置内容不同）优先，没改过的用内置版本，
    /// 这样程序升级带上新预设时不会被旧副本永久遮蔽。
    /// </summary>
    private List<Profile> LoadProfiles()
    {
        var builtIns = ReadDirectory(BuiltInDirectory, isBuiltIn: true);
        var userCopies = ReadDirectory(UserDirectory, isBuiltIn: false);
        var result = new List<Profile>(builtIns);

        foreach (Profile userProfile in userCopies)
        {
            int index = result.FindIndex(p =>
                string.Equals(p.Name, userProfile.Name, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                result.Add(userProfile);
                continue;
            }

            if (userProfile.FilePath == null)
            {
                continue;
            }

            string builtInPath = Path.Combine(BuiltInDirectory, Path.GetFileName(userProfile.FilePath));
            bool userEdited = !File.Exists(builtInPath) || !FilesEqual(userProfile.FilePath, builtInPath);

            if (userEdited)
            {
                result[index] = userProfile;
            }
            else
            {
                // 用户没改过 → 用内置版本，并把用户副本同步成最新，方便直接打开改
                try
                {
                    File.Copy(builtInPath, userProfile.FilePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    Debug($"同步预设失败 {userProfile.FilePath}: {ex.Message}");
                }
            }
        }

        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ApplyLoadedProfiles(List<Profile> loaded)
    {
        lock (_gate)
        {
            _profiles.Clear();
            _profiles.AddRange(loaded);
        }
    }

    private static bool FilesEqual(string left, string right)
    {
        try
        {
            byte[] a = File.ReadAllBytes(left);
            byte[] b = File.ReadAllBytes(right);
            return a.AsSpan().SequenceEqual(b);
        }
        catch
        {
            return false;
        }
    }

    private static void Debug(string message) => System.Diagnostics.Debug.WriteLine($"[TourXbox] {message}");

    public void Save(Profile profile)
    {
        Directory.CreateDirectory(UserDirectory);

        string safeName = string.Join("_", profile.Name.Split(Path.GetInvalidFileNameChars()));
        string path = profile.FilePath != null && profile.FilePath.StartsWith(UserDirectory, StringComparison.OrdinalIgnoreCase)
            ? profile.FilePath
            : Path.Combine(UserDirectory, safeName + ".json");

        profile.FilePath = path;

        string json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(path, json);
    }

    public void SaveAll()
    {
        foreach (Profile profile in Profiles)
        {
            if (!profile.IsBuiltIn)
            {
                Save(profile);
            }
        }
    }

    public Profile? FindByProcess(string? processName, string? windowTitle)
    {
        lock (_gate)
        {
            return _profiles.FirstOrDefault(p =>
                p.MatchesProcess(processName) && p.MatchesTitle(windowTitle));
        }
    }

    public Profile? FindByName(string name)
    {
        lock (_gate)
        {
            return _profiles.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }

    private List<Profile> ReadDirectory(string directory, bool isBuiltIn)
    {
        var result = new List<Profile>();
        if (!Directory.Exists(directory))
        {
            return result;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                string json = File.ReadAllText(file);
                Profile? profile = JsonSerializer.Deserialize<Profile>(json, JsonOptions);
                if (profile == null)
                {
                    continue;
                }

                profile.FilePath = isBuiltIn ? null : file;
                profile.IsBuiltIn = isBuiltIn;
                profile.Bindings = new Dictionary<string, Binding>(
                    profile.Bindings, StringComparer.OrdinalIgnoreCase);
                result.Add(profile);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TourXbox] 读取配置失败 {file}: {ex.Message}");
            }
        }

        return result;
    }

    private void SeedUserDirectory()
    {
        if (!Directory.Exists(BuiltInDirectory))
        {
            return;
        }

        foreach (string source in Directory.EnumerateFiles(BuiltInDirectory, "*.json"))
        {
            string target = Path.Combine(UserDirectory, Path.GetFileName(source));
            if (File.Exists(target))
            {
                continue;
            }

            try
            {
                File.Copy(source, target);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TourXbox] 复制预设失败 {source}: {ex.Message}");
            }
        }
    }

    private void StartWatcher()
    {
        if (_watcher != null)
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(UserDirectory, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TourXbox] 配置监视器启动失败: {ex.Message}");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // 防抖：编辑器保存会连续触发多次
        _debounce?.Dispose();
        _debounce = new Timer(_ =>
        {
            try
            {
                ReloadFromDisk();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TourXbox] 热重载失败: {ex.Message}");
            }
        }, null, 350, Timeout.Infinite);
    }

    private void ReloadFromDisk()
    {
        List<Profile> loaded = LoadProfiles();
        ApplyLoadedProfiles(loaded);

        Log.Write($"配置已从磁盘重载，共 {loaded.Count} 套");

        Changed?.Invoke();
    }
}

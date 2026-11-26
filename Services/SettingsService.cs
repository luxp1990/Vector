using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Vector.Models;

namespace Vector.Services;

public class SettingsService : ISettingsService
{
    private readonly string _configPath;
    private readonly string _logPath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly SemaphoreSlim _logLock = new(1, 1);

    public string DataFolder { get; }

    public SettingsService(string dataFolder)
    {
        DataFolder = dataFolder;
        Directory.CreateDirectory(dataFolder);
        _configPath = AppPaths.ConfigPathIn(dataFolder);
        _logPath = AppPaths.LogPathIn(dataFolder);
    }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_configPath)) return new AppSettings();
            var json = await File.ReadAllTextAsync(_configPath, ct);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings != null)
            {
                settings.LocalShortcuts ??= new System.Collections.ObjectModel.ObservableCollection<ShortcutConfig>();
                settings.Pin ??= new PinSettings();
                settings.Coexistence ??= new HotkeyCoexistenceSettings();
                if (string.IsNullOrWhiteSpace(settings.Language))
                    settings.Language = "zh-CN";
                settings.Version = Math.Max(settings.Version, 4);
                return settings;
            }
        }
        catch (Exception ex)
        {
            await LogAsync($"加载配置失败: {ex.Message}", ct);
        }
        return new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        string? temporaryPath = null;
        bool lockAcquired = false;
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            await _saveLock.WaitAsync(ct);
            lockAcquired = true;
            temporaryPath = _configPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, ct);
            File.Move(temporaryPath, _configPath, overwrite: true);
            temporaryPath = null;
        }
        catch (Exception ex)
        {
            await LogAsync($"保存配置失败: {ex.Message}", ct);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
            if (lockAcquired)
                _saveLock.Release();
        }
    }

    public async Task LogAsync(string msg, CancellationToken ct = default)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}";

        await _logLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_logPath, line, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Vector LogAsync 失败] {ex.GetType().Name}: {ex.Message} | 原消息: {msg}");
        }
        finally
        {
            _logLock.Release();
        }
    }

    /// <summary>
    /// 切换便携模式：写/删标记并拷贝 config.json。返回提示文案；需重启后路径才切换。
    /// </summary>
    public static string PreparePortableModeSwitch(bool enablePortable, ILanguageService? languageService = null)
    {
        var exeDir = AppPaths.GetExeDirectory();
        var appData = AppPaths.GetAppDataDirectory();
        var marker = Path.Combine(exeDir, AppPaths.PortableMarkerFileName);
        var exeConfig = AppPaths.ConfigPathIn(exeDir);
        var appConfig = AppPaths.ConfigPathIn(appData);

        try
        {
            if (enablePortable)
            {
                Directory.CreateDirectory(exeDir);
                if (File.Exists(appConfig))
                    File.Copy(appConfig, exeConfig, overwrite: true);
                else if (!File.Exists(exeConfig))
                    File.WriteAllText(exeConfig, "{\n  \"Version\": 4,\n  \"LocalShortcuts\": [],\n  \"GlobalHotKey\": \"None\",\n  \"Theme\": \"Dark\",\n  \"RunAsAdmin\": false,\n  \"Pin\": {},\n  \"Coexistence\": {}\n}\n");
                File.WriteAllText(marker, "portable=1\n");
                return languageService?.GetString("Settings_Portable_Enabled")
                    ?? "已启用便携模式（配置将保存在程序目录）。请重启 Vector 后生效。";
            }

            if (File.Exists(marker))
                File.Delete(marker);

            // 关掉便携：把程序旁配置迁回 AppData，并改名 exe 旁文件，避免启动仍命中便携判定
            Directory.CreateDirectory(appData);
            if (File.Exists(exeConfig))
            {
                File.Copy(exeConfig, appConfig, overwrite: true);
                var bak = exeConfig + ".bak";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(exeConfig, bak);
            }

            return languageService?.GetString("Settings_Portable_Disabled")
                ?? "已关闭便携模式（配置改用用户目录）。请重启 Vector 后生效。";
        }
        catch (Exception ex)
        {
            return languageService?.Format("Settings_Portable_SwitchFailed_Format", ex.Message)
                ?? $"切换失败：{ex.Message}";
        }
    }
}

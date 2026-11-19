using Vector.Models;

namespace Vector.Services;

public interface ISettingsService
{
    /// <summary>当前数据目录（AppData 或程序目录）。</summary>
    string DataFolder { get; }

    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
    Task LogAsync(string msg, CancellationToken ct = default);
}
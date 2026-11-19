using System;
using System.IO;

namespace Vector.Services;

/// <summary>
/// 统一解析数据目录：程序旁有 config.json 或 Vector.portable → 便携；否则 %AppData%\Vector。
/// </summary>
public static class AppPaths
{
    public const string PortableMarkerFileName = "Vector.portable";
    public const string ConfigFileName = "config.json";
    public const string LogFileName = "Vector.log";

    public static string GetExeDirectory()
    {
        var exe = Environment.ProcessPath
                  ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        return Path.GetDirectoryName(exe)
               ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string GetAppDataDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vector");

    /// <summary>当前应使用的数据目录（启动时决定）。</summary>
    public static string ResolveDataDirectory()
    {
        var exeDir = GetExeDirectory();
        var portableMarker = Path.Combine(exeDir, PortableMarkerFileName);
        var portableConfig = Path.Combine(exeDir, ConfigFileName);
        if (File.Exists(portableMarker) || File.Exists(portableConfig))
            return exeDir;
        return GetAppDataDirectory();
    }

    public static bool IsPortableMode()
    {
        var exeDir = GetExeDirectory();
        return File.Exists(Path.Combine(exeDir, PortableMarkerFileName))
               || File.Exists(Path.Combine(exeDir, ConfigFileName));
    }

    public static string ConfigPathIn(string dataDir) => Path.Combine(dataDir, ConfigFileName);
    public static string LogPathIn(string dataDir) => Path.Combine(dataDir, LogFileName);
}

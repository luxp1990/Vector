namespace Vector.Models;

public class ShortcutConfig
{
    public string Key { get; set; } = string.Empty;
    public string Type { get; set; } = ShortcutTypes.Program;
    public string Target { get; set; } = string.Empty;

    /// <summary>独立参数（优先于从 Target 里解析出来的参数）。</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>工作目录；空则默认用程序所在目录。</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 运行策略（仅「程序」非脚本有意义）：
    /// New = 始终新开；Activate = 已开则前置否则启动（默认）；Toggle = 已开则最小化/恢复否则启动。
    /// </summary>
    public string RunStrategy { get; set; } = RunStrategies.Activate;
}

public static class RunStrategies
{
    public const string New = "New";
    public const string Activate = "Activate";
    public const string Toggle = "Toggle";

    public static string Normalize(string? value)
        => value switch
        {
            New => New,
            Toggle => Toggle,
            _ => Activate
        };
}

public static class ShortcutTypes
{
    public const string Program = "程序";
    public const string Folder = "文件夹";
    public const string Web = "网页";
    public const string File = "文件";

    public static string? ToResourceKey(string? type)
        => type switch
        {
            Program => "Type_Program",
            Folder => "Type_Folder",
            Web => "Type_Web",
            File => "Type_File",
            _ => null
        };
}

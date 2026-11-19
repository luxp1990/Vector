namespace Vector.Services;

public interface IFileTypeDetector
{
    string Detect(string target);
    string ResolveShortcut(string lnkPath);
    (string Target, string Arguments, string WorkingDirectory) ResolveShortcutDetails(string lnkPath);
    string ResolveUrlShortcut(string urlPath);
}
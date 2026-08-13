namespace WinDNLA.Core;

public static class AppPaths
{
    private static string? _rootOverride;

    /// <summary>Test hook to redirect AppData paths into a temp folder.</summary>
    public static void SetRootOverride(string? path) => _rootOverride = path;

    public static string Root =>
        _rootOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDNLA");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LibraryDb => Path.Combine(Root, "library.db");
    public static string ThumbsDir => Path.Combine(Root, "thumbs");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string CurrentLogFile => Path.Combine(LogsDir, $"windnla-{DateTime.Now:yyyy-MM-dd}.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ThumbsDir);
        Directory.CreateDirectory(LogsDir);
    }
}

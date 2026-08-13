using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WinDNLA.Core.Services;

public sealed class AutostartService
{
    private const string ShortcutName = "WinDNLA.lnk";
    private readonly SettingsService _settings;
    private readonly ILogger<AutostartService>? _logger;

    public AutostartService(SettingsService settings, ILogger<AutostartService>? logger = null)
    {
        _settings = settings;
        _logger = logger;
    }

    public static string StartupFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup));

    public static string ShortcutPath => Path.Combine(StartupFolder, ShortcutName);

    public bool IsEnabled => File.Exists(ShortcutPath);

    public void SyncFromSettings()
    {
        var enabled = _settings.Current.RunAtStartup;
        if (enabled) Enable();
        else Disable();
    }

    public void SetEnabled(bool enabled)
    {
        _settings.Update(s => s.RunAtStartup = enabled);
        if (enabled) Enable();
        else Disable();
    }

    public void Enable()
    {
        try
        {
            Directory.CreateDirectory(StartupFolder);
            var target = Environment.ProcessPath
                         ?? Path.Combine(AppContext.BaseDirectory, "WinDNLA.exe");
            CreateShortcut(ShortcutPath, target, "--quiet", Path.GetDirectoryName(target) ?? AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create startup shortcut");
        }
    }

    public void Disable()
    {
        try
        {
            if (File.Exists(ShortcutPath))
                File.Delete(ShortcutPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to remove startup shortcut");
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDirectory)
    {
        // WScript.Shell COM via dynamic / IWshRuntimeLibrary-free late binding
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("WScript.Shell недоступен.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        var shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.Arguments = arguments;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.WindowStyle = 7; // minimized
        shortcut.Description = "WinDNLA DLNA Server";
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon))
            shortcut.IconLocation = icon;
        shortcut.Save();
        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }
}

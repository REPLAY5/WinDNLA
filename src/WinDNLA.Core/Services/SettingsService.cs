using System.Text.Json;
using WinDNLA.Core.Models;

namespace WinDNLA.Core.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _lock = new();
    private AppSettings _settings = new();
    private bool _loaded;

    public event EventHandler? SettingsChanged;

    public AppSettings Current
    {
        get
        {
            EnsureLoaded();
            return Clone(_settings);
        }
    }

    public void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            AppPaths.EnsureCreated();
            if (File.Exists(AppPaths.SettingsFile))
            {
                try
                {
                    var json = File.ReadAllText(AppPaths.SettingsFile);
                    _settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                }
                catch
                {
                    _settings = new AppSettings();
                }
            }
            else
            {
                _settings = new AppSettings();
                SaveInternal(_settings);
            }
            _loaded = true;
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        lock (_lock)
        {
            EnsureLoadedUnlocked();
            mutate(_settings);
            SaveInternal(_settings);
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            _settings = Clone(settings);
            SaveInternal(_settings);
            _loaded = true;
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureLoadedUnlocked()
    {
        if (_loaded) return;
        AppPaths.EnsureCreated();
        if (File.Exists(AppPaths.SettingsFile))
        {
            try
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                _settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch
            {
                _settings = new AppSettings();
            }
        }
        else
        {
            _settings = new AppSettings();
            SaveInternal(_settings);
        }
        _loaded = true;
    }

    private static void SaveInternal(AppSettings settings)
    {
        AppPaths.EnsureCreated();
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(AppPaths.SettingsFile, json);
    }

    private static AppSettings Clone(AppSettings s)
    {
        var json = JsonSerializer.Serialize(s, JsonOptions);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }
}

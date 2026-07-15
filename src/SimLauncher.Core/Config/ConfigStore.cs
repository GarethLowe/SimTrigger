using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SimLauncher.Core.Config;

/// <summary>
/// Loads/saves %APPDATA%\SimLauncher\config.json and hot-reloads it when edited
/// externally. Invalid configs are surfaced via <see cref="ConfigError"/> and the
/// last-good config stays active — a bad edit never crashes the app.
/// </summary>
public sealed class ConfigStore : IDisposable
{
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SimLauncher", "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _path;
    private readonly ILogger<ConfigStore> _log;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _reloadDebounce;
    private bool _suppressWatcher;

    public LauncherConfig Current { get; private set; } = new();

    /// <summary>Fired after Current is replaced by a valid external edit or Save.</summary>
    public event Action<LauncherConfig>? ConfigChanged;

    /// <summary>Fired when a load/reload fails; carries the problems found.</summary>
    public event Action<IReadOnlyList<string>>? ConfigError;

    public ConfigStore(ILogger<ConfigStore> log, string? path = null)
    {
        _log = log;
        _path = path ?? DefaultPath;
    }

    public string Path => _path;

    /// <summary>Loads the config, creating a default file if none exists. Returns validation errors (empty = ok).</summary>
    public IReadOnlyList<string> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                _log.LogInformation("No config at {Path}; writing default sample config", _path);
                Current = DefaultConfigFactory.Create();
                SaveLocked();
                return Array.Empty<string>();
            }
            return LoadLocked();
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            SaveLocked();
        }
        ConfigChanged?.Invoke(Current);
    }

    /// <summary>Mutate the config under the store lock, then persist and notify.</summary>
    public void Update(Action<LauncherConfig> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
            SaveLocked();
        }
        ConfigChanged?.Invoke(Current);
    }

    public void StartWatching()
    {
        var dir = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_suppressWatcher)
            {
                return;
            }
            // Editors fire multiple change events; coalesce into one reload.
            _reloadDebounce?.Dispose();
            _reloadDebounce = new System.Threading.Timer(_ => Reload(), null, 500, Timeout.Infinite);
        }
    }

    private void Reload()
    {
        IReadOnlyList<string> errors;
        lock (_gate)
        {
            _log.LogInformation("Config file changed on disk; reloading");
            errors = LoadLocked();
        }
        if (errors.Count == 0)
        {
            ConfigChanged?.Invoke(Current);
        }
    }

    private IReadOnlyList<string> LoadLocked()
    {
        try
        {
            LauncherConfig? parsed = null;
            // The editor may still hold the file; retry briefly on sharing violations.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    parsed = JsonSerializer.Deserialize<LauncherConfig>(stream, JsonOptions);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(100);
                }
            }

            if (parsed is null)
            {
                var err = new[] { "Config file parsed to null." };
                ConfigError?.Invoke(err);
                return err;
            }

            var errors = ConfigValidator.Validate(parsed);
            if (errors.Count > 0)
            {
                _log.LogWarning("Config invalid; keeping previous config. Problems: {Problems}", string.Join("; ", errors));
                ConfigError?.Invoke(errors);
                return errors;
            }

            Current = parsed;
            if (MigrateMsfsRows(parsed))
            {
                _log.LogInformation("Migrated MSFS entries out of profile app lists into the 'msfs' config block");
                SaveLocked();
            }
            _log.LogInformation("Config loaded: {Profiles} profile(s), active '{Active}'",
                parsed.Profiles.Count, parsed.FindActiveProfile()?.Name);
            return errors;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load config from {Path}", _path);
            var err = new[] { $"Failed to load config: {ex.Message}" };
            ConfigError?.Invoke(err);
            return err;
        }
    }

    /// <summary>
    /// Older configs modelled MSFS as a managed app pinned to LauncherStart. The sim is
    /// now launched via the dedicated <see cref="LauncherConfig.Msfs"/> block, so any
    /// such rows are lifted out of the profiles. Returns true if anything changed.
    /// </summary>
    private static bool MigrateMsfsRows(LauncherConfig config)
    {
        var changed = false;
        foreach (var profile in config.Profiles)
        {
            var msfsRows = profile.Apps
                .Where(a => a.Checkpoint == Checkpoint.LauncherStart && LooksLikeMsfs(a))
                .ToList();
            foreach (var row in msfsRows)
            {
                if (changed == false)
                {
                    config.Msfs.Path = row.Path;
                    config.Msfs.Args = row.Args;
                    config.Msfs.ShellExecute = row.EffectiveShellExecute;
                }
                profile.Apps.Remove(row);
                changed = true;
            }
        }
        return changed;
    }

    private static bool LooksLikeMsfs(AppConfig app) =>
        app.Path.Contains("Microsoft.Limitless", StringComparison.OrdinalIgnoreCase)
        || app.Path.Contains("FlightSimulator", StringComparison.OrdinalIgnoreCase)
        || app.Path.Contains("rungameid/2537590", StringComparison.OrdinalIgnoreCase)
        || app.Name.StartsWith("MSFS", StringComparison.OrdinalIgnoreCase)
        || app.Name.Contains("Flight Simulator", StringComparison.OrdinalIgnoreCase);

    private void SaveLocked()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        _suppressWatcher = true;
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions));
        }
        finally
        {
            // Let the watcher settle before re-enabling reload-on-change.
            var timer = new System.Threading.Timer(_ =>
            {
                lock (_gate)
                {
                    _suppressWatcher = false;
                }
            }, null, 1000, Timeout.Infinite);
            _ = timer;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Dispose();
    }
}

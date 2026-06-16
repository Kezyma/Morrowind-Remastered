using System.IO;
using System.Text.Json;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Loads and persists <see cref="LauncherConfig"/> to disk. A single instance is
/// shared across the app; callers mutate <see cref="Current"/> and call Save().
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();

    /// <summary>The most recently loaded config service, for static helpers that can't take injection.</summary>
    public static ConfigService? Instance { get; private set; }

    public LauncherConfig Current { get; private set; } = new();

    /// <summary>Loads config from disk, seeding and backfilling from the bundled default.</summary>
    public void Load()
    {
        AppPaths.EnsureBaseDirectories();

        try
        {
            if (!File.Exists(AppPaths.ConfigFile))
            {
                var defaultJson = ReadDefaultConfigJson();
                if (defaultJson is not null)
                {
                    File.WriteAllText(AppPaths.ConfigFile, defaultJson);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't seed config from default: {ex.Message}");
        }

        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var json = File.ReadAllText(AppPaths.ConfigFile);
                var loaded = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions);
                if (loaded is not null)
                {
                    Current = loaded;
                }
            }
        }
        catch
        {
            Current = new LauncherConfig();
        }

        var toolsEmpty = Current.Tools.Count == 0 || Current.Tools.Values.All(l => l.Count == 0);
        var machineUrlEmpty = string.IsNullOrWhiteSpace(Current.InstallSource.MachineUrl);
        if (toolsEmpty || machineUrlEmpty)
        {
            var def = TryLoadDefault();
            if (def is not null)
            {
                if (toolsEmpty && def.Tools.Count > 0)
                {
                    Current.Tools = def.Tools;
                    Logger.Warn("Config backfill: seeded empty 'tools' from the bundled default.");
                }
                if (machineUrlEmpty && !string.IsNullOrWhiteSpace(def.InstallSource.MachineUrl))
                {
                    Current.InstallSource.MachineUrl = def.InstallSource.MachineUrl;
                    Logger.Warn("Config backfill: seeded empty 'installSource.machineUrl' from the bundled default.");
                }
            }
        }

        Instance = this;
    }

    /// <summary>Deserializes the bundled default config, or null if it can't be read/parsed.</summary>
    private static LauncherConfig? TryLoadDefault()
    {
        var json = ReadDefaultConfigJson();
        if (json is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't parse default config: {ex.Message}");
            return null;
        }
    }

    /// <summary>Returns the default-config JSON from the loose override or the embedded copy, else null.</summary>
    private static string? ReadDefaultConfigJson()
    {
        try
        {
            if (File.Exists(AppPaths.DefaultConfigFile))
            {
                return File.ReadAllText(AppPaths.DefaultConfigFile);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't read default config file: {ex.Message}");
        }

        try
        {
            var asm = typeof(ConfigService).Assembly;
            using var stream = asm.GetManifestResourceStream("config.default.json");
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't read embedded default config: {ex.Message}");
        }
        return null;
    }

    /// <summary>Atomically persists the current config to disk.</summary>
    public void Save()
    {
        lock (_gate)
        {
            AppPaths.EnsureBaseDirectories();
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            var tmp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
        }
    }
}

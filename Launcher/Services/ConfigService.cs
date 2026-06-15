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

    public LauncherConfig Current { get; private set; } = new();

    public void Load()
    {
        AppPaths.EnsureBaseDirectories();

        // First run: seed the live config from the bundled default so the whole
        // config (tools, MO2 paths, etc.) is present and editable. Note: editing
        // the bundled config.default.json only affects a fresh install or a config
        // with no tools (the fallback below) — an existing config.json keeps its
        // values. To re-seed during development, delete Config/config.json.
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
            // Corrupt config should never block startup; fall back to defaults.
            Current = new LauncherConfig();
        }

        // Backfill sections that older configs may lack from the bundled default,
        // so changes to the default take effect without forcing a re-seed:
        //   - Tools: legacy configs (pre-Tools) have none.
        //   - installSource.machineUrl: pins the "latest version" lookup; an empty
        //     value would otherwise fall back to per-edition (flipping between the
        //     two old lists when the edition is toggled).
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
    }

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

    /// <summary>
    /// Returns the default-config JSON: the loose <c>config.default.json</c> next to
    /// the exe if present, otherwise the copy embedded in the executable (so the
    /// defaults survive even if the loose file is deleted). Null if neither exists.
    /// </summary>
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

    public void Save()
    {
        lock (_gate)
        {
            AppPaths.EnsureBaseDirectories();
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            // Atomic write: write to temp then move over the target.
            var tmp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
        }
    }
}

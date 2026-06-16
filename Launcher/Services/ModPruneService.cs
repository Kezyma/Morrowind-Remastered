using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;
using System.Text;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>A single redundant mod file that prune would delete.</summary>
public sealed record PrunableFile(string ModName, string RelativePath, string FullPath, long Size);

/// <summary>The result of a read-only prune scan: the redundant files plus their total size/count.</summary>
public sealed record ModPruneAnalysis(IReadOnlyList<PrunableFile> Files, long TotalSize, int TotalCount)
{
    /// <summary>An empty analysis (nothing to prune).</summary>
    public static ModPruneAnalysis Empty { get; } = new(Array.Empty<PrunableFile>(), 0, 0);
}

/// <summary>The outcome of executing a prune: how many files were deleted, bytes freed, and how many failed.</summary>
public sealed record ModPruneResult(int DeletedCount, long DeletedSize, int FailedCount);

/// <summary>
/// Finds and deletes loose mod files that neither MO2 profile loads, to shrink the install footprint.
/// </summary>
/// <remarks>
/// A Morrowind Remastered install is one MO2 instance with two profiles (OpenMW / MWSE) sharing a single
/// <c>mods\</c> folder. The same loose asset is often shipped by several mods; the engine only ever sees the
/// highest-priority <em>enabled</em> mod's copy of each relative path (MO2's VFS overlay). The "winner" for a
/// path in a profile is therefore the only copy that profile loads. This service keeps, per relative path, the
/// winning copy in each profile and prunes every other loose copy — files overwritten in a profile, files of
/// mods enabled in only one profile (or disabled in both), and MO2 <c>.mohidden</c> files. Each mod's root
/// <c>meta.ini</c> is always kept (MO2 metadata, never deployed).
///
/// <para><b>Load order.</b> <c>modlist.txt</c> is in reverse priority order: the TOP line is the highest
/// priority and wins conflicts. We read it top-down and the first enabled mod that contains a path wins it.</para>
///
/// <para><b>Safety.</b> Deleting a file the winner maps prove is loaded by neither profile cannot change what
/// either profile presents to the game. <see cref="Analyze"/> aborts (prunes nothing) unless BOTH profiles'
/// <c>modlist.txt</c> are readable and non-empty, so a half-read load order can never trigger over-deletion.</para>
/// </remarks>
public sealed class ModPruneService
{
    private readonly ConfigService _config;
    private readonly InstallStateService _installState;

    /// <summary>Creates the service with its config and install-state dependencies.</summary>
    public ModPruneService(ConfigService config, InstallStateService installState)
    {
        _config = config;
        _installState = installState;
    }

    /// <summary>
    /// Scans the shared <c>mods\</c> folder and both profiles' load orders and returns the files that
    /// neither profile loads. Read-only and heavy (enumerates every mod folder) — run it off the UI thread.
    /// </summary>
    public ModPruneAnalysis Analyze(Edition edition, CancellationToken ct = default)
    {
        if (!_installState.IsMo2Present(edition))
        {
            return ModPruneAnalysis.Empty;
        }

        var installDir = _installState.GetEditionInstallDir(edition);
        var modsDir = AppPaths.Mo2ModsDir(installDir);
        if (!Directory.Exists(modsDir))
        {
            return ModPruneAnalysis.Empty;
        }

        var mo2Paths = _config.Current.Mo2Paths;
        var openModlist = AppPaths.ModlistTxt(installDir, mo2Paths.OpenMwProfile);
        var mwseModlist = AppPaths.ModlistTxt(installDir, mo2Paths.MwseProfile);
        return Analyze(modsDir, openModlist, mwseModlist, ct);
    }

    /// <summary>
    /// The pure, path-based prune analysis (no config/install-state dependency, so it is directly testable):
    /// scans <paramref name="modsDir"/> and both profiles' <c>modlist.txt</c> and returns the files neither
    /// profile loads. Aborts (returns <see cref="ModPruneAnalysis.Empty"/>) unless both load orders are
    /// readable and non-empty.
    /// </summary>
    public static ModPruneAnalysis Analyze(
        string modsDir, string openModlist, string mwseModlist, CancellationToken ct = default)
    {
        if (!Directory.Exists(modsDir))
        {
            return ModPruneAnalysis.Empty;
        }

        // SAFETY GUARD: both load orders must be readable, else "keep only winners" would over-prune
        // everything the unreadable profile would otherwise load.
        if (!File.Exists(openModlist) || !File.Exists(mwseModlist))
        {
            Logger.Warn("Prune skipped: both profiles' modlist.txt must exist " +
                        $"(\"{openModlist}\", \"{mwseModlist}\").");
            return ModPruneAnalysis.Empty;
        }

        var openEnabled = ParseEnabledModsInPriorityOrder(openModlist);
        var mwseEnabled = ParseEnabledModsInPriorityOrder(mwseModlist);
        if (openEnabled.Count == 0 || mwseEnabled.Count == 0)
        {
            Logger.Warn("Prune skipped: a profile's modlist.txt parsed to an empty enabled mod list " +
                        $"(OpenMW {openEnabled.Count}, MWSE {mwseEnabled.Count}).");
            return ModPruneAnalysis.Empty;
        }

        // (a) Enumerate every mod folder in parallel -> per-mod index of loose files (relPath -> full + size).
        //     Sizes come from the enumeration's find-data (no extra per-file stat) — that per-file stat was the
        //     dominant cost on a cold cache. Keep each mod's root meta.ini; always prune *.mohidden.
        var modFiles = new ConcurrentDictionary<string, Dictionary<string, (string Full, long Size)>>(
            StringComparer.OrdinalIgnoreCase);
        var hiddenPrunable = new ConcurrentBag<PrunableFile>();
        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true, // skip unreadable files instead of aborting the whole mod.
            AttributesToSkip = 0,      // include hidden/system, matching the legacy enumerator.
        };
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
            CancellationToken = ct,
        };

        Parallel.ForEach(Directory.GetDirectories(modsDir), parallelOptions, modDir =>
        {
            var modName = Path.GetFileName(modDir);
            var map = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var (full, size) in EnumerateFilesWithSize(modDir, enumOptions))
                {
                    var rel = Path.GetRelativePath(modDir, full);
                    if (rel.Equals("meta.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // MO2 metadata at the mod root — keep, do not index.
                    }

                    if (full.EndsWith(".mohidden", StringComparison.OrdinalIgnoreCase))
                    {
                        // Hidden by MO2 — never loaded. Prune directly so it can't "win" its own path.
                        hiddenPrunable.Add(new PrunableFile(modName, rel, full, size));
                        continue;
                    }

                    map[rel] = (full, size);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Prune scan skipped mod \"{modName}\": {ex.Message}");
            }
            modFiles[modName] = map;
        });

        var prunable = new List<PrunableFile>(hiddenPrunable);

        // (b) Per profile: winner map = the highest-priority enabled mod that contains each relative path.
        var openWinners = BuildWinners(openEnabled, modFiles);
        var mwseWinners = BuildWinners(mwseEnabled, modFiles);

        // (c) Keep only the winners: a file is prunable iff its mod wins its path in NEITHER profile.
        foreach (var (modName, files) in modFiles)
        {
            foreach (var (rel, info) in files)
            {
                var keptByOpen = openWinners.TryGetValue(rel, out var wo)
                                 && string.Equals(wo, modName, StringComparison.OrdinalIgnoreCase);
                var keptByMwse = mwseWinners.TryGetValue(rel, out var wm)
                                 && string.Equals(wm, modName, StringComparison.OrdinalIgnoreCase);
                if (!keptByOpen && !keptByMwse)
                {
                    prunable.Add(new PrunableFile(modName, rel, info.Full, info.Size));
                }
            }
        }

        long total = 0;
        foreach (var f in prunable)
        {
            total += f.Size;
        }
        return new ModPruneAnalysis(prunable, total, prunable.Count);
    }

    /// <summary>
    /// Re-derives a fresh analysis (so the bytes deleted equal the bytes measured), deletes each redundant
    /// file, then removes any now-empty subdirectories under each mod folder (mod roots and meta.ini survive).
    /// </summary>
    public ModPruneResult Prune(Edition edition, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var analysis = Analyze(edition, ct);
        if (analysis.TotalCount == 0)
        {
            return new ModPruneResult(0, 0, 0);
        }

        var deleted = 0;
        long deletedSize = 0;
        var failed = 0;
        var done = 0;
        foreach (var f in analysis.Files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Delete(f.FullPath);
                deleted++;
                deletedSize += f.Size;
            }
            catch (Exception ex)
            {
                failed++;
                Logger.Warn($"Couldn't prune \"{f.FullPath}\": {ex.Message}");
            }
            done++;
            progress.Report("Pruning", $"Deleting redundant files… {done}/{analysis.TotalCount}",
                done * 100.0 / analysis.TotalCount);
        }

        var modsDir = AppPaths.Mo2ModsDir(_installState.GetEditionInstallDir(edition));
        try
        {
            foreach (var modDir in Directory.GetDirectories(modsDir))
            {
                RemoveEmptySubdirectories(modDir);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Prune empty-folder cleanup failed: {ex.Message}");
        }

        Logger.Info($"Pruned {deleted} file(s), freed {Converters.ByteSizeConverter.Format(deletedSize)}" +
                    (failed > 0 ? $"; {failed} could not be deleted." : "."));
        return new ModPruneResult(deleted, deletedSize, failed);
    }

    /// <summary>
    /// Enumerates all files under <paramref name="root"/>, returning each file's full path and size straight
    /// from the directory find-data — no extra per-file metadata syscall (the cold-scan bottleneck).
    /// </summary>
    private static IEnumerable<(string FullPath, long Size)> EnumerateFilesWithSize(
        string root, EnumerationOptions options) =>
        new FileSystemEnumerable<(string, long)>(
            root,
            static (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Length),
            options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
        };

    /// <summary>Maps each relative path to the highest-priority enabled mod that contains it.</summary>
    private static Dictionary<string, string> BuildWinners(
        List<string> enabledInPriorityOrder,
        IReadOnlyDictionary<string, Dictionary<string, (string Full, long Size)>> modFiles)
    {
        var winner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var modName in enabledInPriorityOrder) // index 0 = highest priority = first to claim a path.
        {
            if (!modFiles.TryGetValue(modName, out var files))
            {
                continue; // enabled in the list but no folder on disk.
            }
            foreach (var rel in files.Keys)
            {
                if (!winner.ContainsKey(rel))
                {
                    winner[rel] = modName;
                }
            }
        }
        return winner;
    }

    /// <summary>
    /// Reads a profile's <c>modlist.txt</c> and returns its ENABLED mod names in priority order
    /// (index 0 = highest priority = top of file). Disabled (<c>-</c>) mods and separators are skipped.
    /// </summary>
    private static List<string> ParseEnabledModsInPriorityOrder(string modlistPath)
    {
        var result = new List<string>();
        if (!File.Exists(modlistPath))
        {
            return result;
        }

        try
        {
            foreach (var raw in File.ReadLines(modlistPath, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                if (line.EndsWith("_separator", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // MO2 separator row — not a real mod folder.
                }
                var prefix = line[0];
                if (prefix != '+' && prefix != '-')
                {
                    continue; // tolerate unknown line shapes.
                }
                if (prefix == '-')
                {
                    continue; // disabled in this profile — contributes nothing.
                }
                var name = line[1..].Trim();
                if (name.Length > 0)
                {
                    result.Add(name); // preserve file order = priority order.
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't read \"{modlistPath}\": {ex.Message}");
            return new List<string>();
        }

        return result;
    }

    /// <summary>Deletes empty subdirectories under <paramref name="root"/> without ever deleting the root.</summary>
    private static void RemoveEmptySubdirectories(string root)
    {
        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch
            {
            }
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Launches MO2 custom executables so they run inside MO2's virtual file system:
/// <c>ModOrganizer.exe -i "&lt;instance&gt;" -p "&lt;profile&gt;" "&lt;app name&gt;"</c>,
/// where the app name is the executable's title in ModOrganizer.ini. Both lists
/// are portable MO2 instances ("Portable"). Used by Play (launches the game),
/// the Tools panel (manual tool launches), and the Phase-4 automation.
///
/// MO2 is launched ELEVATED: some tools (Morrowind Code Patch, MGE XE) have
/// requireAdministrator manifests and MO2 can only start them when it is itself
/// elevated (otherwise the user sees "Error 5 ERROR_ACCESS_DENIED … MO elevated:
/// no"). When the launcher is already elevated (its own manifest requires admin)
/// MO2 inherits that token; otherwise it is relaunched via the "runas" verb.
/// </summary>
public sealed class Mo2LaunchService
{
    /// <summary>Portable MO2 instance name (both editions ship portable.txt).</summary>
    private const string InstanceName = "Portable";

    private readonly InstallStateService _installState;
    private readonly ConfigService _config;

    public Mo2LaunchService(InstallStateService installState, ConfigService config)
    {
        _installState = installState;
        _config = config;
    }

    /// <summary>
    /// Starts an MO2 executable by its ModOrganizer.ini title. When
    /// <paramref name="waitForExit"/> is true the returned task completes when MO2
    /// (and the launched tool) exit; otherwise it returns once started. Returns
    /// the started process.
    /// </summary>
    public async Task<Process> LaunchAsync(
        Edition edition, string appName, bool waitForExit, CancellationToken ct)
    {
        var installDir = _installState.GetEditionInstallDir(edition);
        var mo2 = AppPaths.Mo2Exe(installDir);
        if (!File.Exists(mo2))
        {
            throw new FileNotFoundException($"ModOrganizer.exe not found at {mo2}.");
        }

        // The list's ModSetup plugin kills MO2 on launch; ensure it's disabled.
        Mo2IniService.DisableModSetupPlugin(installDir);

        var elevated = IsProcessElevated();
        var psi = new ProcessStartInfo
        {
            FileName = mo2,
            WorkingDirectory = installDir,
            // Inherit our (elevated) token when already elevated; otherwise use
            // ShellExecute + runas so Windows elevates MO2 (UAC prompt).
            UseShellExecute = !elevated,
            Verb = elevated ? string.Empty : "runas"
        };
        var profileName = _config.Current.Mo2Paths.ProfileName(edition);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(InstanceName);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(profileName);
        // An empty appName opens the MO2 GUI itself (instance+profile, no tool).
        if (!string.IsNullOrEmpty(appName))
        {
            psi.ArgumentList.Add(appName);
        }

        Logger.Info($"Launching MO2 {(string.IsNullOrEmpty(appName) ? "(GUI)" : $"app \"{appName}\"")} " +
                    $"({edition}, instance \"{InstanceName}\", profile " +
                    $"\"{profileName}\", elevated={elevated}) via {mo2}.");
        var process = Process.Start(psi)
            ?? throw new IOException(
                $"Failed to start ModOrganizer.exe{(string.IsNullOrEmpty(appName) ? "" : $" for \"{appName}\"")}.");

        if (waitForExit)
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        return process;
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

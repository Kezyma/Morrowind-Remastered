using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Launches MO2 custom executables (by their ModOrganizer.ini title) so they run inside MO2's virtual file system.</summary>
/// <remarks>
/// Runs <c>ModOrganizer.exe -i "&lt;instance&gt;" -p "&lt;profile&gt;" "&lt;app name&gt;"</c>
/// for Play, the Tools panel, and the Phase-4 automation. MO2 is launched
/// ELEVATED because some tools (Morrowind Code Patch, MGE XE) require admin and
/// MO2 can only start them when itself elevated; an already-elevated launcher
/// passes its token through, otherwise MO2 is relaunched via the "runas" verb.
/// </remarks>
public sealed class Mo2LaunchService
{
    /// <summary>Resolves the install directory and profile for an edition.</summary>
    private readonly InstallStateService _installState;
    /// <summary>Persisted launcher config (MO2 instance and profile names).</summary>
    private readonly ConfigService _config;

    /// <summary>Creates the service over the install-state service and config.</summary>
    public Mo2LaunchService(InstallStateService installState, ConfigService config)
    {
        _installState = installState;
        _config = config;
    }

    /// <summary>Starts an MO2 executable by its ModOrganizer.ini title (empty title opens the MO2 GUI), disabling the list's MO2-killing ModSetup plugin first, optionally awaiting exit, and returns the process.</summary>
    public async Task<Process> LaunchAsync(
        Edition edition, string appName, bool waitForExit, CancellationToken ct)
    {
        var installDir = _installState.GetEditionInstallDir(edition);
        var mo2 = AppPaths.Mo2Exe(installDir);
        if (!File.Exists(mo2))
        {
            throw new FileNotFoundException($"ModOrganizer.exe not found at {mo2}.");
        }

        Mo2IniService.DisableModSetupPlugin(installDir);

        var elevated = IsProcessElevated();
        var psi = new ProcessStartInfo
        {
            FileName = mo2,
            WorkingDirectory = installDir,
            UseShellExecute = !elevated,
            Verb = elevated ? string.Empty : "runas"
        };
        var instanceName = _config.Current.Mo2.InstanceName;
        var profileName = _config.Current.Mo2Paths.ProfileName(edition);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(instanceName);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(profileName);
        if (!string.IsNullOrEmpty(appName))
        {
            psi.ArgumentList.Add(appName);
        }

        Logger.Info($"Launching MO2 {(string.IsNullOrEmpty(appName) ? "(GUI)" : $"app \"{appName}\"")} " +
                    $"({edition}, instance \"{instanceName}\", profile " +
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

    /// <summary>True when the current process is running with administrator rights.</summary>
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

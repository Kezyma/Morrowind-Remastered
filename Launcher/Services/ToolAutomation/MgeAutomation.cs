using System.Diagnostics;
using System.Text;
using System.Threading;
using MorrowindRemasteredLauncher.Models;
using static MorrowindRemasteredLauncher.Services.Win32Interop;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Drives MGE XE's distant-land generator entirely off-screen via window messages.
/// </summary>
/// <remarks>
/// MGE XE is a WinForms app: BUTTON controls respond to BM_CLICK and the tab control is
/// switched by posting a mouse-click at the tab header's rect (read cross-process via
/// TCM_GETITEMRECT). Buttons are matched by VISIBILITY — MGE keeps every wizard page's
/// controls as hidden children of one window, so only the current page's are visible.
/// During load-order selection, do NOT toggle "Use current load order": it unchecks
/// critical plugins. Generation is long (minutes); we poll until it settles, auto-dismiss
/// info dialogs, then kill the process. Every step fails soft — the overwrite harvest +
/// verifier remain the source of truth.
/// </remarks>
public static class MgeAutomation
{
    /// <summary>Runs the full off-screen distant-land generation wizard, then returns. Best-effort throughout.</summary>
    public static Task GenerateDistantLandAsync(
        MgeAutomationSettings settings, IProgress<InstallProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            try
            {
                var main = WaitForMgeMain(settings, TimeSpan.FromSeconds(settings.WindowWaitSeconds), ct);
                if (main == IntPtr.Zero)
                {
                    Logger.Warn("MGE automation: main window not found; generate distant land manually.");
                    return;
                }
                GetWindowThreadProcessId(main, out var pid);
                Logger.Info($"MGE automation: window found (pid {pid}).");
                MoveOffScreen(main);

                DismissDialogs((int)pid, settings.DismissLabels, settings);

                var tab = FindChildByClass(main, settings.TabClass);
                if (tab != IntPtr.Zero)
                {
                    ClickTabHeader(tab, settings.DistantLandTabIndex);
                    Thread.Sleep(settings.TabSwitchPauseMs);
                    DismissDialogs((int)pid, settings.DismissLabels, settings);
                }

                var gen = FindDescendantButton(main, settings.GeneratorWizardButton);
                if (gen == IntPtr.Zero)
                {
                    Logger.Warn($"MGE automation: \"{settings.GeneratorWizardButton}\" button not found; generate manually.");
                    return;
                }
                BmClick(gen);
                progress?.Report(new InstallProgress("Setup", "Generating distant land…", null, true));

                AdvanceUntilVisible((int)pid, settings.YesButton, settings.ContinueButton,
                    TimeSpan.FromSeconds(settings.YesToContinueWaitSeconds), settings, ct);
                var atGen = AdvanceUntilVisible((int)pid, settings.ContinueButton, settings.RunStepsButton,
                    TimeSpan.FromSeconds(settings.ContinueToRunWaitSeconds), settings, ct);
                if (!atGen)
                {
                    Logger.Warn("MGE automation: generation page not reached; generate manually.");
                    return;
                }
                ClickUntilGone((int)pid, settings.RunStepsButton,
                    TimeSpan.FromSeconds(settings.RunStepsWaitSeconds), settings, ct);
                Logger.Info("MGE automation: distant land generation started (this can take several minutes).");

                WaitForGenerationComplete((int)pid, settings, progress, ct);

                KillProcess((int)pid);
            }
            catch (Exception ex)
            {
                Logger.Warn($"MGE automation error (continuing): {ex.Message}");
            }
        }, ct);

    /// <summary>Waits for MGE XE's main window, dismissing the OK-only startup popups it shows before it opens.</summary>
    private static IntPtr WaitForMgeMain(MgeAutomationSettings settings, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            HideMo2Windows();

            foreach (var mge in Process.GetProcessesByName(settings.ProcessName))
            {
                using (mge)
                {
                    HideAllWindows(mge.Id);
                    DismissDialogs(mge.Id, settings.StartupDismissLabels, settings);
                }
            }

            var found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h))
                {
                    return true;
                }
                var t = new StringBuilder(256);
                GetWindowText(h, t, t.Capacity);
                if (t.ToString().Contains(settings.MainWindowTitle, StringComparison.OrdinalIgnoreCase))
                {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero)
            {
                return found;
            }
            Thread.Sleep(settings.MainPollMs);
        }
        return IntPtr.Zero;
    }

    /// <summary>Finds a visible button matching the text across the process's dialogs, skipping the MGE main window.</summary>
    private static IntPtr FindVisibleDialogButton(int processId, string buttonText, MgeAutomationSettings settings)
    {
        var result = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var wp);
            if (wp != (uint)processId || !IsWindowVisible(h))
            {
                return true;
            }
            var t = new StringBuilder(256);
            GetWindowText(h, t, t.Capacity);
            if (t.ToString().Contains(settings.MainWindowTitle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            var b = FindDescendantButton(h, buttonText, visibleOnly: true);
            if (b != IntPtr.Zero)
            {
                result = b;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Clicks the visible click-button each pass until the until-button appears (wizard advanced); returns whether reached.</summary>
    private static bool AdvanceUntilVisible(
        int processId, string click, string until, TimeSpan timeout, MgeAutomationSettings settings, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            HideToolWindows(processId);
            if (FindVisibleDialogButton(processId, until, settings) != IntPtr.Zero)
            {
                return true;
            }
            var b = FindVisibleDialogButton(processId, click, settings);
            if (b != IntPtr.Zero)
            {
                BmClick(b);
                Logger.Info($"MGE automation: clicked \"{click}\".");
            }
            Thread.Sleep(settings.WizardClickPollMs);
        }
        return false;
    }

    /// <summary>Clicks a visible button matching the text until it is gone (the page advanced / action consumed).</summary>
    private static void ClickUntilGone(
        int processId, string text, TimeSpan timeout, MgeAutomationSettings settings, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            HideToolWindows(processId);
            var b = FindVisibleDialogButton(processId, text, settings);
            if (b == IntPtr.Zero)
            {
                return;
            }
            BmClick(b);
            Logger.Info($"MGE automation: clicked \"{text}\".");
            Thread.Sleep(settings.WizardClickPollMs);
        }
    }

    /// <summary>Dismisses any open dialog by clicking the first visible button matching the accept labels (one pass).</summary>
    private static void DismissDialogs(int processId, string[] acceptLabels, MgeAutomationSettings settings)
    {
        foreach (var label in acceptLabels)
        {
            var b = FindVisibleDialogButton(processId, label, settings);
            if (b != IntPtr.Zero)
            {
                BmClick(b);
                Logger.Info($"MGE automation: dismissed dialog via \"{label}\".");
                Thread.Sleep(settings.DialogDismissPauseMs);
                return;
            }
        }
    }

    /// <summary>
    /// Waits for generation to finish: the Finish button stays VISIBLE-but-DISABLED while work
    /// runs and only ENABLES when every phase is done, so our visible-and-enabled finder returning
    /// non-zero means generation actually completed. Clicks Finish, or bails if the process dies.
    /// </summary>
    private static void WaitForGenerationComplete(
        int processId, MgeAutomationSettings settings, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(settings.GenerationTimeoutMinutes);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (!IsProcessAlive(processId))
            {
                Logger.Info("MGE automation: process exited during generation.");
                return;
            }
            HideToolWindows(processId);

            var finish = FindVisibleDialogButton(processId, settings.FinishButton, settings);
            if (finish != IntPtr.Zero)
            {
                BmClick(finish);
                Logger.Info("MGE automation: generation complete; clicked Finish.");
                Thread.Sleep(settings.FinishPauseMs);
                return;
            }
            Thread.Sleep(settings.GenerationPollMs);
        }
        Logger.Warn("MGE automation: generation completion not detected before timeout.");
    }
}

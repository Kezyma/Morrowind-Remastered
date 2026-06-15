using System.Diagnostics;
using System.Text;
using System.Threading;
using static MorrowindRemasteredLauncher.Services.Win32Interop;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Drives MGE XE's distant-land generator entirely off-screen via window messages
/// (no cursor/foreground): switches to the Distant Land tab, opens the generator
/// wizard, accepts the "regenerate?" warning, takes the default load order, and
/// runs generation with saved/default settings. MGE XE is a WinForms app — its
/// BUTTON controls respond to BM_CLICK, and the tab control is switched by posting
/// a mouse-click at the tab header's rect (read cross-process via TCM_GETITEMRECT).
/// Generation is long (minutes); we poll the off-screen window until it settles,
/// auto-dismissing info dialogs, then kill the process. The overwrite harvest +
/// verifier remain the source of truth, so every step fails soft.
/// </summary>
public static class MgeAutomation
{
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan YesToContinueWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ContinueToRunWait = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan RunStepsWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(90);

    private const int MainPollMs = 500;
    private const int DialogDismissPauseMs = 400;
    private const int TabSwitchPauseMs = 800;
    private const int WizardClickPollMs = 1200;
    private const int GenerationPollMs = 3000;
    private const int FinishPauseMs = 1500;

    /// <summary>
    /// Runs the full off-screen distant-land generation wizard (see class summary),
    /// then returns. Best-effort throughout.
    /// </summary>
    public static Task GenerateDistantLandAsync(
        IProgress<InstallProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            try
            {
                var main = WaitForMgeMain(WindowWait, ct);
                if (main == IntPtr.Zero)
                {
                    Logger.Warn("MGE automation: main window not found; generate distant land manually.");
                    return;
                }
                GetWindowThreadProcessId(main, out var pid);
                Logger.Info($"MGE automation: window found (pid {pid}).");
                MoveOffScreen(main);

                // MGE sometimes opens with a "Warning" popup about stale/old distant
                // land files; dismiss it so it doesn't block the tab/wizard.
                DismissDialogs((int)pid, new[] { "Yes", "OK", "Continue" });

                // Switch to the Distant Land tab (index 1) by posting a click at its
                // header rect, then dismiss any warning the switch triggers.
                var tab = FindChildByClass(main, "SysTabControl32");
                if (tab != IntPtr.Zero)
                {
                    ClickTabHeader(tab, 1);
                    Thread.Sleep(TabSwitchPauseMs);
                    DismissDialogs((int)pid, new[] { "Yes", "OK", "Continue" });
                }

                // Open the generator wizard.
                var gen = FindDescendantButton(main, "generator wizard");
                if (gen == IntPtr.Zero)
                {
                    Logger.Warn("MGE automation: \"generator wizard\" button not found; generate manually.");
                    return;
                }
                BmClick(gen);
                progress?.Report(new InstallProgress("Setup", "Generating distant land…", null, true));

                // Wizard sequence. Each page is advanced by clicking the page's
                // button until the NEXT page's button appears — a freshly-shown
                // wizard page can drop the first posted click, so a single click is
                // unreliable. Buttons are matched by VISIBILITY: MGE keeps every
                // page's controls as hidden children of one window, so a hidden
                // "Continue" lingers on later pages and must be ignored.
                //  1. "Warning" (regenerate stale files?)  -> Yes,      until Continue shows (Setup Wizard)
                //  2. Setup Wizard (load order; DON'T touch "Use current load order"
                //     — it unchecks critical plugins) -> Continue, until the Run button shows
                //  3. Distant Land Generation -> "Run above steps using saved / default settings"
                AdvanceUntilVisible((int)pid, click: "Yes", until: "Continue", YesToContinueWait, ct);
                var atGen = AdvanceUntilVisible((int)pid, click: "Continue", until: "Run above steps",
                    ContinueToRunWait, ct);
                if (!atGen)
                {
                    Logger.Warn("MGE automation: generation page not reached; generate manually.");
                    return;
                }
                ClickUntilGone((int)pid, "Run above steps", RunStepsWait, ct);
                Logger.Info("MGE automation: distant land generation started (this can take several minutes).");

                WaitForGenerationComplete((int)pid, progress, ct);

                // MGE ignores WM_CLOSE while modal wizards are up — kill it once
                // generation has settled. MO2 then sees the tool exit and closes.
                KillProcess((int)pid);
            }
            catch (Exception ex)
            {
                Logger.Warn($"MGE automation error (continuing): {ex.Message}");
            }
        }, ct);

    /// <summary>Waits for MGE XE's main window (title contains "Graphics Extender").</summary>
    private static IntPtr WaitForMgeMain(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            // MO2's lock window is up before MGE finishes launching — hide it too.
            HideMo2Windows();

            // On a first run after install, MGE shows up to two OK-only popups
            // BEFORE its main window opens ("Distant Land files have not been
            // created…", then "The distant land statics files are missing."). Hide
            // them off-screen and click OK so startup proceeds and they never sit on
            // the user's desktop. No-op once distant land exists.
            foreach (var mge in Process.GetProcessesByName("MGEXEgui"))
            {
                using (mge)
                {
                    HideAllWindows(mge.Id);
                    DismissDialogs(mge.Id, new[] { "OK" });
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
                if (t.ToString().Contains("Graphics Extender", StringComparison.OrdinalIgnoreCase))
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
            Thread.Sleep(MainPollMs);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Finds, across every dialog owned by <paramref name="processId"/> (excluding
    /// the MGE main window), a <b>visible</b> button whose text contains
    /// <paramref name="buttonText"/>; returns its hwnd or Zero. Visibility matters:
    /// MGE keeps controls from all wizard pages as children of one window, so only
    /// the current page's buttons are actually visible.
    /// </summary>
    private static IntPtr FindVisibleDialogButton(int processId, string buttonText)
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
            // Skip the MGE main window (its own buttons aren't wizard buttons).
            if (t.ToString().Contains("Graphics Extender", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Clicks the visible <paramref name="click"/> button repeatedly until a visible
    /// <paramref name="until"/> button appears (the wizard advanced) or the timeout
    /// elapses. Hides every MGE window each pass so the wizard never flashes
    /// on-screen. Returns whether the target page was reached.
    /// </summary>
    private static bool AdvanceUntilVisible(
        int processId, string click, string until, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            HideToolWindows(processId);
            if (FindVisibleDialogButton(processId, until) != IntPtr.Zero)
            {
                return true;
            }
            var b = FindVisibleDialogButton(processId, click);
            if (b != IntPtr.Zero)
            {
                BmClick(b);
                Logger.Info($"MGE automation: clicked \"{click}\".");
            }
            Thread.Sleep(WizardClickPollMs);
        }
        return false;
    }

    /// <summary>Clicks a visible button containing <paramref name="text"/> until it
    /// is gone (i.e. the page advanced / the action was consumed).</summary>
    private static void ClickUntilGone(
        int processId, string text, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            HideToolWindows(processId);
            var b = FindVisibleDialogButton(processId, text);
            if (b == IntPtr.Zero)
            {
                return;
            }
            BmClick(b);
            Logger.Info($"MGE automation: clicked \"{text}\".");
            Thread.Sleep(WizardClickPollMs);
        }
    }

    /// <summary>Dismisses any currently-open dialog by clicking the first visible
    /// button matching <paramref name="acceptLabels"/> (one pass, best-effort).</summary>
    private static void DismissDialogs(int processId, string[] acceptLabels)
    {
        foreach (var label in acceptLabels)
        {
            var b = FindVisibleDialogButton(processId, label);
            if (b != IntPtr.Zero)
            {
                BmClick(b);
                Logger.Info($"MGE automation: dismissed dialog via \"{label}\".");
                Thread.Sleep(DialogDismissPauseMs);
                return;
            }
        }
    }

    /// <summary>
    /// Waits for distant-land generation to finish. While work runs, the generator's
    /// "Finish" button is VISIBLE but DISABLED and the status text reads "Waiting for
    /// … to complete"; when every phase is done, Finish becomes ENABLED. Our button
    /// finder matches visible-AND-enabled, so a non-zero "Finish" here means
    /// generation actually completed (not just that the page is up). We hide all
    /// windows each pass (silent), click Finish when it enables, and bail if the
    /// process dies. Generation can take many minutes on a large load order, so the
    /// timeout is generous.
    /// </summary>
    private static void WaitForGenerationComplete(
        int processId, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + GenerationTimeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (!IsProcessAlive(processId))
            {
                Logger.Info("MGE automation: process exited during generation.");
                return;
            }
            HideToolWindows(processId);

            var finish = FindVisibleDialogButton(processId, "Finish");
            if (finish != IntPtr.Zero)
            {
                BmClick(finish);
                Logger.Info("MGE automation: generation complete; clicked Finish.");
                Thread.Sleep(FinishPauseMs);
                return;
            }
            Thread.Sleep(GenerationPollMs);
        }
        Logger.Warn("MGE automation: generation completion not detected before timeout.");
    }
}

using System.Text;
using System.Threading;
using System.Windows.Automation;
using static MorrowindRemasteredLauncher.Services.Win32Interop;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Drives Morrowind Code Patch (a wxWidgets app with no silent CLI). MCP's buttons
/// are misreported as panes by UIAutomation and ignore posted/synthetic BM_CLICK,
/// so we post raw mouse messages at the button's rect while the window sits
/// off-screen, then read completion from MCP's in-window log pane ("Patch
/// succeeded") via UIAutomation. Best-effort: every step logs and fails soft, so
/// if the window/button can't be driven the user finishes by hand — the overwrite
/// harvest + verifier remain the source of truth.
/// </summary>
public static class McpAutomation
{
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan LogWait = TimeSpan.FromSeconds(45);
    private const int PollMs = 500;

    /// <summary>
    /// Waits for the MCP window, clicks "Apply chosen patches", waits for the
    /// log pane to report success, then closes the window.
    /// </summary>
    public static Task ApplyMorrowindCodePatchAsync(
        IProgress<InstallProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            try
            {
                // Find the window that actually contains the Apply button — robust
                // against unrelated windows whose title merely mentions MCP (e.g.
                // an editor with this plan file open).
                var (hwnd, apply) = WaitForButtonWindow("Apply chosen patches", WindowWait, ct);
                if (hwnd == IntPtr.Zero || apply == IntPtr.Zero)
                {
                    Logger.Warn("MCP automation: window/button not found; complete it manually.");
                    return;
                }
                GetWindowThreadProcessId(hwnd, out var pid);
                Logger.Info($"MCP automation: MCP window found (pid {pid}).");

                // Park the window off-screen so the patch runs silently. It stays
                // "visible" to the OS (unlike SW_HIDE), so the message-click still
                // lands and the log pane is still readable via UIAutomation.
                MoveOffScreen(hwnd);
                ClickButton(apply);
                Logger.Info("MCP automation: clicked \"Apply chosen patches\".");

                // MCP writes progress + "Patch succeeded." to its log pane (no
                // dialog, no log file). Bail early on failure.
                var ok = WaitForLogText((int)pid,
                    succeed: new[] { "Patch succeeded", "succeeded" },
                    fail: new[] { "patch failed", "error", "cannot patch", "unable" },
                    LogWait, ct);
                Logger.Info(ok
                    ? "MCP automation: patch reported complete."
                    : "MCP automation: no success message seen (continuing).");

                // wxWidgets MCP ignores WM_CLOSE/SC_CLOSE — kill it (patch is done,
                // nothing unsaved). MO2 then sees the tool exit and closes.
                KillProcess((int)pid);
            }
            catch (Exception ex)
            {
                Logger.Warn($"MCP automation error (continuing): {ex.Message}");
            }
        }, ct);

    /// <summary>
    /// Polls top-level visible windows for one that contains a child Button with
    /// <paramref name="buttonText"/>; returns (window, button).
    /// </summary>
    private static (IntPtr Window, IntPtr Button) WaitForButtonWindow(
        string buttonText, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            // Keep MO2's "locked" window off-screen while we wait for MCP's window.
            HideMo2Windows();
            var window = IntPtr.Zero;
            var button = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h))
                {
                    return true;
                }
                var b = FindChildButton(h, buttonText);
                if (b != IntPtr.Zero)
                {
                    window = h;
                    button = b;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (window != IntPtr.Zero)
            {
                return (window, button);
            }
            Thread.Sleep(PollMs);
        }
        return (IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Polls the MCP window's text panes (via UIAutomation) until one contains a
    /// <paramref name="succeed"/> phrase (returns true) or a <paramref name="fail"/>
    /// phrase (returns false).
    /// </summary>
    private static bool WaitForLogText(
        int processId, string[] succeed, string[] fail, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            // MO2's "locked" window stays up the whole time MCP runs — keep it hidden.
            HideMo2Windows();
            var text = ReadWindowText(processId);
            if (!string.IsNullOrEmpty(text))
            {
                if (succeed.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
                if (fail.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
            Thread.Sleep(PollMs);
        }
        return false;
    }

    private static string ReadWindowText(int processId)
    {
        try
        {
            var window = AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
            if (window is null)
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            var all = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement e in all)
            {
                var n = e.Current.Name;
                if (!string.IsNullOrEmpty(n))
                {
                    sb.Append(n).Append('\n');
                }
            }
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}

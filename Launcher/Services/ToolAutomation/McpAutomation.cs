using System.Text;
using System.Threading;
using System.Windows.Automation;
using MorrowindRemasteredLauncher.Models;
using static MorrowindRemasteredLauncher.Services.Win32Interop;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Drives Morrowind Code Patch (a wxWidgets app with no silent CLI) off-screen.
/// </summary>
/// <remarks>
/// MCP's buttons are misreported as panes by UIAutomation and ignore posted/synthetic
/// BM_CLICK, so we post raw mouse messages at the button's rect while the window sits
/// off-screen (moved, NOT SW_HIDE — hidden windows drop out of the UIAutomation tree),
/// then read completion from MCP's in-window log pane. Best-effort: every step logs and
/// fails soft, so the overwrite harvest + verifier remain the source of truth.
/// </remarks>
public static class McpAutomation
{
    /// <summary>Waits for the MCP window, clicks the apply button, waits for success in the log pane, then closes it.</summary>
    public static Task ApplyMorrowindCodePatchAsync(
        McpAutomationSettings settings, IProgress<InstallProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            try
            {
                var (hwnd, apply) = WaitForButtonWindow(
                    settings.ApplyButton, TimeSpan.FromSeconds(settings.WindowWaitSeconds),
                    settings.PollMs, ct);
                if (hwnd == IntPtr.Zero || apply == IntPtr.Zero)
                {
                    Logger.Warn("MCP automation: window/button not found; complete it manually.");
                    return;
                }
                GetWindowThreadProcessId(hwnd, out var pid);
                Logger.Info($"MCP automation: MCP window found (pid {pid}).");

                MoveOffScreen(hwnd);
                ClickButton(apply);
                Logger.Info($"MCP automation: clicked \"{settings.ApplyButton}\".");

                var ok = WaitForLogText((int)pid,
                    succeed: settings.SuccessPhrases,
                    fail: settings.FailurePhrases,
                    TimeSpan.FromSeconds(settings.LogWaitSeconds), settings.PollMs, ct);
                Logger.Info(ok
                    ? "MCP automation: patch reported complete."
                    : "MCP automation: no success message seen (continuing).");

                KillProcess((int)pid);
            }
            catch (Exception ex)
            {
                Logger.Warn($"MCP automation error (continuing): {ex.Message}");
            }
        }, ct);

    /// <summary>Polls visible top-level windows for one containing a child button with the given text; returns (window, button).</summary>
    private static (IntPtr Window, IntPtr Button) WaitForButtonWindow(
        string buttonText, TimeSpan timeout, int pollMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
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
            Thread.Sleep(pollMs);
        }
        return (IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Polls the MCP window's text panes until one contains a success phrase (true) or a failure phrase (false).</summary>
    private static bool WaitForLogText(
        int processId, string[] succeed, string[] fail, TimeSpan timeout, int pollMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
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
            Thread.Sleep(pollMs);
        }
        return false;
    }

    /// <summary>Reads all descendant element names of the given process's window via UIAutomation.</summary>
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

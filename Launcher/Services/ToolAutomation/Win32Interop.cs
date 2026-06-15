using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Low-level Win32 interop and reusable window primitives shared by the GUI-only tool automation (<see cref="McpAutomation"/> / <see cref="MgeAutomation"/>).</summary>
/// <remarks>Windows are parked off-screen with <c>SetWindowPos(-32000,-32000)</c> — NOT <c>SW_HIDE</c> — so they stay in the UIAutomation tree while running unseen. Every helper is best-effort / fail-soft.</remarks>
internal static class Win32Interop
{
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const int MkLButton = 0x0001;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int OffScreen = -32000;
    private const uint BmClickMsg = 0x00F5;
    private const uint TcmGetItemRect = 0x130A;
    private const uint MemCommit = 0x3000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint ProcVmRead = 0x0010;
    private const uint ProcVmWrite = 0x0020;
    private const uint ProcVmOperation = 0x0008;

    private const int TabClickHoldMs = 60;
    private const int ButtonClickHoldMs = 80;

    /// <summary>Callback for the window-enumeration P/Invokes.</summary>
    internal delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, uint size, uint type, uint prot);
    [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(IntPtr p, IntPtr addr, uint size, uint type);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr p, IntPtr addr, byte[] buf, int size, out int read);

    /// <summary>A Win32 RECT (left, top, right, bottom).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>First descendant window whose class contains <paramref name="classSub"/>.</summary>
    internal static IntPtr FindChildByClass(IntPtr parent, string classSub)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(parent, (h, _) =>
        {
            var c = new StringBuilder(96);
            GetClassName(h, c, c.Capacity);
            if (c.ToString().Contains(classSub, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>First descendant BUTTON control whose text contains <paramref name="textSub"/> (case-insensitive).</summary>
    /// <remarks>When <paramref name="visibleOnly"/> is set, hidden OR disabled controls are skipped — both matter for MGE, where off-page buttons stay hidden and "Finish" stays visible-but-disabled until generation completes, so the enabled check makes it a valid completion signal and avoids clicking a greyed-out button.</remarks>
    internal static IntPtr FindDescendantButton(IntPtr parent, string textSub, bool visibleOnly = false)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(parent, (h, _) =>
        {
            if (visibleOnly && (!IsWindowVisible(h) || !IsWindowEnabled(h)))
            {
                return true;
            }
            var c = new StringBuilder(96);
            GetClassName(h, c, c.Capacity);
            if (c.ToString().Contains("BUTTON", StringComparison.OrdinalIgnoreCase))
            {
                var t = new StringBuilder(256);
                GetWindowText(h, t, t.Capacity);
                if (t.ToString().Contains(textSub, StringComparison.OrdinalIgnoreCase))
                {
                    found = h;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>First child window of class exactly "Button" whose text equals <paramref name="text"/>.</summary>
    internal static IntPtr FindChildButton(IntPtr parent, string text)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(parent, (h, _) =>
        {
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() == "Button")
            {
                var t = new StringBuilder(256);
                GetWindowText(h, t, t.Capacity);
                if (string.Equals(t.ToString(), text, StringComparison.OrdinalIgnoreCase))
                {
                    found = h;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Posts BM_CLICK, which works for MGE's WinForms BUTTON controls (unlike MCP's wxWidgets).</summary>
    internal static void BmClick(IntPtr button) => PostMessage(button, BmClickMsg, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Clicks a button by posting mouse messages straight to it (works off-screen; MCP's wxWidgets button responds to these but ignores BM_CLICK).</summary>
    internal static void ClickButton(IntPtr button)
    {
        if (!GetWindowRect(button, out var r))
        {
            return;
        }
        var lp = (IntPtr)((((r.Bottom - r.Top) / 2) << 16) | ((r.Right - r.Left) / 2));
        PostMessage(button, WmLButtonDown, (IntPtr)MkLButton, lp);
        Thread.Sleep(ButtonClickHoldMs);
        PostMessage(button, WmLButtonUp, IntPtr.Zero, lp);
    }

    /// <summary>Switches a SysTabControl32 to <paramref name="index"/> by posting a click at the tab header's rect, which TCM_GETITEMRECT writes cross-process.</summary>
    internal static void ClickTabHeader(IntPtr tab, int index)
    {
        GetWindowThreadProcessId(tab, out var pid);
        var hProc = OpenProcess(ProcVmOperation | ProcVmRead | ProcVmWrite, false, pid);
        if (hProc == IntPtr.Zero)
        {
            return;
        }
        var remote = VirtualAllocEx(hProc, IntPtr.Zero, 16, MemCommit, PageReadWrite);
        try
        {
            SendMessage(tab, TcmGetItemRect, (IntPtr)index, remote);
            var buf = new byte[16];
            if (!ReadProcessMemory(hProc, remote, buf, 16, out _))
            {
                return;
            }
            int left = BitConverter.ToInt32(buf, 0), top = BitConverter.ToInt32(buf, 4);
            int right = BitConverter.ToInt32(buf, 8), bottom = BitConverter.ToInt32(buf, 12);
            var cx = (left + right) / 2;
            var cy = (top + bottom) / 2;
            var lp = (IntPtr)((cy << 16) | cx);
            PostMessage(tab, WmLButtonDown, (IntPtr)MkLButton, lp);
            Thread.Sleep(TabClickHoldMs);
            PostMessage(tab, WmLButtonUp, IntPtr.Zero, lp);
        }
        finally
        {
            VirtualFreeEx(hProc, remote, 0, MemRelease);
            CloseHandle(hProc);
        }
    }

    /// <summary>Moves a window off the visible desktop so its tool runs unseen.</summary>
    internal static void MoveOffScreen(IntPtr hwnd)
    {
        try
        {
            SetWindowPos(hwnd, IntPtr.Zero, OffScreen, OffScreen, 0, 0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        catch { }
    }

    /// <summary>Moves every top-level window of the process off the visible desktop.</summary>
    internal static void HideAllWindows(int processId)
    {
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var wp);
            if (wp == (uint)processId && IsWindowVisible(h))
            {
                MoveOffScreen(h);
            }
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>Parks Mod Organizer's "locked while the executable runs" window off-screen during otherwise-silent automation (best-effort; nothing to click on the Qt window, just moved).</summary>
    internal static void HideMo2Windows()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("ModOrganizer"))
            {
                using (p)
                {
                    HideAllWindows(p.Id);
                }
            }
        }
        catch { }
    }

    /// <summary>Hides the tool's own windows and MO2's lock window together.</summary>
    internal static void HideToolWindows(int toolPid)
    {
        HideAllWindows(toolPid);
        HideMo2Windows();
    }

    /// <summary>Kills a process by id (how a GUI-only tool is closed); logs success/failure.</summary>
    internal static void KillProcess(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            p.Kill();
            Logger.Info($"Tool automation: closed process (pid {processId}).");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Tool automation: couldn't close process (pid {processId}): {ex.Message}");
        }
    }

    /// <summary>True if the process is still running.</summary>
    internal static bool IsProcessAlive(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }
}

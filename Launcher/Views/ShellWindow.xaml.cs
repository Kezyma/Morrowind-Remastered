using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MorrowindRemasteredLauncher.Services;
using MorrowindRemasteredLauncher.ViewModels;

namespace MorrowindRemasteredLauncher.Views;

public partial class ShellWindow : Window
{
    /// <summary>The book artwork's aspect ratio; resizing is locked to it.</summary>
    private const double Aspect = 1648.0 / 1024.0;

    public ShellWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm)
        {
            return;
        }

        // async void: guard so a failure in one startup step degrades gracefully
        // (and is logged) instead of tripping the global unhandled-exception dialog
        // or silently skipping the other step.
        try
        {
            await vm.RestoreNexusSessionAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("Restoring the Nexus session at startup failed", ex);
        }

        try
        {
            await vm.RefreshCatalogAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("Refreshing the modlist catalog at startup failed", ex);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    // ---- Aspect-ratio lock: constrain interactive resizes via WM_SIZING ----

    private const int WmSizing = 0x0214;

    // WM_SIZING wParam: which edge/corner is being dragged.
    private const int WmszLeft = 1, WmszRight = 2, WmszTop = 3, WmszTopLeft = 4,
                      WmszTopRight = 5, WmszBottom = 6, WmszBottomLeft = 7,
                      WmszBottomRight = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect { public int Left, Top, Right, Bottom; }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmSizing)
        {
            return IntPtr.Zero;
        }

        var rect = Marshal.PtrToStructure<Win32Rect>(lParam);
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        var edge = (int)wParam;

        switch (edge)
        {
            case WmszLeft:
            case WmszRight:
                h = (int)Math.Round(w / Aspect);
                break;
            case WmszTop:
            case WmszBottom:
                w = (int)Math.Round(h * Aspect);
                break;
            default:
                // Corners: honour the larger of the two drag deltas.
                if (w / Aspect > h)
                {
                    h = (int)Math.Round(w / Aspect);
                }
                else
                {
                    w = (int)Math.Round(h * Aspect);
                }
                break;
        }

        // Re-anchor on the side opposite the dragged edge/corner.
        if (edge is WmszLeft or WmszTopLeft or WmszBottomLeft)
        {
            rect.Left = rect.Right - w;
        }
        else
        {
            rect.Right = rect.Left + w;
        }
        if (edge is WmszTop or WmszTopLeft or WmszTopRight)
        {
            rect.Top = rect.Bottom - h;
        }
        else
        {
            rect.Bottom = rect.Top + h;
        }

        Marshal.StructureToPtr(rect, lParam, fDeleteOld: false);
        handled = true;
        return (IntPtr)1; // TRUE: we processed WM_SIZING
    }

    // The window is chromeless (the book artwork is the frame), so any
    // unhandled left-press on it moves the window. Clicks consumed by
    // buttons/textboxes never bubble up here.
    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { /* ignore */ }
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

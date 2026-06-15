using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MorrowindRemasteredLauncher.Services;
using MorrowindRemasteredLauncher.ViewModels;

namespace MorrowindRemasteredLauncher.Views;

/// <summary>The single chromeless main window; the open-book artwork is its frame and resizes lock to its aspect ratio.</summary>
public partial class ShellWindow : Window
{
    /// <summary>The book artwork's aspect ratio; resizing is locked to it.</summary>
    private const double Aspect = 1648.0 / 1024.0;

    /// <summary>Initializes the window and wires the startup load.</summary>
    public ShellWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Restores the Nexus session and refreshes the catalog at startup, each guarded so one failure degrades gracefully.</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm)
        {
            return;
        }

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

    /// <summary>Hooks the window message loop so resizes can be constrained to the aspect ratio.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private const int WmSizing = 0x0214;

    /// <summary>WM_SIZING wParam values identifying which edge/corner is being dragged.</summary>
    private const int WmszLeft = 1, WmszRight = 2, WmszTop = 3, WmszTopLeft = 4,
                      WmszTopRight = 5, WmszBottom = 6, WmszBottomLeft = 7,
                      WmszBottomRight = 8;

    /// <summary>Native RECT used to read/write the resize rectangle from WM_SIZING.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect { public int Left, Top, Right, Bottom; }

    /// <summary>Handles WM_SIZING to constrain interactive resizes to the book aspect ratio.</summary>
    /// <remarks>Corners honour the larger of the two drag deltas, then the rect is re-anchored on the side opposite the dragged edge/corner.</remarks>
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
        return (IntPtr)1;
    }

    /// <summary>Drags the chromeless window on any left-press that bubbles up (clicks consumed by buttons/textboxes never reach here).</summary>
    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>Closes the window.</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

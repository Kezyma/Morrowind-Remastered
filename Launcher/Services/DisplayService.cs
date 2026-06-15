using System.Runtime.InteropServices;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Primary-monitor mode: physical pixels + refresh rate.</summary>
public sealed record DisplayMode(int Width, int Height, int RefreshHz)
{
    /// <summary>Recommended UI scale: 1.5 on 4K-class displays, else 1.0.</summary>
    public double RecommendedUiScale => (Width >= 3840 || Height >= 2160) ? 1.5 : 1.0;
}

/// <summary>Reads the primary monitor's display modes (true physical pixels + refresh rate) via Win32 <c>EnumDisplaySettings</c>, which is what the game configs need.</summary>
/// <remarks>WPF's <c>SystemParameters</c> reports DPI-scaled DIPs and no refresh rate; the launcher is PerMonitorV2 DPI-aware so these values aren't virtualized.</remarks>
public sealed class DisplayService
{
    private const int EnumCurrentSettings = -1;

    /// <summary>The Win32 DEVMODE structure describing a display mode.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        private const int CchDeviceName = 32;
        private const int CchFormName = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    /// <summary>Win32 <c>EnumDisplaySettings</c>: reads a display mode for the given device.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(
        string? deviceName, int modeNum, ref DevMode devMode);

    /// <summary>Normalises a DEVMODE refresh rate: 0 and 1 both mean "unspecified", so report 60 Hz.</summary>
    private static int NormalizeHz(uint frequency) => frequency is 0 or 1 ? 60 : (int)frequency;

    /// <summary>The primary monitor's current mode, or a 1920x1080@60 fallback if the query fails.</summary>
    public DisplayMode GetPrimaryMode()
    {
        var dm = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
        if (EnumDisplaySettings(null, EnumCurrentSettings, ref dm) &&
            dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
        {
            return new DisplayMode(
                (int)dm.dmPelsWidth, (int)dm.dmPelsHeight, NormalizeHz(dm.dmDisplayFrequency));
        }

        Logger.Warn("EnumDisplaySettings failed; falling back to 1920x1080@60.");
        return new DisplayMode(1920, 1080, 60);
    }

    /// <summary>All distinct 32-bit display modes the primary adapter advertises, ordered largest-first, for the Settings dropdowns.</summary>
    public IReadOnlyList<DisplayMode> EnumerateModes()
    {
        var modes = new HashSet<(int, int, int)>();
        var dm = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
        for (var i = 0; EnumDisplaySettings(null, i, ref dm); i++)
        {
            if (dm is { dmBitsPerPel: 32, dmPelsWidth: > 0, dmPelsHeight: > 0 })
            {
                modes.Add(((int)dm.dmPelsWidth, (int)dm.dmPelsHeight,
                    NormalizeHz(dm.dmDisplayFrequency)));
            }
            dm.dmSize = (ushort)Marshal.SizeOf<DevMode>();
        }

        var list = modes
            .Select(m => new DisplayMode(m.Item1, m.Item2, m.Item3))
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.RefreshHz)
            .ToList();
        return list.Count > 0 ? list : new List<DisplayMode> { GetPrimaryMode() };
    }
}

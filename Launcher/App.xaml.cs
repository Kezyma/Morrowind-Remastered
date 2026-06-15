using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using MorrowindRemasteredLauncher.Services;
using MorrowindRemasteredLauncher.ViewModels;
using MorrowindRemasteredLauncher.Views;

namespace MorrowindRemasteredLauncher;

public partial class App : Application
{
    /// <summary>Minimal service container, deliberately not a DI framework to keep the single-file binary small.</summary>
    public static ServiceRegistry Services { get; } = new();

    /// <summary>App entry point: wires global exception handlers, then either runs the Steam-presence helper or shows the shell window.</summary>
    /// <remarks>The <c>--steam-presence</c> branch runs as a separate short-lived process because Steam only ends a session when the owning process exits, so the long-lived launcher can't hold it itself. Startup is wrapped in try/catch because an exception here (before the message loop is pumping) would otherwise crash silently with no visible window.</remarks>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        if (e.Args.Length > 0 && e.Args[0] == "--steam-presence")
        {
            RunSteamPresence(e.Args);
            return;
        }

        try
        {
            AppPaths.EnsureBaseDirectories();
            Logger.Info("Launcher starting up.");
            Services.Initialize();

            var shell = new ShellWindow
            {
                DataContext = Services.Get<ShellViewModel>()
            };
            shell.Show();
            Logger.Info("Shell window shown.");
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal error during startup", ex);
            MessageBox.Show(
                $"The launcher failed to start:\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                $"See {Logger.LogFile} for details.",
                "Morrowind Remastered Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    /// <summary>Runs the headless Steam-presence loop on a background thread: extract the API, wait for the game to start, hold the Morrowind session, then exit when the game closes.</summary>
    /// <remarks>Args: <c>--steam-presence &lt;appid&gt; &lt;gameProcessName&gt;</c>. Has no window, so it stays alive via the message loop until <see cref="Application.Shutdown()"/>. Waits up to ~2 min for the game (it launches via MO2) before claiming a session, so a failed launch logs no phantom playtime.</remarks>
    private void RunSteamPresence(string[] args)
    {
        var appId = args.Length > 1 && uint.TryParse(args[1], out var a) ? a : SteamService.MorrowindAppId;
        var gameProc = args.Length > 2 ? args[2] : "Morrowind";

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Task.Run(() =>
        {
            try
            {
                AppPaths.EnsureBaseDirectories();
                var config = new ConfigService();
                config.Load();
                var steam = new SteamService(config);

                if (!steam.EnsureSteamApi())
                {
                    Logger.Warn("Steam presence: steam_api64.dll unavailable; exiting.");
                    return;
                }

                var appeared = false;
                for (var i = 0; i < 240 && !appeared; i++)
                {
                    if (IsRunning(gameProc))
                    {
                        appeared = true;
                    }
                    else
                    {
                        Thread.Sleep(500);
                    }
                }
                if (!appeared)
                {
                    Logger.Warn($"Steam presence: game \"{gameProc}\" never started; exiting.");
                    return;
                }

                if (!steam.StartTracking(appId))
                {
                    Logger.Warn("Steam presence: SteamAPI_Init failed; exiting.");
                    return;
                }
                Logger.Info($"Steam presence active (appid {appId}); tracking until \"{gameProc}\" exits.");

                while (IsRunning(gameProc))
                {
                    Thread.Sleep(2000);
                }

                steam.StopTracking();
                Logger.Info("Steam presence: game exited; session ended.");
            }
            catch (Exception ex)
            {
                Logger.Error("Steam presence helper failed", ex);
            }
            finally
            {
                Dispatcher.Invoke(Shutdown);
            }
        });
    }

    /// <summary>True when a process with the given name is currently running.</summary>
    private static bool IsRunning(string processName)
    {
        try { return Process.GetProcessesByName(processName).Length > 0; }
        catch { return false; }
    }

    /// <summary>Logs an unhandled UI-thread exception, shows it to the user, and marks it handled so the app keeps running.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "Morrowind Remastered Launcher",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>Logs an unhandled exception raised on a non-UI thread.</summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Logger.Error("Unhandled domain exception", ex);
        }
    }
}

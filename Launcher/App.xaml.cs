using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using MorrowindRemasteredLauncher.Services;
using MorrowindRemasteredLauncher.ViewModels;
using MorrowindRemasteredLauncher.Views;

namespace MorrowindRemasteredLauncher;

public partial class App : Application
{
    /// <summary>
    /// Minimal service container. Kept simple to avoid pulling in a DI framework
    /// (helps keep the single-file binary small).
    /// </summary>
    public static ServiceRegistry Services { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // Headless Steam-presence helper (spawned by the main launcher on Play): hold
        // a Morrowind (22320) Steam session while the game runs, then EXIT — Steam only
        // ends a session when the process that opened it exits, so it can't be done in
        // the long-lived launcher process itself.
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
            // An exception here (before the message loop is pumping) would
            // otherwise crash silently with no visible window. Log it and tell
            // the user, then shut down cleanly.
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

    /// <summary>
    /// Runs the headless Steam-presence loop on a background thread: extract the API,
    /// wait for the game to start, hold the Morrowind session, then exit when the game
    /// closes. Args: <c>--steam-presence &lt;appid&gt; &lt;gameProcessName&gt;</c>.
    /// </summary>
    private void RunSteamPresence(string[] args)
    {
        var appId = args.Length > 1 && uint.TryParse(args[1], out var a) ? a : SteamService.MorrowindAppId;
        var gameProc = args.Length > 2 ? args[2] : "Morrowind";

        // No window: keep the process alive via the message loop until we Shutdown().
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Task.Run(() =>
        {
            try
            {
                AppPaths.EnsureBaseDirectories();
                var config = new ConfigService();
                config.Load();
                var steam = new SteamService(new HttpClient(), config);

                if (!steam.EnsureSteamApiAsync(null, default).GetAwaiter().GetResult())
                {
                    Logger.Warn("Steam presence: steam_api64.dll unavailable; exiting.");
                    return;
                }

                // Wait for the game to actually start (up to ~2 min — it launches via
                // MO2) before claiming a session, so a failed launch logs no phantom time.
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

    private static bool IsRunning(string processName)
    {
        try { return Process.GetProcessesByName(processName).Length > 0; }
        catch { return false; }
    }

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

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Logger.Error("Unhandled domain exception", ex);
        }
    }
}

using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using MorrowindRemasteredLauncher.Services;

namespace MorrowindRemasteredLauncher.Views;

/// <summary>
/// Embedded WebView2 popup that drives the Nexus OAuth login, exactly mirroring
/// Wabbajack's own approach: it shows the Nexus authorize page and watches for a
/// navigation to the loopback redirect (<c>https://127.0.0.1:1234</c>). When that
/// happens it captures the full redirect URI (carrying <c>code</c>/<c>state</c>)
/// and closes — no real TLS connection to 127.0.0.1 is ever made, so there is no
/// certificate warning.
///
/// The caller passes in the authorize URL and the redirect host to watch for, and
/// reads <see cref="RedirectUri"/> after <c>ShowDialog()</c> returns true.
/// </summary>
public partial class NexusLoginWindow : Window
{
    /// <summary>Status line shown above the web view (bindable).</summary>
    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(
            nameof(StatusText), typeof(string), typeof(NexusLoginWindow),
            new PropertyMetadata(
                "Sign in to your Nexus Mods account to authorize the launcher."));

    private readonly string _authorizeUrl;
    private readonly string _redirectHost;
    private bool _completed;

    /// <summary>The intercepted redirect URI (set when login completes).</summary>
    public Uri? RedirectUri { get; private set; }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public NexusLoginWindow(string authorizeUrl, string redirectHost)
    {
        _authorizeUrl = authorizeUrl;
        _redirectHost = redirectHost;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Keep the WebView2 user-data folder inside our portable tree so we
            // don't litter the user's profile and so sign-out is self-contained.
            var userDataFolder = AppPaths.WebView2Dir;
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment
                .CreateAsync(userDataFolder: userDataFolder)
                .ConfigureAwait(true);

            await Web.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            // Catch both same-window navigations and popups to the redirect host.
            Web.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Web.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            Web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            Logger.Info($"Nexus login: navigating to {_authorizeUrl}");
            Web.CoreWebView2.Navigate(_authorizeUrl);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start the Nexus login web view", ex);
            StatusText = "Couldn't start the sign-in view. Please try again.";
            DialogResult = false;
            Close();
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        Logger.Info($"Nexus login: navigation starting -> {e.Uri}");
        if (TryCapture(e.Uri))
        {
            e.Cancel = true;
            FinishSuccess();
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            Logger.Info("Nexus login: navigation completed successfully.");
            return;
        }

        // A failed page load (DNS, TLS, network, web error) would otherwise leave
        // a blank surface with no clue why. Surface it and log the status.
        Logger.Error($"Nexus login: navigation failed with status {e.WebErrorStatus} " +
                     $"(HTTP {e.HttpStatusCode}).");
        StatusText = $"Couldn't load the sign-in page ({e.WebErrorStatus}). " +
                     "Check your internet connection and try again.";
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (TryCapture(e.Uri))
        {
            e.Handled = true;
            FinishSuccess();
        }
    }

    /// <summary>Captures the URI if it targets the loopback redirect host.</summary>
    private bool TryCapture(string? candidate)
    {
        if (_completed || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Host, _redirectHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        RedirectUri = uri;
        return true;
    }

    private void FinishSuccess()
    {
        if (_completed)
        {
            return;
        }
        _completed = true;

        StatusText = "Authorized. Finishing sign-in…";

        // Marshal the close onto the UI thread (event may fire mid-navigation).
        Dispatcher.BeginInvoke(() =>
        {
            DialogResult = true;
            Close();
        });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Web.CoreWebView2 is not null)
        {
            Web.CoreWebView2.NavigationStarting -= OnNavigationStarting;
            Web.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
            Web.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        }
        base.OnClosing(e);
    }
}

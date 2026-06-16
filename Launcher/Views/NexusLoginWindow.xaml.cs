using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using MorrowindRemasteredLauncher.Services;

namespace MorrowindRemasteredLauncher.Views;

/// <summary>Embedded WebView2 popup that drives the Nexus OAuth login (mirrors Wabbajack's own approach).</summary>
/// <remarks>
/// Shows the Nexus authorize page and watches for a navigation to the loopback redirect
/// (<c>https://127.0.0.1:1234</c>); on match it captures the full redirect URI (carrying
/// <c>code</c>/<c>state</c>) and closes. No real TLS connection to 127.0.0.1 is ever made, so
/// there is no certificate warning. The caller passes the authorize URL and redirect host to
/// watch for, and reads <see cref="RedirectUri"/> after <c>ShowDialog()</c> returns true.
/// </remarks>
public partial class NexusLoginWindow : Window
{
    /// <summary>Status line shown above the web view (bindable).</summary>
    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(
            nameof(StatusText), typeof(string), typeof(NexusLoginWindow),
            new PropertyMetadata(
                "Sign in to your Nexus Mods account to authorize the launcher."));

    /// <summary>Nexus authorize page the web view navigates to.</summary>
    private readonly string _authorizeUrl;

    /// <summary>Loopback host whose navigation signals the OAuth redirect.</summary>
    private readonly string _redirectHost;

    /// <summary>Guards against finishing the login more than once.</summary>
    private bool _completed;

    /// <summary>The intercepted redirect URI (set when login completes).</summary>
    public Uri? RedirectUri { get; private set; }

    /// <summary>Status line shown above the web view.</summary>
    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    /// <summary>Creates the login window for the given authorize URL and loopback redirect host.</summary>
    public NexusLoginWindow(string authorizeUrl, string redirectHost)
    {
        _authorizeUrl = authorizeUrl;
        _redirectHost = redirectHost;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Initializes WebView2 (portable user-data folder) and navigates to the authorize page.</summary>
    /// <remarks>
    /// The user-data folder lives inside the portable tree so we don't litter the user's profile and
    /// sign-out stays self-contained. Both same-window navigations and popups to the redirect host are watched.
    /// </remarks>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataFolder = AppPaths.WebView2Dir;
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment
                .CreateAsync(userDataFolder: userDataFolder)
                .ConfigureAwait(true);

            await Web.EnsureCoreWebView2Async(env).ConfigureAwait(true);

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

    /// <summary>Captures the redirect URI on a same-window navigation and finishes the login.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        Logger.Info($"Nexus login: navigation starting -> {e.Uri}");
        if (TryCapture(e.Uri))
        {
            e.Cancel = true;
            FinishSuccess();
        }
    }

    /// <summary>Surfaces a failed page load (DNS/TLS/network) instead of leaving a blank surface.</summary>
    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            Logger.Info("Nexus login: navigation completed successfully.");
            return;
        }

        Logger.Error($"Nexus login: navigation failed with status {e.WebErrorStatus} " +
                     $"(HTTP {e.HttpStatusCode}).");
        StatusText = $"Couldn't load the sign-in page ({e.WebErrorStatus}). " +
                     "Check your internet connection and try again.";
    }

    /// <summary>Captures the redirect URI on a popup navigation and finishes the login.</summary>
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

    /// <summary>Marks the login complete and closes the dialog (marshalled onto the UI thread, as the event may fire mid-navigation).</summary>
    private void FinishSuccess()
    {
        if (_completed)
        {
            return;
        }
        _completed = true;

        StatusText = "Authorized. Finishing sign-in…";

        Dispatcher.BeginInvoke(() =>
        {
            DialogResult = true;
            Close();
        });
    }

    /// <summary>Detaches the WebView2 handlers before the window closes.</summary>
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

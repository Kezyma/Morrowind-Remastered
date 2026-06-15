using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Web;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>The Nexus account a validated OAuth session belongs to.</summary>
public sealed record NexusAccount(string Name, bool IsPremium);

/// <summary>The authorize URL to show plus the PKCE/state secrets needed to complete the exchange (the UI shows it in a WebView2 and reports the redirect back to <see cref="NexusAuthService.CompleteAsync"/>).</summary>
public sealed record OAuthChallenge(string AuthorizeUrl, string State, string CodeVerifier);

/// <summary>Drives Nexus Mods OAuth (PKCE) exactly as Wabbajack does, so the token written is accepted by the wabbajack-cli.</summary>
/// <remarks>
/// A WebView2 popup handles the interactive part (login page + catching the
/// 127.0.0.1 redirect); this service owns the protocol: <see cref="BeginLogin"/>
/// builds the authorize URL and PKCE secrets, the popup passes the intercepted
/// redirect to <see cref="CompleteAsync"/>, which validates state, exchanges the
/// code, persists it via <see cref="WabbajackTokenStore"/>, and reads the
/// account. Verified against Wabbajack 4.2.1.4's <c>NexusLoginHandler</c>.
/// </remarks>
public sealed class NexusAuthService
{
    /// <summary>Shared HTTP client used for the token and userinfo calls.</summary>
    private readonly HttpClient _http;
    /// <summary>Persists the OAuth token in Wabbajack's store.</summary>
    private readonly WabbajackTokenStore _tokenStore;
    /// <summary>Persisted launcher config (Nexus OAuth settings).</summary>
    private readonly ConfigService _config;

    /// <summary>The OAuth authorize endpoint.</summary>
    private string AuthorizeEndpoint => _config.Current.Nexus.OAuthBase + "/authorize";
    /// <summary>The OAuth token endpoint.</summary>
    private string TokenEndpoint => _config.Current.Nexus.OAuthBase + "/token";
    /// <summary>The OAuth userinfo endpoint.</summary>
    private string UserInfoEndpoint => _config.Current.Nexus.OAuthBase + "/userinfo";

    /// <summary>Host of the redirect URI; the WebView2 popup intercepts navigations here.</summary>
    public string RedirectHost => _config.Current.Nexus.RedirectHost;

    /// <summary>Creates the service over the shared HTTP client, token store and config.</summary>
    public NexusAuthService(HttpClient http, WabbajackTokenStore tokenStore, ConfigService config)
    {
        _http = http;
        _tokenStore = tokenStore;
        _config = config;
    }

    /// <summary>The current account, populated after sign-in/restore.</summary>
    public NexusAccount? Account { get; private set; }

    /// <summary>True when an account is currently signed in.</summary>
    public bool IsSignedIn => Account is not null;

    /// <summary>True when a usable Wabbajack Nexus token already exists on disk, so the CLI can run without an interactive login.</summary>
    public bool HasUsableToken => _tokenStore.HasValidToken();

    /// <summary>Builds the authorize URL and PKCE secrets (RFC 7636) for a new login attempt; the caller shows <see cref="OAuthChallenge.AuthorizeUrl"/> in the popup.</summary>
    public OAuthChallenge BeginLogin()
    {
        var codeVerifier = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")));
        var challengeBytes = SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(challengeBytes);

        var state = Guid.NewGuid().ToString();

        var nexus = _config.Current.Nexus;
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["response_type"] = "code";
        query["scope"] = nexus.Scopes;
        query["code_challenge_method"] = "S256";
        query["client_id"] = nexus.ClientId;
        query["redirect_uri"] = nexus.RedirectUri;
        query["code_challenge"] = codeChallenge;
        query["state"] = state;

        var url = $"{AuthorizeEndpoint}?{query}";
        return new OAuthChallenge(url, state, codeVerifier);
    }

    /// <summary>Completes login from the intercepted redirect URI: validates state, exchanges the code, persists the token, and loads the account; null on failure.</summary>
    public async Task<NexusAccount?> CompleteAsync(
        Uri redirect, OAuthChallenge challenge, CancellationToken ct = default)
    {
        try
        {
            var query = HttpUtility.ParseQueryString(redirect.Query);
            if (query["state"] != challenge.State)
            {
                Logger.Error("OAuth state mismatch on Nexus redirect.");
                return null;
            }

            var code = query["code"];
            if (string.IsNullOrWhiteSpace(code))
            {
                Logger.Error("Nexus redirect did not contain an auth code.");
                return null;
            }

            var token = await ExchangeCodeAsync(code!, challenge.CodeVerifier, ct)
                .ConfigureAwait(false);
            if (token is null)
            {
                return null;
            }

            token.ReceivedAt = DateTime.UtcNow.ToFileTimeUtc();
            _tokenStore.Write(token);

            var account = await LoadAccountAsync(token.AccessToken!, ct).ConfigureAwait(false);
            Account = account;
            return account;
        }
        catch (Exception ex)
        {
            Logger.Error("Nexus OAuth completion failed", ex);
            return null;
        }
    }

    /// <summary>Restores account info from an existing, non-expired token (no UI); null if there is no usable token.</summary>
    public async Task<NexusAccount?> TryRestoreAsync(CancellationToken ct = default)
    {
        var state = _tokenStore.Read();
        if (state?.OAuth is not { } token ||
            string.IsNullOrEmpty(token.AccessToken) ||
            token.IsExpired)
        {
            Account = null;
            return null;
        }

        var account = await LoadAccountAsync(token.AccessToken!, ct).ConfigureAwait(false);
        Account = account;
        return account;
    }

    /// <summary>Signs out by clearing the stored token and current account.</summary>
    public void SignOut()
    {
        _tokenStore.Clear();
        Account = null;
    }

    /// <summary>Exchanges an authorization code (with its PKCE verifier) for a token; null on failure, logging the server's error body which explains why.</summary>
    private async Task<JwtTokenReply?> ExchangeCodeAsync(
        string code, string verifier, CancellationToken ct)
    {
        var nexus = _config.Current.Nexus;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = nexus.ClientId,
            ["redirect_uri"] = nexus.RedirectUri,
            ["code"] = code,
            ["code_verifier"] = verifier
        });

        using var response = await _http.PostAsync(TokenEndpoint, form, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string body;
            try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
            catch { body = string.Empty; }
            Logger.Error($"Nexus token exchange failed: {(int)response.StatusCode} " +
                         $"{response.ReasonPhrase}. {body}");
            return null;
        }

        return await response.Content
            .ReadFromJsonAsync<JwtTokenReply>(cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>Reads account name and premium status from the userinfo endpoint, treating the user as signed in even if userinfo is unavailable (the token is still valid).</summary>
    private async Task<NexusAccount?> LoadAccountAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"Nexus userinfo returned {(int)response.StatusCode}");
                return new NexusAccount("Nexus user", IsPremium: false);
            }

            var info = await response.Content
                .ReadFromJsonAsync<OAuthUserInfo>(cancellationToken: ct)
                .ConfigureAwait(false);

            if (info is null)
            {
                return new NexusAccount("Nexus user", IsPremium: false);
            }

            var premium = info.MembershipRoles.Any(r =>
                r.Contains("premium", StringComparison.OrdinalIgnoreCase) ||
                r.Contains("lifetime", StringComparison.OrdinalIgnoreCase));

            return new NexusAccount(
                Name: string.IsNullOrWhiteSpace(info.Name) ? "Nexus user" : info.Name,
                IsPremium: premium);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load Nexus account info", ex);
            return new NexusAccount("Nexus user", IsPremium: false);
        }
    }

    /// <summary>Base64url-encodes bytes (RFC 7636 style: no padding, URL-safe alphabet).</summary>
    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>Nexus OAuth userinfo response (subset).</summary>
    private sealed class OAuthUserInfo
    {
        /// <summary>The account's subject identifier.</summary>
        [JsonPropertyName("sub")]
        public string Sub { get; set; } = "";

        /// <summary>The account display name.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        /// <summary>The account's membership roles (checked for premium/lifetime).</summary>
        [JsonPropertyName("membership_roles")]
        public string[] MembershipRoles { get; set; } = Array.Empty<string>();
    }
}

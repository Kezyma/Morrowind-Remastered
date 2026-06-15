using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Web;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>The Nexus account a validated OAuth session belongs to.</summary>
public sealed record NexusAccount(string Name, bool IsPremium);

/// <summary>
/// The authorize URL to show plus the PKCE/state secrets needed to complete the
/// exchange. The UI shows <see cref="AuthorizeUrl"/> in a WebView2 and reports
/// the intercepted redirect URI back to <see cref="NexusAuthService.CompleteAsync"/>.
/// </summary>
public sealed record OAuthChallenge(string AuthorizeUrl, string State, string CodeVerifier);

/// <summary>
/// Drives Nexus Mods OAuth (PKCE) exactly as Wabbajack does, so the token we
/// write is accepted by the wabbajack-cli. The interactive part (showing the
/// Nexus login page and catching the <c>https://127.0.0.1:1234</c> redirect) is
/// handled by a WebView2 popup; this service owns the protocol:
///   1. <see cref="BeginLogin"/> builds the authorize URL + PKCE secrets.
///   2. The popup navigates there; when Nexus redirects to 127.0.0.1, the popup
///      passes that URI to <see cref="CompleteAsync"/>.
///   3. We validate state, exchange the code for a token, persist it via
///      <see cref="WabbajackTokenStore"/>, and read the account info.
///
/// Verified against Wabbajack 4.2.1.4's <c>NexusLoginHandler</c>.
/// </summary>
public sealed class NexusAuthService
{
    private const string OAuthBase = "https://users.nexusmods.com/oauth";
    private const string AuthorizeEndpoint = OAuthBase + "/authorize";
    private const string TokenEndpoint = OAuthBase + "/token";
    private const string UserInfoEndpoint = OAuthBase + "/userinfo";

    private const string ClientId = "wabbajack";
    private const string RedirectUri = "https://127.0.0.1:1234";
    private const string Scopes = "public openid profile";

    /// <summary>Host of the redirect URI; the popup intercepts navigations here.</summary>
    public const string RedirectHost = "127.0.0.1";

    private readonly HttpClient _http;
    private readonly WabbajackTokenStore _tokenStore;

    public NexusAuthService(HttpClient http, WabbajackTokenStore tokenStore)
    {
        _http = http;
        _tokenStore = tokenStore;
    }

    /// <summary>The current account, populated after sign-in/restore.</summary>
    public NexusAccount? Account { get; private set; }

    public bool IsSignedIn => Account is not null;

    /// <summary>
    /// True when a usable Wabbajack Nexus token already exists on disk (so the
    /// CLI can run without an interactive login).
    /// </summary>
    public bool HasUsableToken => _tokenStore.HasValidToken();

    /// <summary>
    /// Builds the authorize URL and PKCE secrets for a new login attempt. The
    /// caller shows <see cref="OAuthChallenge.AuthorizeUrl"/> in the popup.
    /// </summary>
    public OAuthChallenge BeginLogin()
    {
        // PKCE (RFC 7636): verifier is a random base64 string; challenge is the
        // base64url(SHA256(verifier)).
        var codeVerifier = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")));
        var challengeBytes = SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(challengeBytes);

        var state = Guid.NewGuid().ToString();

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["response_type"] = "code";
        query["scope"] = Scopes;
        query["code_challenge_method"] = "S256";
        query["client_id"] = ClientId;
        query["redirect_uri"] = RedirectUri;
        query["code_challenge"] = codeChallenge;
        query["state"] = state;

        var url = $"{AuthorizeEndpoint}?{query}";
        return new OAuthChallenge(url, state, codeVerifier);
    }

    /// <summary>
    /// Completes login given the redirect URI the popup intercepted. Validates
    /// state, exchanges the auth code, persists the token, and loads account
    /// info. Returns the account, or null on failure.
    /// </summary>
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

    /// <summary>
    /// Restores account info from an existing, non-expired token (no UI). Returns
    /// null if there is no usable token.
    /// </summary>
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

    public void SignOut()
    {
        _tokenStore.Clear();
        Account = null;
    }

    // --------------------------------------------------------- token exchange

    private async Task<JwtTokenReply?> ExchangeCodeAsync(
        string code, string verifier, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["code"] = code,
            ["code_verifier"] = verifier
        });

        using var response = await _http.PostAsync(TokenEndpoint, form, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Include the server's error body — it usually explains *why* (invalid
            // grant, expired code, etc.), which the status line alone doesn't.
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
                // Token is valid even if userinfo is unavailable; treat as signed in.
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

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>Nexus OAuth userinfo response (subset).</summary>
    private sealed class OAuthUserInfo
    {
        [JsonPropertyName("sub")]
        public string Sub { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("membership_roles")]
        public string[] MembershipRoles { get; set; } = Array.Empty<string>();
    }
}

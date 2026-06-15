using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// The OAuth token reply from Nexus, mirroring Wabbajack's <c>JwtTokenReply</c>
/// exactly (property names matter — the CLI deserializes this).
/// </summary>
public sealed class JwtTokenReply
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    /// <summary>Windows FILETIME (UTC) of when the token was received.</summary>
    [JsonPropertyName("_received_at")]
    public long ReceivedAt { get; set; }

    [JsonPropertyName("token_type")]
    public string? Type { get; set; }

    [JsonPropertyName("expires_in")]
    public ulong ExpiresIn { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    /// <summary>
    /// Mirrors Wabbajack's expiry rule (5-minute safety margin). Used to decide
    /// whether a restored token is still usable.
    /// </summary>
    [JsonIgnore]
    public bool IsExpired =>
        DateTime.FromFileTimeUtc(ReceivedAt)
            + TimeSpan.FromSeconds(ExpiresIn)
            - TimeSpan.FromMinutes(5)
        <= DateTimeOffset.UtcNow;
}

/// <summary>
/// Wabbajack's persisted Nexus login state. Serialized (then DPAPI-encrypted) to
/// <c>%LOCALAPPDATA%\Wabbajack\encrypted\nexus-oauth-info</c>.
/// </summary>
public sealed class NexusOAuthState
{
    [JsonPropertyName("oauth")]
    public JwtTokenReply? OAuth { get; set; } = new();

    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>
/// Reads and writes Wabbajack's Nexus OAuth token file so the wabbajack-cli can
/// authenticate headlessly.
///
/// The on-disk format mirrors Wabbajack 4.x's own
/// <c>Wabbajack.Services.OSIntegrated.ProtectedData</c> (which, despite the
/// name, is NOT Windows DPAPI): TripleDES (CBC/PKCS7) over default-options JSON,
/// with a device key derived from xxHash64 of the %LOCALAPPDATA% path and an IV
/// derived from xxHash64 of the file name. Writing any other format (e.g. real
/// DPAPI, as earlier launcher builds did) makes the CLI crash with
/// "CryptographicException: The input data is not a complete block" as soon as
/// it needs Nexus auth. Reads fall back to the legacy DPAPI format and silently
/// migrate it.
/// </summary>
public sealed class WabbajackTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>True when Wabbajack's Nexus token file exists on disk.</summary>
    public bool HasToken => File.Exists(AppPaths.WabbajackNexusTokenFile);

    /// <summary>
    /// The 24-byte TripleDES key Wabbajack derives per device: xxHash64 of the
    /// UTF8 %LOCALAPPDATA% path (separators normalised to '\', as
    /// Wabbajack.Paths.AbsolutePath.ToString() renders it), concatenated with
    /// two XOR-tweaked variants. Each hash is serialised little-endian.
    /// </summary>
    private static byte[] DeviceKey()
    {
        var raw = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var normalised = string.Join('\\',
            raw.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries));

        var h1 = System.IO.Hashing.XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(normalised));

        var key = new byte[24];
        BitConverter.GetBytes(h1).CopyTo(key, 0);
        BitConverter.GetBytes(h1 ^ 42UL).CopyTo(key, 8);
        BitConverter.GetBytes(h1 ^ (ulong.MaxValue - 42UL)).CopyTo(key, 16);
        return key;
    }

    /// <summary>The 8-byte IV: xxHash64 of the file's name ("nexus-oauth-info").</summary>
    private static byte[] FileIv(string fileName)
        => BitConverter.GetBytes(
            System.IO.Hashing.XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(fileName)));

    private static byte[] Transform(byte[] data, string fileName, bool encrypt)
    {
        using var tdes = TripleDES.Create(); // CBC + PKCS7 defaults, as Wabbajack uses
        using var transform = encrypt
            ? tdes.CreateEncryptor(DeviceKey(), FileIv(fileName))
            : tdes.CreateDecryptor(DeviceKey(), FileIv(fileName));
        return transform.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>
    /// Returns the stored state if present and decryptable, or null. Does not
    /// check expiry (callers can inspect <see cref="JwtTokenReply.IsExpired"/>).
    /// Tokens written by old launcher builds in DPAPI format are migrated to
    /// the Wabbajack format on successful read.
    /// </summary>
    public NexusOAuthState? Read()
    {
        try
        {
            if (!HasToken)
            {
                return null;
            }
            var cipher = File.ReadAllBytes(AppPaths.WabbajackNexusTokenFile);
            var fileName = Path.GetFileName(AppPaths.WabbajackNexusTokenFile);

            try
            {
                var plain = Transform(cipher, fileName, encrypt: false);
                return JsonSerializer.Deserialize<NexusOAuthState>(
                    Encoding.UTF8.GetString(plain), JsonOptions);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                // Legacy: earlier launcher builds wrote real DPAPI. Migrate.
                var plain = ProtectedData.Unprotect(
                    cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
                var state = JsonSerializer.Deserialize<NexusOAuthState>(
                    Encoding.UTF8.GetString(plain), JsonOptions);
                if (state?.OAuth is not null)
                {
                    Logger.Info("Migrating legacy DPAPI Nexus token to Wabbajack format.");
                    Write(state.OAuth);
                }
                return state;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't read Wabbajack Nexus token: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when a usable (non-expired) token is already present.</summary>
    public bool HasValidToken()
    {
        var state = Read();
        return state?.OAuth is { } o && !string.IsNullOrEmpty(o.AccessToken) && !o.IsExpired;
    }

    /// <summary>
    /// Writes the OAuth reply into Wabbajack's encrypted token file. Stamps
    /// <c>_received_at</c> with the current FILETIME (matching Wabbajack) if the
    /// caller hasn't already.
    /// </summary>
    public void Write(JwtTokenReply token)
    {
        if (token.ReceivedAt == 0)
        {
            token.ReceivedAt = DateTime.UtcNow.ToFileTimeUtc();
        }

        var state = new NexusOAuthState { OAuth = token };
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, JsonOptions));
        var fileName = Path.GetFileName(AppPaths.WabbajackNexusTokenFile);
        var cipher = Transform(plain, fileName, encrypt: true);

        Directory.CreateDirectory(AppPaths.WabbajackEncryptedDir);
        File.WriteAllBytes(AppPaths.WabbajackNexusTokenFile, cipher);
        Logger.Info("Wrote Wabbajack Nexus OAuth token (Wabbajack TripleDES format).");
    }

    /// <summary>Removes the token file (used by sign-out).</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.WabbajackNexusTokenFile))
            {
                File.Delete(AppPaths.WabbajackNexusTokenFile);
                Logger.Info("Cleared Wabbajack Nexus OAuth token.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't clear Wabbajack Nexus token: {ex.Message}");
        }
    }
}

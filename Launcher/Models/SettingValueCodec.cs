using System.Globalization;

namespace MorrowindRemasteredLauncher.Models;

/// <summary>Converts between a setting's stored token and the normalised "logical" value the editor binds to.</summary>
/// <remarks>Logical bools are always "true"/"false"; logical numbers are invariant-culture strings. The single place encoding rules live, so loading and applying always agree.</remarks>
public static class SettingValueCodec
{
    /// <summary>Converts a logical editor value into the token to write to the store.</summary>
    public static string ToStored(ValueFormat fmt, string logical)
    {
        logical = logical?.Trim() ?? string.Empty;
        switch (fmt)
        {
            case ValueFormat.BoolTrueFalse:
                return ParseBool(logical) ? "true" : "false";
            case ValueFormat.BoolOnOff:
                return ParseBool(logical) ? "On" : "Off";
            case ValueFormat.BoolOneZero:
                return ParseBool(logical) ? "1" : "0";
            case ValueFormat.Float1:
                return double.TryParse(logical, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                    ? f.ToString("0.0###", CultureInfo.InvariantCulture)
                    : logical;
            case ValueFormat.Int:
                return double.TryParse(logical, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture)
                    : logical;
            default:
                return logical;
        }
    }

    /// <summary>Converts a stored token into the logical value the editor binds to.</summary>
    public static string? FromStored(ValueFormat fmt, string? token)
    {
        if (token is null)
        {
            return null;
        }
        token = token.Trim();
        return fmt switch
        {
            ValueFormat.BoolTrueFalse or ValueFormat.BoolOnOff or ValueFormat.BoolOneZero
                => ParseBool(token) ? "true" : "false",
            _ => token
        };
    }

    /// <summary>Lenient truthiness: accepts true/on/1/yes (case-insensitive).</summary>
    public static bool ParseBool(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }
        s = s.Trim();
        return s.Equals("true", StringComparison.OrdinalIgnoreCase)
            || s.Equals("on", StringComparison.OrdinalIgnoreCase)
            || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || s == "1";
    }
}

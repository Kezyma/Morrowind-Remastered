using System.Text.RegularExpressions;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>
/// Minimal section-aware INI value writer that preserves a file's existing
/// layout and <c>=</c> spacing. Used for OpenMW's <c>settings.cfg</c>
/// (<c>key = value</c>) and MGE's <c>MGE.ini</c> (<c>Key=Value</c>); it adapts
/// to whatever spacing each key already uses. Keys/sections are matched
/// case-insensitively; a missing key is inserted into its section, a missing
/// section is appended.
/// </summary>
public static class IniEditor
{
    /// <summary>Reads a key's value within a section, or null if absent.</summary>
    public static string? GetValue(string[] lines, string section, string key)
    {
        var keyRegex = new Regex($@"^\s*{Regex.Escape(key)}\s*=\s*(?<val>.*?)\s*$",
            RegexOptions.IgnoreCase);
        var inSection = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (inSection)
                {
                    return null; // left the section without finding the key
                }
                inSection = string.Equals(trimmed[1..^1].Trim(), section,
                    StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inSection)
            {
                var m = keyRegex.Match(line);
                if (m.Success)
                {
                    return m.Groups["val"].Value;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Sets <paramref name="key"/> = <paramref name="value"/> within
    /// <paramref name="section"/>. Returns the new lines and whether anything
    /// changed.
    /// </summary>
    public static (string[] Lines, bool Changed) SetValue(
        string[] lines, string section, string key, string value)
    {
        var result = new List<string>(lines);
        var keyRegex = new Regex($@"^(?<indent>\s*){Regex.Escape(key)}\s*=\s*",
            RegexOptions.IgnoreCase);

        var inSection = false;
        var sectionStart = -1; // index of the [section] header
        var sectionEnd = -1;   // index just past the last line of the section

        for (var i = 0; i < result.Count; i++)
        {
            var trimmed = result[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var name = trimmed[1..^1].Trim();
                if (inSection)
                {
                    sectionEnd = i; // first header after our section
                    break;
                }
                if (string.Equals(name, section, StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    sectionStart = i;
                }
                continue;
            }

            if (inSection)
            {
                var m = keyRegex.Match(result[i]);
                if (m.Success)
                {
                    var newLine = m.Value + value;
                    if (newLine == result[i])
                    {
                        return (lines, false);
                    }
                    result[i] = newLine;
                    return (result.ToArray(), true);
                }
            }
        }

        // Key not present: insert into the section, or create the section.
        var writeLine = $"{key}={value}";
        if (sectionStart >= 0)
        {
            var insertAt = sectionEnd >= 0 ? sectionEnd : result.Count;
            result.Insert(insertAt, writeLine);
        }
        else
        {
            if (result.Count > 0 && result[^1].Trim().Length > 0)
            {
                result.Add(string.Empty);
            }
            result.Add($"[{section}]");
            result.Add(writeLine);
        }
        return (result.ToArray(), true);
    }
}

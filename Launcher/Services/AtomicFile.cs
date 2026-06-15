using System.IO;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Small helpers for crash-safe file writes (write to .tmp, then move).</summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="lines"/> to <paramref name="path"/> atomically: the
    /// content is written to a sibling ".tmp" file and then moved over the target,
    /// so a crash mid-write can't leave a half-written config.
    /// </summary>
    public static void WriteLines(string path, string[] lines)
    {
        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, path, overwrite: true);
    }
}

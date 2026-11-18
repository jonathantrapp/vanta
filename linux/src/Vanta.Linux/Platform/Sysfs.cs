namespace HwSuite.Platform;

/// <summary>Small helpers for reading Linux sysfs/procfs.</summary>
internal static class Sysfs
{
    /// <summary>Follow every symbolic link in the path (realpath).</summary>
    public static string RealPath(string path)
    {
        var parts = Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        string cur = "/";
        for (int i = 0; i < parts.Length; i++)
        {
            string next = Path.Combine(cur, parts[i]);
            for (int hops = 0; hops < 40; hops++)
            {
                FileSystemInfo fi = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (fi.LinkTarget == null) break;
                next = Path.GetFullPath(Path.IsPathRooted(fi.LinkTarget) ? fi.LinkTarget : Path.Combine(cur, fi.LinkTarget));
            }
            cur = next;
        }
        return cur;
    }

    public static string? ReadText(string path) { try { return File.ReadAllText(path).Trim(); } catch { return null; } }

    public static long? ReadLong(string path) => long.TryParse(ReadText(path), out var v) ? v : null;

    public static bool WriteText(string path, string value, out string? error)
    {
        try { File.WriteAllText(path, value); error = null; return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}

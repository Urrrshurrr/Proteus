namespace Proteus.Core;

/// <summary>
/// Where Proteus keeps its settings, presets, history and log. Normally under AppData; when a file
/// named "portable.txt" sits next to the exe, everything goes in a "Data" folder beside it instead,
/// so the app leaves nothing behind on the machine.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<bool> Portable = new(() =>
    {
        try { return File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")); }
        catch (Exception) { return false; }
    });

    public static bool IsPortable => Portable.Value;

    /// <summary>Machine-local data (history, log).</summary>
    public static string Local => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Proteus");

    /// <summary>Per-user data that may roam (settings, presets).</summary>
    public static string Roaming => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Proteus");

    public static string History => Path.Combine(Local, "History");
    public static string Presets => Path.Combine(Roaming, "Presets");
    public static string Settings => Path.Combine(Roaming, "settings.json");
    public static string Log => Path.Combine(Local, "proteus.log");
}

/// <summary>Tiny append-only error log, so problems are never silent.</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static void Write(string context, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.Log)!);
                var info = new FileInfo(AppPaths.Log);
                if (info.Exists && info.Length > 2_000_000) info.Delete(); // keep it small
                File.AppendAllText(AppPaths.Log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{(ex != null ? Environment.NewLine + ex : "")}{Environment.NewLine}");
            }
        }
        catch (Exception) { /* logging must never throw */ }
    }
}

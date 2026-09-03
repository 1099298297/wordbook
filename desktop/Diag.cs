namespace Wordbook;

internal static class Diag
{
    public static string LogPath => Path.Combine(Path.GetTempPath(), "wordbook-debug.log");

    public static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine); }
        catch { /* 日志失败不影响运行 */ }
    }
}

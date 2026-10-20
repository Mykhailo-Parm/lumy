namespace Lumy.App;

/// <summary>
/// Writes unhandled errors to %LOCALAPPDATA%\Lumy\crash.log, so a window that closes on its own leaves a trace.
/// </summary>
internal static class CrashLog
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumy", "crash.log");

    public static void Write(Exception? exception)
    {
        if (exception is null)
            return;

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

namespace Lumy.App;

/// <summary>
/// Writes unhandled errors to %LOCALAPPDATA%\Lumy\crash.log, so a window that closes on its own leaves a trace.
/// </summary>
internal static class CrashLog
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumy", "crash.log");

    /// <summary>Errors written since Lumy started; the self-test fails if this is not zero.</summary>
    public static int Count { get; private set; }

    public static void Write(Exception? exception)
    {
        if (exception is null)
            return;

        Count++;
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

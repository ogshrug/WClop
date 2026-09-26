using System.Globalization;

namespace WClop.Core.Logging;

/// <summary>
/// Minimal thread-safe file log at <c>%LOCALAPPDATA%\WClop\logs\wclop.log</c>, rotated at 5 MB.
/// </summary>
public static class Log
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath { get; set; } = Path.Combine(AppPaths.LocalAppDataDir, "logs", "wclop.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, line);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

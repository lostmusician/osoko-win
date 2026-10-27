using System.Diagnostics;

namespace Osoko.Core;

/// <summary>
/// Tiny append-only file logger (<c>%LOCALAPPDATA%\Osoko\logs\osoko.log</c>).
/// Logging never throws: a failed write must not take the app down.
/// </summary>
public static class Log
{
    private const long MaxLogBytes = 1_048_576; // 1 MiB
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        Debug.WriteLine($"[Osoko] {line}");

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);

                var file = new FileInfo(AppPaths.LogFilePath);
                if (file.Exists && file.Length > MaxLogBytes)
                {
                    File.Move(file.FullName, file.FullName + ".old", overwrite: true);
                }

                File.AppendAllText(AppPaths.LogFilePath, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Nothing sensible to do — diagnostics are best effort.
        }
    }
}

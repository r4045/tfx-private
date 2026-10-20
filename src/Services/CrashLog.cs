using System.IO;
using System.Text;
using Path = System.IO.Path;

namespace Tfx;

/// <summary>
/// Appends unhandled-exception reports to %LOCALAPPDATA%\tfx\crash.log so a
/// crash that used to close the window silently leaves a stack trace behind.
/// Every method is best-effort: logging must never throw from inside the
/// exception handlers that call it.
/// </summary>
internal static class CrashLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "tfx",
        "crash.log");

    public static void Write(string source, Exception? exception)
    {
        try
        {
            var builder = new StringBuilder();
            builder.Append("==== ")
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(source).Append("] tfx ")
                .Append(typeof(CrashLog).Assembly.GetName().Version)
                .Append(" / ").Append(Environment.OSVersion.VersionString)
                .AppendLine();
            builder.AppendLine(exception?.ToString() ?? "(no exception object)");
            builder.AppendLine();

            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                RotateIfLarge();
                File.AppendAllText(LogPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private static void RotateIfLarge()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Move(LogPath, LogPath + ".old", overwrite: true);
            }
        }
        catch
        {
        }
    }
}

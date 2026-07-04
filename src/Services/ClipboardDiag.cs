using System.IO;
using System.Windows;
using Path = System.IO.Path;

namespace Tfx;

/// <summary>
/// Opt-in clipboard diagnostics for interop issues that only reproduce in
/// specific environments (notably RDP, where the clipboard exposes different
/// formats than a local copy). Enabled by setting TFX_CLIP_DEBUG=1 before
/// launching; appends to %TEMP%\tfx-clipboard.log. A no-op otherwise, so it
/// is safe to leave in release builds.
/// </summary>
internal static class ClipboardDiag
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("TFX_CLIP_DEBUG") == "1";

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "tfx-clipboard.log");

    public static void Log(string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break the operation being diagnosed.
        }
    }

    /// <summary>
    /// Logs the formats currently on the clipboard, both as published and
    /// with WPF's automatic conversions applied (the latter is what
    /// ContainsFileDropList effectively sees).
    /// </summary>
    public static void LogFormats(string context)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null)
            {
                Log($"{context}: no data object on clipboard");
                return;
            }

            Log($"{context}: formats = [{string.Join(", ", data.GetFormats(false))}]" +
                $", with conversion = [{string.Join(", ", data.GetFormats(true))}]");
        }
        catch (Exception ex)
        {
            Log($"{context}: reading clipboard formats failed: {ex.Message}");
        }
    }
}

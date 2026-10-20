using System.Diagnostics;
using System.Threading.Tasks;

namespace Tfx;

/// <summary>
/// Hands a copy / move to the external tool configured in
/// <c>[fileOperations]</c> (FastCopy, TeraCopy, ...). The tool owns conflict
/// handling and progress UI; tfx only waits for it to exit so it can refresh
/// the panes.
/// </summary>
internal static class ExternalFileTool
{
    public static async Task<int> RunAsync(FileOperationsConfig config, IReadOnlyList<string> sources, string destination, bool isMove)
    {
        var template = (isMove ? config.MoveArgs : config.CopyArgs) ?? "";
        var tool = Environment.ExpandEnvironmentVariables(AppConfig.ExpandUserPath(config.Tool ?? ""));
        var arguments = template
            .Replace("{sources}", string.Join(" ", sources.Select(Quote)), StringComparison.OrdinalIgnoreCase)
            // No trailing separator, so templates can add their own
            // ("{dest}\" for "copy into"): "D:\" becomes "D:".
            .Replace("{dest}", destination.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        using var process = Process.Start(new ProcessStartInfo(tool, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = destination,
        }) ?? throw new InvalidOperationException(Loc.F("Could not start {0}", tool));

        await process.WaitForExitAsync().ConfigureAwait(true);
        return process.ExitCode;
    }

    /// <summary>
    /// Quotes for the standard Windows argv rules: embedded quotes are
    /// backslash-escaped and trailing backslashes doubled so they don't
    /// escape the closing quote ("D:\" would otherwise parse as D:").
    /// </summary>
    private static string Quote(string path)
    {
        var escaped = path.Replace("\"", "\\\"");
        var trailing = escaped.Length - escaped.TrimEnd('\\').Length;
        return "\"" + escaped + new string('\\', trailing) + "\"";
    }
}

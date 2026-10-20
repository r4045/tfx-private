using System.Diagnostics;
using System.IO;
using Path = System.IO.Path;

namespace Tfx;

/// <summary>
/// Shell namespace locations that are not real folders (the Recycle Bin, This PC,
/// Control Panel, ...). tfx can only list real directories, so a bookmark or a
/// typed path in one of these forms is handed to Windows Explorer instead:
///   • <c>shell:RecycleBinFolder</c> (any <c>shell:</c> known-folder name)
///   • <c>::{645FF040-5081-101B-9F08-00AA002F954E}</c> (a CLSID path)
/// </summary>
internal static class ShellLocation
{
    public const string RecycleBin = "shell:RecycleBinFolder";

    private const string RecycleBinClsid = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    public static bool IsShellLocation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        var p = path.Trim();
        return p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("::{", StringComparison.Ordinal);
    }

    /// <summary>Default label for a shell-location bookmark with no alias.</summary>
    public static string DisplayName(string path)
    {
        var p = path.Trim();
        if (p.Equals(RecycleBin, StringComparison.OrdinalIgnoreCase)
            || p.Equals(RecycleBinClsid, StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("Recycle Bin");
        }
        return p;
    }

    /// <summary>
    /// Opens the location in a new Explorer window. Returns false (with a
    /// message) when the string is malformed or Explorer could not be started.
    /// </summary>
    public static bool TryOpen(string path, out string? error)
    {
        error = null;
        var p = path.Trim();
        // A quote would let the string escape the single argument we pass.
        if (p.Contains('"') || p.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            error = Loc.F("Invalid path: {0}", path);
            return false;
        }

        // Absolute path in the Windows directory so a planted explorer.exe on
        // PATH / in the CWD is never picked up.
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var explorerExe = Path.Combine(windowsDir, "explorer.exe");
        try
        {
            Process.Start(new ProcessStartInfo(explorerExe, $"\"{p}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = Loc.F("Failed to open Explorer: {0}", ex.Message);
            return false;
        }
    }
}

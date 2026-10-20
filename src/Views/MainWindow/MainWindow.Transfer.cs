using System.IO;
using System.Windows.Controls;
using System.Windows.Interop;
using Path = System.IO.Path;

namespace Tfx;

/// <summary>
/// Background execution of copy / move for paste and drag-and-drop. The
/// planning (conflict prompts, target names) is done by the callers on the
/// UI thread; this part runs the shell's IFileOperation (or the external
/// tool from [fileOperations]) without blocking the dispatcher, then checks
/// the file system and reports the outcome in the status bar.
/// </summary>
public partial class MainWindow
{
    private async Task RunTransfersAsync(
        DataGrid destGrid,
        string destination,
        IReadOnlyList<FileTransferItem> shellItems,
        IReadOnlyList<string> externalCopies,
        IReadOnlyList<string> externalMoves,
        List<string> failed)
    {
        var total = shellItems.Count + externalCopies.Count + externalMoves.Count;
        if (total == 0)
        {
            if (failed.Count > 0)
            {
                SetStatus(Loc.F("Failed: {0}", string.Join(", ", failed)));
            }
            return;
        }

        var anyMove = externalMoves.Count > 0 || shellItems.Any(i => i.IsMove);
        SetStatus(anyMove ? Loc.F("Moving {0} item(s)...", total) : Loc.F("Copying {0} item(s)...", total));

        var succeeded = 0;
        var leftBehind = new List<string>();
        var notCompleted = new List<string>();
        string? lastWrittenName = null;

        try
        {
            if (shellItems.Count > 0)
            {
                var owner = new WindowInteropHelper(this).Handle;
                await ShellFileOperation.RunAsync(shellItems, owner);

                foreach (var item in shellItems)
                {
                    var written = File.Exists(item.Target) || Directory.Exists(item.Target);
                    if (!written)
                    {
                        // Cancelled, skipped in the shell's error dialog, or failed.
                        notCompleted.Add(Path.GetFileName(item.Source));
                        continue;
                    }

                    // A move that "succeeded" while the source is still there
                    // means the copy-then-delete fallback (cross-volume) could
                    // not delete the source.
                    if (item.IsMove && (File.Exists(item.Source) || Directory.Exists(item.Source)))
                    {
                        leftBehind.Add(Path.GetFileName(item.Source));
                    }
                    else
                    {
                        succeeded++;
                    }
                    lastWrittenName = item.NewName;
                }
            }

            if (externalCopies.Count > 0)
            {
                if (await RunExternalToolAsync(externalCopies, destination, isMove: false, failed))
                {
                    succeeded += externalCopies.Count;
                    lastWrittenName = Path.GetFileName(externalCopies[^1]);
                }
            }

            if (externalMoves.Count > 0)
            {
                if (await RunExternalToolAsync(externalMoves, destination, isMove: true, failed))
                {
                    succeeded += externalMoves.Count;
                    lastWrittenName = Path.GetFileName(externalMoves[^1]);
                }
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("RunTransfersAsync", ex);
            failed.Add(ex.Message);
        }

        // Only pull focus back if the user is still looking at tfx; after a
        // long copy they may be working in another window.
        RefreshPanesAfterPaste(lastWrittenName, destGrid, recoverFocus: IsActive);

        if (leftBehind.Count > 0)
        {
            SetStatus(Loc.F("Pasted {0}; source remained for: {1}", succeeded, string.Join(", ", leftBehind)));
        }
        else if (failed.Count > 0 || notCompleted.Count > 0)
        {
            var parts = failed.Concat(notCompleted.Select(n => Loc.F("{0} (not completed)", n)));
            SetStatus(Loc.F("Pasted {0}; failed: {1}", succeeded, string.Join(", ", parts)));
        }
        else
        {
            SetStatus(Loc.F("Pasted {0} item(s)", succeeded));
        }
    }

    /// <summary>
    /// Like <see cref="FsHelpers.NextAvailablePath"/>, but also treats names
    /// already planned in this batch as taken (two sources with the same name
    /// from different folders must not both target "name (2)").
    /// </summary>
    private static string NextAvailablePathExcluding(string path, IReadOnlyList<FileTransferItem> planned)
    {
        bool Taken(string candidate) =>
            File.Exists(candidate) || Directory.Exists(candidate) ||
            planned.Any(i => FsHelpers.SamePath(i.Target, candidate));

        if (!Taken(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!Taken(candidate))
            {
                return candidate;
            }
        }
    }

    private async Task<bool> RunExternalToolAsync(IReadOnlyList<string> sources, string destination, bool isMove, List<string> failed)
    {
        var toolName = Path.GetFileName(_config.FileOperations.Tool ?? "");
        try
        {
            var exitCode = await ExternalFileTool.RunAsync(_config.FileOperations, sources, destination, isMove);
            if (exitCode != 0)
            {
                failed.Add(Loc.F("{0} exited with code {1}", toolName, exitCode));
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            failed.Add(Loc.F("{0} failed: {1}", toolName, ex.Message));
            return false;
        }
    }
}

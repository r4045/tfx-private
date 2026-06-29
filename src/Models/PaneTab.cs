namespace Tfx;

/// <summary>
/// One navigation context inside a pane. A pane shows a single DataGrid /
/// IconView at a time; switching tabs swaps which <see cref="PaneTab"/> drives
/// that control. Each tab owns its own current folder, back / forward history,
/// and a remembered selection so switching back restores where the user was.
/// </summary>
internal sealed class PaneTab(string path)
{
    public string Path { get; set; } = path;

    /// <summary>Back-history stack (most recent at the end).</summary>
    public List<string> Back { get; } = [];

    /// <summary>Forward-history stack (most recent at the end).</summary>
    public List<string> Forward { get; } = [];

    /// <summary>
    /// Name of the row that was selected when the user last left this tab, so
    /// switching back can restore the selection / focus.
    /// </summary>
    public string? SelectedName { get; set; }

    /// <summary>
    /// Whether this tab is pinned. A pinned tab stays fixed on its folder and
    /// at its strip position: navigating it opens a new tab instead of moving
    /// it (see <c>Navigate</c>), and it cannot be closed — Ctrl+W, middle-click
    /// and the close button are all suppressed while pinned.
    /// </summary>
    public bool Pinned { get; set; }
}

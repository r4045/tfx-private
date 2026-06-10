using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Tfx;

public partial class MainWindow
{
    private enum MoveMode
    {
        /// <summary>Arrow keys + built-in type-ahead (incremental search).</summary>
        Explorer,

        /// <summary>Arrow keys + vi letter keys (j/k/h/l/g/G); type-ahead off.</summary>
        Vi,
    }

    // Not persisted by design: always starts in Explorer mode. Toggled by the
    // changeMoveMode shortcut (F1 by default, configurable in config.toml
    // [shortcuts]).
    private MoveMode _moveMode = MoveMode.Explorer;

    // Accent used for the Name column header + the status-bar mode label while
    // in Vi mode. A mid red that stays readable on both light and dark themes.
    // Frozen so the theme system never mutates it.
    private static readonly Brush ViAccentBrush = CreateFrozenBrush(Color.FromRgb(0xE0, 0x4C, 0x4C));

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void ToggleMoveMode()
    {
        _moveMode = _moveMode == MoveMode.Explorer ? MoveMode.Vi : MoveMode.Explorer;
        ApplyMoveMode();
        SetStatus(Loc.F("Move mode: {0}", _moveMode == MoveMode.Vi ? Loc.T("VI") : Loc.T("EXPLORER")));
    }

    /// <summary>
    /// Reflects the current move mode into behaviour and UI. Called once at
    /// startup and on every toggle.
    ///   • Built-in type-ahead (TextSearch) is ON in Explorer mode and OFF in Vi
    ///     mode — Vi uses letter keys to navigate, so type-ahead must not eat
    ///     them.
    ///   • The status-bar label shows EXPLORER / VI.
    ///   • The Name column header turns red in Vi mode (Details view only; the
    ///     icon view has no header, so the status label is the cue there).
    /// </summary>
    private void ApplyMoveMode()
    {
        var vi = _moveMode == MoveMode.Vi;

        // Type-ahead reads FileItem.Name. The Name column is templated, so an
        // explicit TextPath is required for the built-in search to work.
        SetListingTypeAhead(LeftGrid, !vi);
        SetListingTypeAhead(RightGrid, !vi);
        SetListingTypeAhead(LeftIconView, !vi);
        SetListingTypeAhead(RightIconView, !vi);

        SetNameHeaderAccent(LeftNameColumn, vi);
        SetNameHeaderAccent(RightNameColumn, vi);

        MoveModeText.Text = vi ? Loc.T("VI") : Loc.T("EXPLORER");
        MoveModeText.Foreground = vi ? ViAccentBrush : (Brush)FindResource("TfxMuted");
    }

    private static void SetListingTypeAhead(ItemsControl listing, bool enabled)
    {
        listing.SetValue(TextSearch.TextPathProperty, "Name");
        listing.SetValue(ItemsControl.IsTextSearchEnabledProperty, enabled);
    }

    /// <summary>
    /// Colours a column header red (Vi) or restores the default themed header
    /// (Explorer). Uses HeaderStyle based on the implicit DataGridColumnHeader
    /// style rather than replacing the Header content, so the themed look is
    /// preserved and nothing that reads Header as a string breaks.
    /// </summary>
    private void SetNameHeaderAccent(DataGridColumn column, bool vi)
    {
        var baseStyle = TryFindResource(typeof(DataGridColumnHeader)) as Style;
        if (!vi)
        {
            // Reapply the themed header style explicitly. Setting HeaderStyle =
            // null leaves a local null that suppresses the implicit
            // DataGridColumnHeader style, leaving a bare white header.
            column.HeaderStyle = baseStyle;
            return;
        }

        var style = new Style(typeof(DataGridColumnHeader), baseStyle);
        style.Setters.Add(new Setter(Control.ForegroundProperty, ViAccentBrush));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
        column.HeaderStyle = style;
    }
}

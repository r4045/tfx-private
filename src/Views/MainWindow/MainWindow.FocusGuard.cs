using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Tfx;

/// <summary>
/// Keeps keyboard focus from getting "lost" (no focus on either listing and no
/// keyboard way back), e.g. when a modal dialog closes and WPF tries to restore
/// focus to a row a reload has since destroyed. In that state
/// Keyboard.FocusedElement is null, the window itself, or a detached element.
/// </summary>
public partial class MainWindow
{
    private void InitializeFocusGuard()
    {
        // Owner window reactivated (a modal dialog closed, or the user came back
        // via Alt+Tab): if WPF failed to restore focus anywhere meaningful,
        // hand it back to the active listing.
        Activated += (_, _) =>
            Dispatcher.BeginInvoke(RecoverListingFocusIfLost, DispatcherPriority.ApplicationIdle);

        // Focus dropped out of the window while it is still the active one
        // (e.g. the focused row was removed by a reload).
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is false && IsActive)
            {
                Dispatcher.BeginInvoke(RecoverListingFocusIfLost, DispatcherPriority.ApplicationIdle);
            }
        };
    }

    private void RecoverListingFocusIfLost()
    {
        if (IsActive && IsKeyboardFocusLost())
        {
            StartListingFocusRecovery();
        }
    }

    /// <summary>
    /// True when keyboard focus is nowhere useful: no element, the window
    /// itself, or an element that is no longer in the live visual tree.
    /// </summary>
    private bool IsKeyboardFocusLost()
    {
        var focused = Keyboard.FocusedElement;
        if (focused is null || ReferenceEquals(focused, this))
        {
            return true;
        }
        if (focused is DependencyObject d)
        {
            if (PresentationSource.FromDependencyObject(d) is null)
            {
                return true;
            }
            if (d is UIElement u && !u.IsVisible)
            {
                return true;
            }
        }
        return false;
    }
}

using System.Windows;

namespace SpeedEmulator.Views;

internal static class WindowNavigation
{
    public static void ShowAsCurrent(Window owner, Window child)
    {
        child.Owner ??= owner;
        child.ShowInTaskbar = true;

        var shouldRestoreOwner = owner.IsVisible;
        EventHandler? closedHandler = null;
        closedHandler = (_, _) =>
        {
            child.Closed -= closedHandler;
            if (shouldRestoreOwner
                && owner.IsLoaded
                && Application.Current is { Dispatcher.HasShutdownStarted: false })
            {
                owner.Show();
                owner.Activate();
            }
        };

        child.Closed += closedHandler;
        try
        {
            if (shouldRestoreOwner)
            {
                owner.Hide();
            }

            child.Show();
            child.Activate();
        }
        catch
        {
            child.Closed -= closedHandler;
            if (shouldRestoreOwner
                && owner.IsLoaded
                && Application.Current is { Dispatcher.HasShutdownStarted: false })
            {
                owner.Show();
                owner.Activate();
            }

            throw;
        }
    }
}

using System.Windows;

namespace SpeedEmulator.Views;

internal static class WindowNavigation
{
    public static bool? ShowDialogAsCurrent(Window owner, Window child)
    {
        child.Owner ??= owner;
        child.ShowInTaskbar = true;

        var shouldRestoreOwner = owner.IsVisible;
        try
        {
            if (shouldRestoreOwner)
            {
                owner.Hide();
            }

            return child.ShowDialog();
        }
        finally
        {
            if (shouldRestoreOwner
                && owner.IsLoaded
                && Application.Current is { Dispatcher.HasShutdownStarted: false })
            {
                owner.Show();
                owner.Activate();
            }
        }
    }
}

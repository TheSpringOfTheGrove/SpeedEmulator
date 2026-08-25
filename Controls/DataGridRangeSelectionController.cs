using System.Windows.Controls;
using System.Windows.Input;

namespace SpeedEmulator.Controls;

internal sealed class DataGridRangeSelectionController
{
    private object? anchorItem;
    private bool isApplyingRangeSelection;

    public void SynchronizeAnchor(DataGrid grid)
    {
        if (isApplyingRangeSelection
            || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            return;
        }

        if (grid.SelectedItems.Count == 0)
        {
            anchorItem = null;
            return;
        }

        if (grid.SelectedItems.Count == 1)
        {
            anchorItem = grid.SelectedItem ?? grid.SelectedItems[0];
        }
    }

    public bool HandleModifierSelection(
        DataGrid grid,
        DataGridCell cell,
        object rowItem,
        MouseButtonEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            SelectRange(
                grid,
                cell,
                rowItem,
                additive: (modifiers & ModifierKeys.Control) != 0);
            e.Handled = true;
            return true;
        }

        anchorItem = rowItem;
        return (modifiers & ModifierKeys.Control) != 0;
    }

    internal void SelectRange(
        DataGrid grid,
        DataGridCell cell,
        object targetItem,
        bool additive)
    {
        var targetIndex = grid.Items.IndexOf(targetItem);
        if (targetIndex < 0)
        {
            return;
        }

        var anchorIndex = anchorItem is null ? -1 : grid.Items.IndexOf(anchorItem);
        if (anchorIndex < 0 && grid.SelectedItem is not null)
        {
            anchorItem = grid.SelectedItem;
            anchorIndex = grid.Items.IndexOf(anchorItem);
        }

        if (anchorIndex < 0)
        {
            anchorItem = targetItem;
            anchorIndex = targetIndex;
        }

        isApplyingRangeSelection = true;
        try
        {
            if (!additive)
            {
                grid.SelectedItems.Clear();
            }

            AddSelectedItem(grid, targetItem);

            var firstIndex = Math.Min(anchorIndex, targetIndex);
            var lastIndex = Math.Max(anchorIndex, targetIndex);
            for (var index = firstIndex; index <= lastIndex; index++)
            {
                AddSelectedItem(grid, grid.Items[index]);
            }

            if (cell.Column is not null)
            {
                grid.CurrentCell = new DataGridCellInfo(targetItem, cell.Column);
            }

            cell.Focus();
        }
        finally
        {
            isApplyingRangeSelection = false;
        }
    }

    private static void AddSelectedItem(DataGrid grid, object item)
    {
        if (!grid.SelectedItems.Contains(item))
        {
            grid.SelectedItems.Add(item);
        }
    }
}

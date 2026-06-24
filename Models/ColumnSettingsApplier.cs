namespace SpeedEmulator.Models;

public static class ColumnSettingsApplier
{
    public static void Apply(IEnumerable<ColumnDefinition> columns, IReadOnlyList<BankUserColumnSetting> settings)
    {
        var settingsByField = settings
            .Where(item => !string.IsNullOrWhiteSpace(item.Field))
            .GroupBy(item => item.Field, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var column in columns)
        {
            if (IsIdColumn(column))
            {
                column.Show = true;
                column.Order = -1;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(column.Field) && settingsByField.TryGetValue(column.Field, out var setting))
            {
                column.Width = setting.Width <= 0 ? 100 : setting.Width;
                column.Order = setting.Order;
                column.Show = setting.Show;
                continue;
            }

            column.Width = column.Width <= 0 ? 100 : column.Width;
            column.Order = column.Order;
            column.Show = column.Show;
        }
    }

    private static bool IsIdColumn(ColumnDefinition column)
    {
        return string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase);
    }
}

namespace NetScope;
sealed record GridRow(string Key, object? Tag, params object?[] Values);
readonly record struct GridChanges(int Inserted, int Removed, int Moved, int Cells);
sealed class GridRows
{
    readonly Dictionary<string, DataGridViewRow> rows = new();
    public GridChanges Apply(DataGridView grid, IReadOnlyList<GridRow> desired)
    {
        int inserted = 0, removed = 0, moved = 0, cells = 0;
        var keys = desired.Select(r => r.Key).ToHashSet();
        int topIndex = grid.FirstDisplayedScrollingRowIndex;
        DataGridViewRow? top = topIndex >= 0 && topIndex < grid.Rows.Count ? grid.Rows[topIndex] : null;
        var selected = grid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault();
        var currentCell = grid.CurrentCell;
        int horizontal = grid.HorizontalScrollingOffset;
        grid.SuspendLayout();
        try
        {
            foreach (var key in rows.Keys.Where(k => !keys.Contains(k)).ToArray())
            {
                grid.Rows.Remove(rows[key]); rows.Remove(key); removed++;
            }
            for (int index = 0; index < desired.Count; index++)
            {
                var item = desired[index];
                if (!rows.TryGetValue(item.Key, out var row))
                {
                    row = new DataGridViewRow { Height = grid.RowTemplate.Height, MinimumHeight = grid.RowTemplate.MinimumHeight }; row.CreateCells(grid); grid.Rows.Insert(index, row); row = grid.Rows[index]; rows[item.Key] = row; inserted++;
                }
                else if (row.Index != index)
                {
                    grid.Rows.Remove(row); grid.Rows.Insert(index, row); row = grid.Rows[index]; rows[item.Key] = row; moved++;
                }
                row.Tag = item.Tag;
                for (int column = 0; column < item.Values.Length; column++)
                    if (!Equals(row.Cells[column].Value, item.Values[column])) { row.Cells[column].Value = item.Values[column]; cells++; }
            }
            // Restore focus only after structural edits. Ordinary samples never touch selection or scrolling.
            if (inserted + removed + moved > 0)
            {
                if (currentCell?.DataGridView == grid) grid.CurrentCell = currentCell;
                if (selected?.DataGridView == grid && !selected.Selected) { grid.ClearSelection(); selected.Selected = true; }
                int target = top?.DataGridView == grid ? top.Index : Math.Min(topIndex, grid.Rows.Count - 1);
                if (target >= 0 && grid.FirstDisplayedScrollingRowIndex != target) grid.FirstDisplayedScrollingRowIndex = target;
                if (grid.HorizontalScrollingOffset != horizontal) grid.HorizontalScrollingOffset = horizontal;
            }
        }
        finally { grid.ResumeLayout(false); }
        return new(inserted, removed, moved, cells);
    }
}

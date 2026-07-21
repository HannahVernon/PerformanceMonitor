/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PerformanceMonitor.Ui;

/// <summary>
/// Makes right-clicking a DataGrid select the row AND make the cell under the cursor current. WPF's
/// DataGrid does neither on right-click by default, so context-menu actions that read
/// <c>SelectedItem</c>/<c>CurrentItem</c> (e.g. "View Plan", "Copy Row") or <c>CurrentCell</c>
/// (e.g. "Copy Cell") silently do nothing on a bare right-click — and any prior left-click selection
/// is most commonly cleared by a background auto-refresh re-binding <c>ItemsSource</c>. Setting both
/// the selected row and the current cell under the cursor makes every context-menu item act on what
/// the user actually clicked, regardless of how the grid attaches its menu (row-level or grid-level).
/// Call <see cref="Enable"/> once at application startup to apply this app-wide.
/// </summary>
public static class DataGridRowSelectionBehavior
{
    private static bool _enabled;

    /// <summary>
    /// Registers a class handler so every <see cref="DataGrid"/> in the app selects the row and makes
    /// the cell under the cursor current on right-click. Idempotent.
    /// </summary>
    public static void Enable()
    {
        if (_enabled) return;
        _enabled = true;

        EventManager.RegisterClassHandler(
            typeof(DataGrid),
            UIElement.PreviewMouseRightButtonDownEvent,
            new MouseButtonEventHandler(OnPreviewRightButtonDown));
    }

    private static void OnPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        // Walk up from the actual clicked element (a cell's content), capturing the DataGridCell on
        // the way to its DataGridRow. The cell is a descendant of the row, so it is seen first.
        DataGridCell? cell = null;
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridRow)
        {
            if (cell == null && dep is DataGridCell c) cell = c;
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (dep is DataGridRow row)
        {
            row.IsSelected = true;
            grid.SelectedItem = row.Item;
        }

        // Also make the clicked cell current so cell-scoped items (Copy Cell) act on it. This sets
        // CurrentItem too, which row-scoped items (Copy Row) fall back to. We do NOT mark the event
        // handled — the context menu must still open.
        if (cell?.Column != null)
        {
            grid.CurrentCell = new DataGridCellInfo(cell);
        }
    }
}

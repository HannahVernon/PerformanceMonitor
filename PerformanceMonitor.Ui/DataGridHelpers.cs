/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PerformanceMonitor.Ui;

/// <summary>
/// Shared visual-tree helpers for resolving the owning <see cref="DataGrid"/> of a context-menu / element,
/// used by Lite's and Darling's grid context menus and column filters. (The deprecated Full Dashboard is not
/// developed further and keeps its own inline copies.)
/// </summary>
public static class DataGridHelpers
{
    /// <summary>The DataGrid whose row context menu raised this event (sender is the clicked MenuItem).</summary>
    public static DataGrid? FindParentDataGrid(object sender)
    {
        if (sender is not MenuItem menuItem) return null;
        var contextMenu = menuItem.Parent as ContextMenu;
        var target = contextMenu?.PlacementTarget as FrameworkElement;
        while (target != null && target is not DataGrid)
        {
            target = VisualTreeHelper.GetParent(target) as FrameworkElement;
        }
        return target as DataGrid;
    }

    /// <summary>Walks up the visual tree from any element to its owning DataGrid.</summary>
    public static DataGrid? FindParentDataGridFromElement(DependencyObject element)
    {
        var current = element;
        while (current != null)
        {
            if (current is DataGrid dg)
                return dg;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}

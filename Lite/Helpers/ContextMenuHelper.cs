/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace PerformanceMonitorLite.Helpers;

/// <summary>
/// Shared context menu helpers for DataGrid copy/export operations.
/// Used by standalone windows (history, collection log, manage servers, settings)
/// that don't have the full ServerTab context menu infrastructure.
/// </summary>
public static class ContextMenuHelper
{
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

    public static string GetCellValue(DataGridColumn col, object item)
    {
        if (col is DataGridBoundColumn boundCol
            && boundCol.Binding is Binding binding)
        {
            var prop = item.GetType().GetProperty(binding.Path.Path);
            return FormatForExport(prop?.GetValue(item));
        }

        if (col is DataGridTemplateColumn templateCol && templateCol.CellTemplate != null)
        {
            var content = templateCol.CellTemplate.LoadContent();
            if (content is TextBlock textBlock)
            {
                var textBinding = BindingOperations.GetBinding(textBlock, TextBlock.TextProperty);
                if (textBinding != null)
                {
                    var prop = item.GetType().GetProperty(textBinding.Path.Path);
                    return FormatForExport(prop?.GetValue(item));
                }
            }
        }

        return "";
    }

    public static void CopyCell(object sender)
    {
        var grid = FindParentDataGrid(sender);
        if (grid?.CurrentCell.Column == null || grid.CurrentItem == null) return;

        var value = GetCellValue(grid.CurrentCell.Column, grid.CurrentItem);
        if (value.Length > 0) Clipboard.SetDataObject(value, false);
    }

    public static void CopyRow(object sender)
    {
        var grid = FindParentDataGrid(sender);
        if (grid?.CurrentItem == null) return;

        var sb = new StringBuilder();
        foreach (var col in grid.Columns)
        {
            sb.Append(GetCellValue(col, grid.CurrentItem));
            sb.Append('\t');
        }
        Clipboard.SetDataObject(sb.ToString().TrimEnd('\t'), false);
    }

    public static void CopyAllRows(object sender)
    {
        var grid = FindParentDataGrid(sender);
        if (grid?.Items == null) return;

        var sb = new StringBuilder();

        foreach (var col in grid.Columns)
        {
            sb.Append(DataGridClipboardBehavior.GetHeaderText(col));
            sb.Append('\t');
        }
        sb.AppendLine();

        foreach (var item in grid.Items)
        {
            foreach (var col in grid.Columns)
            {
                sb.Append(GetCellValue(col, item));
                sb.Append('\t');
            }
            sb.AppendLine();
        }

        Clipboard.SetDataObject(sb.ToString(), false);
    }

    public static void ExportToCsv(object sender, string defaultFilePrefix)
    {
        var grid = FindParentDataGrid(sender);
        if (grid?.Items == null || grid.Items.Count == 0) return;

        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = $"{defaultFilePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dialog.ShowDialog() != true) return;

        var sb = new StringBuilder();
        var sep = App.CsvSeparator;

        var headers = new List<string>();
        foreach (var col in grid.Columns)
        {
            headers.Add(CsvEscape(DataGridClipboardBehavior.GetHeaderText(col), sep));
        }
        sb.AppendLine(string.Join(sep, headers));

        foreach (var item in grid.Items)
        {
            var values = new List<string>();
            foreach (var col in grid.Columns)
            {
                values.Add(CsvEscape(GetCellValue(col, item), sep));
            }
            sb.AppendLine(string.Join(sep, values));
        }

        try
        {
            File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to export: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FormatForExport(object? value)
    {
        if (value == null) return "";
        if (value is IFormattable formattable)
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        return value.ToString() ?? "";
    }

    private static string CsvEscape(string value, string separator)
    {
        if (value.Contains(separator, StringComparison.Ordinal) || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }
}

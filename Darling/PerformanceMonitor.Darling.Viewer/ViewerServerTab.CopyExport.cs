/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Blocking-tab grid copy/export + XML-save handlers (W1e), copied from Lite's
/// <c>ServerTab.CopyExport.cs</c> / <c>ServerTab.Plans.cs</c>. Copy/Export delegate to the shared
/// PerformanceMonitor.Ui <see cref="DataGridExport"/>; the XML-save buttons write the row's stored graph /
/// report XML to a file. These are the handlers the blocked-process and deadlock context menus + XML Save
/// columns bind to. (Lite's "Copy Repro Script" and the plan actions are not ported — the viewer has no
/// plan host yet; deferred.)
/// </summary>
public partial class ViewerServerTab
{
    /// <summary>Finds the parent DataGrid from a context menu opened on a DataGridRow.</summary>
    private static DataGrid? FindParentDataGrid(MenuItem menuItem)
    {
        var contextMenu = menuItem.Parent as ContextMenu;
        var target = contextMenu?.PlacementTarget as FrameworkElement;
        while (target != null && target is not DataGrid)
        {
            target = System.Windows.Media.VisualTreeHelper.GetParent(target) as FrameworkElement;
        }
        return target as DataGrid;
    }

    private void CopyCell_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyCell(sender);

    private void CopyRow_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyRow(sender);

    private void CopyAllRows_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyAllRows(sender);

    private void ExportToCsv_Click(object sender, RoutedEventArgs e) =>
        DataGridExport.ExportToCsv(sender, _server.DisplayName, ",");

    private void DownloadBlockedProcessXml_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ViewerBlockedProcessRow row || string.IsNullOrEmpty(row.BlockedProcessReportXml)) return;

        var dialog = new SaveFileDialog
        {
            Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
            DefaultExt = ".xml",
            FileName = $"blocked_process_{row.EventTime:yyyyMMdd_HHmmss}.xml"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, row.BlockedProcessReportXml, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save blocked process XML: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DownloadDeadlockXml_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not DeadlockProcessDetail row || string.IsNullOrEmpty(row.DeadlockGraphXml)) return;

        var dialog = new SaveFileDialog
        {
            Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
            DefaultExt = ".xml",
            FileName = $"deadlock_{row.DeadlockTime:yyyyMMdd_HHmmss}.xml"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, row.DeadlockGraphXml, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save deadlock XML: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

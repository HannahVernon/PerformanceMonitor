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
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PerformanceMonitor.PlanAnalysis;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The grid copy/export + Copy-Repro-Script + XML-save handlers (W1e / W1f), copied from Lite's
/// <c>ServerTab.CopyExport.cs</c> / <c>ServerTab.Plans.cs</c>. Copy/Export delegate to the shared
/// PerformanceMonitor.Ui <see cref="DataGridExport"/>; the XML-save buttons write the row's stored graph /
/// report XML to a file. <see cref="CopyReproScript_Click"/> builds a paste-ready T-SQL repro from the query
/// grids' STORED row fields (no live connection — Active Queries carries its plan in-row; Top Queries / Query
/// Store best-effort read the collector's STORED plan from Postgres to enrich parameters). Only Lite's
/// "Get Actual Plan" (a LIVE re-execution) stays omitted; the stored-plan "View Plan" host lives in
/// ViewerServerTab.Plans.cs.
/// </summary>
public partial class ViewerServerTab
{
    /* The product tag ReproScriptBuilder stamps in the repro-script header comment. */
    private const string ReproProductName = "SQL Server Performance Monitor";

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
        DataGridExport.ExportToCsv(sender, _server.DisplayName, ViewerExportSettings.CsvSeparator);

    /// <summary>
    /// "Copy Repro Script" for the query grids — builds a paste-ready T-SQL reproduction from the row's STORED
    /// fields and copies it to the clipboard. NO live SQL connection: Active Queries carries its query text /
    /// plan / isolation in-row; Top Queries / Query Store best-effort read their STORED plan from Postgres (a
    /// read via <see cref="ViewerDataService"/>, not a live re-exec) to enrich the extracted parameters, then
    /// fall back to a plan-less repro. (Lite fetches these plans live from the monitored server — the viewer
    /// reads the collector's stored copy, its only seam-difference; Lite's "Get Actual Plan" has no equivalent.)
    /// </summary>
    private async void CopyReproScript_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        var grid = FindParentDataGrid(menuItem);
        if (grid?.CurrentItem is not { } row) return;

        /* Best-effort STORED-plan enrichment for the aggregate grids (a Postgres read, never a live SQL
           connection). Active Queries needs none — its plan rides in-row. Failures fall through to a
           plan-less repro. */
        string? enrichedPlanXml = null;
        try
        {
            switch (row)
            {
                case ViewerQueryStatsRow stats when !string.IsNullOrEmpty(stats.QueryHash):
                    enrichedPlanXml = await _dataService.GetQueryStatsPlanXmlAsync(_server.ServerId, stats.DatabaseName, stats.QueryHash);
                    break;
                case ViewerQueryStoreRow qs:
                    enrichedPlanXml = await _dataService.GetQueryStorePlanTextAsync(_server.ServerId, qs.DatabaseName, qs.QueryId, qs.PlanId);
                    break;
            }
        }
        catch
        {
            /* Stored-plan enrichment is best-effort — build the repro from the row's other stored fields. */
        }

        var script = BuildReproScriptForRow(row, enrichedPlanXml, ReproProductName);
        if (string.IsNullOrEmpty(script)) return;

        /* SetDataObject(copy=false) avoids WPF's problematic Clipboard.Flush(); see dotnet/wpf#9901. */
        Clipboard.SetDataObject(script, false);
    }

    /// <summary>
    /// Maps a query-grid row to a repro script built from its STORED fields (pure, unit-tested). Active Queries
    /// uses its in-row plan + isolation (<paramref name="enrichedPlanXml"/> ignored); Top Queries / Query Store
    /// use the caller's best-effort stored <paramref name="enrichedPlanXml"/> (null → a plan-less repro, which
    /// ReproScriptBuilder still produces with a "plan not available" note). Returns null for a row with no query
    /// text (comparison aggregates / procedures) or an unsupported row type — the caller then no-ops, exactly
    /// like Lite's default branch. NO row type here reads a live connection.
    /// </summary>
    internal static string? BuildReproScriptForRow(object row, string? enrichedPlanXml, string productName)
    {
        switch (row)
        {
            case ViewerQuerySnapshotRow snapshot:
                if (string.IsNullOrEmpty(snapshot.QueryText)) return null;
                return ReproScriptBuilder.BuildReproScript(
                    snapshot.QueryText, snapshot.DatabaseName, snapshot.QueryPlan, snapshot.TransactionIsolationLevel,
                    "Active Queries", productName: productName);

            case ViewerQueryStatsRow stats:
                if (string.IsNullOrEmpty(stats.QueryText)) return null;
                return ReproScriptBuilder.BuildReproScript(
                    stats.QueryText, stats.DatabaseName, enrichedPlanXml, null,
                    "Top Queries (dm_exec_query_stats)", productName: productName);

            case ViewerQueryStoreRow qs:
                if (string.IsNullOrEmpty(qs.QueryText)) return null;
                return ReproScriptBuilder.BuildReproScript(
                    qs.QueryText, qs.DatabaseName, enrichedPlanXml, null,
                    "Query Store", productName: productName);

            /* Query Store Regressions carries the query-text sample but no plan (the regression aggregates
               OVER plans — plan_count deltas, not a single plan_id — so any one plan would mislead; the
               plan-change story is in the double-click history window). Build a plan-less repro from the
               sample, exactly what ReproScriptBuilder produces when the plan is unavailable. */
            case ViewerQueryStoreRegressionRow reg:
                if (string.IsNullOrEmpty(reg.QueryTextSample)) return null;
                return ReproScriptBuilder.BuildReproScript(
                    reg.QueryTextSample, reg.DatabaseName, null, null,
                    "Query Store Regressions", productName: productName);

            /* The unified Expensive Queries row carries its STORED plan in-row (ignore enrichedPlanXml, like
               the Active Queries snapshot case). Only the statement sources (Query Stats / Query Store) have
               runnable text; the procedure sources carry only the object name, so IsStatementSource is false
               and they fall to the default no-op — matching Lite, where procedure rows have no repro. */
            case ViewerExpensiveQueryRow eq when eq.IsStatementSource:
                if (string.IsNullOrEmpty(eq.QueryText)) return null;
                return ReproScriptBuilder.BuildReproScript(
                    eq.QueryText, eq.DatabaseName, eq.QueryPlanXml, null,
                    "Expensive Queries", productName: productName);

            default:
                return null;
        }
    }

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

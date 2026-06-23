/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PerformanceMonitor.Analysis;
using PerformanceMonitor.Ui;
using PerformanceMonitorLite.Analysis;
using PerformanceMonitorLite.Services;

namespace PerformanceMonitorLite.Controls;

public partial class ServerTab : UserControl
{
    // Right-click "View Block Chain" on a blocked-process-report row.
    private async void ViewBlockChain_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        var grid = FindParentDataGrid(menuItem);
        if (grid?.CurrentItem is BlockedProcessReportRow row)
            await OpenBlockChainForRowAsync(row);
    }

    // Double-click a blocked-process-report row.
    private async void BlockedProcessReportGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BlockedProcessReportGrid.SelectedItem is BlockedProcessReportRow row)
            await OpenBlockChainForRowAsync(row);
    }

    /// <summary>
    /// Opens the block-chain viewer scoped to ONE chain — the chain the clicked session belongs to, rooted
    /// at its lead blocker, with the clicked session highlighted. Reconstructs over the Blocking tab's
    /// current window. The slicer exposes UTC; the DuckDB rows filter on server-local event_time, so convert.
    /// Fetch + reconstruct + tree-build run off the UI thread; only the render touches the UI.
    /// </summary>
    private async Task OpenBlockChainForRowAsync(BlockedProcessReportRow row)
    {
        var spid = row.BlockedSpid;
        if (spid <= 0) return;
        var rawTran = row.BlockedLastTranStarted;

        try
        {
            DateTime start, end;
            var startUtc = BlockingSlicer.SelectionStartUtc;
            var endUtc = BlockingSlicer.SelectionEndUtc;
            if (startUtc.HasValue && endUtc.HasValue)
            {
                start = ServerTimeHelper.ToServerTime(startUtc.Value);
                end = ServerTimeHelper.ToServerTime(endUtc.Value);
            }
            else
            {
                (start, end) = GetBlockingServerRange();
            }

            // GetBlockingPairRowsAsync uses OpenConnectionAsync, which already takes the DuckDB read lock —
            // running it inside Task.Run keeps the lock acquire/release on a background thread (never the UI).
            var model = await Task.Run(async () =>
            {
                var rows = await _dataService.GetBlockingPairRowsAsync(_serverId, start, end);
                var reconstruction = BlockingChainReconstructor.Reconstruct(
                    rows, maxDepth: 50, maxPairs: 5000, stepBudget: 100_000);
                return BlockingChainViewerProjection.BuildModelForSession(reconstruction, spid, rawTran);
            });

            if (model == null)
            {
                MessageBox.Show(
                    Window.GetWindow(this)!,
                    $"No reconstructable blocking chain for SPID {spid} in the selected range.\n\n" +
                    "The session may not have been part of a blocked-process report whose wait crossed " +
                    "the blocked-process threshold in this window.",
                    "No Block Chain",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // Normalize the clicked tran the same way the reconstructor keyed the nodes, so the control can
            // match + highlight the clicked session.
            var key = BlockingChainReconstructor.MakeKey(spid, rawTran);

            var control = new BlockingChainControl();
            control.LoadModel(model, key.Spid, key.TranStarted, BlockingChainViewerProjection.EmptyStateDetail);
            GraphViewerWindow.ShowGraph(
                Window.GetWindow(this),
                control,
                $"Block Chain — SPID {spid} on {_server.DisplayName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this)!,
                $"Failed to build the blocking-chain view:\n\n{ex.Message}",
                "Block Chain Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// The current Blocking-tab server-local range, used when the slicer has no narrowed selection /
    /// no data. Mirrors LoadBlockingSlicerAsync's range computation.
    /// </summary>
    private (DateTime start, DateTime end) GetBlockingServerRange()
    {
        var hoursBack = GetHoursBack();
        DateTime? fromDate = null, toDate = null;
        if (IsCustomRange)
        {
            var fromLocal = GetDateTimeFromPickers(FromDatePicker!, FromHourCombo, FromMinuteCombo);
            var toLocal = GetDateTimeFromPickers(ToDatePicker!, ToHourCombo, ToMinuteCombo);
            if (fromLocal.HasValue && toLocal.HasValue)
            {
                fromDate = ServerTimeHelper.DisplayTimeToServerTime(fromLocal.Value, ServerTimeHelper.CurrentDisplayMode);
                toDate = ServerTimeHelper.DisplayTimeToServerTime(toLocal.Value, ServerTimeHelper.CurrentDisplayMode);
            }
        }
        return GetSlicerTimeRange(hoursBack, fromDate, toDate);
    }
}

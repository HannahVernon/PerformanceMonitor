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
using PerformanceMonitor.Analysis;
using PerformanceMonitor.Ui;
using PerformanceMonitorLite.Analysis;
using PerformanceMonitorLite.Services;

namespace PerformanceMonitorLite.Controls;

public partial class ServerTab : UserControl
{
    /// <summary>
    /// Opens the block-chain viewer for the Blocking tab's current window. The slicer exposes UTC
    /// (SelectionStartUtc/EndUtc); the DuckDB rows are filtered on server-local event_time, so convert.
    /// Fetch + reconstruct + tree-build run off the UI thread; only the render touches the UI.
    /// </summary>
    private async void ViewBlockChain_Click(object sender, RoutedEventArgs e)
    {
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
                return BlockingChainViewerProjection.BuildModel(reconstruction);
            });

            var control = new BlockingChainControl();
            control.LoadModel(model, BlockingChainViewerProjection.EmptyStateDetail);
            GraphViewerWindow.ShowGraph(
                Window.GetWindow(this),
                control,
                $"Blocking Chains — {_server.DisplayName}");
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

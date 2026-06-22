/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PerformanceMonitor.Analysis;
using PerformanceMonitor.Ui;
using PerformanceMonitorDashboard.Analysis;

namespace PerformanceMonitorDashboard
{
    public partial class ServerTab : UserControl
    {
        /// <summary>
        /// Opens the block-chain viewer for the Locking tab's current blocking window. Reads the slicer's
        /// narrowed selection (server-local) when set, else the sub-tab's range. Fetch + reconstruct +
        /// tree-build run off the UI thread; only the render touches the UI.
        /// </summary>
        private async void ViewBlockChain_Click(object sender, RoutedEventArgs e)
        {
            if (_databaseService == null) return;

            try
            {
                DateTime start, end;
                if (BlockingSlicer.HasNarrowedSelection
                    && BlockingSlicer.SelectionStart.HasValue
                    && BlockingSlicer.SelectionEnd.HasValue)
                {
                    start = BlockingSlicer.SelectionStart.Value;
                    end = BlockingSlicer.SelectionEnd.Value;
                }
                else
                {
                    (start, end) = GetLockingSlicerTimeRange(_blockingHoursBack, _blockingFromDate, _blockingToDate);
                }

                // Fetch (async DB) + reconstruct (CPU-bound over up to 5000 rows) + tree-build, all off the
                // UI thread. The model that comes back is pure data; the control renders it on the UI thread.
                var model = await Task.Run(async () =>
                {
                    var rows = await _databaseService.GetBlockingPairRowsAsync(start, end);
                    var reconstruction = BlockingChainReconstructor.Reconstruct(
                        rows, maxDepth: 50, maxPairs: 5000, stepBudget: 100_000);
                    return BlockingChainViewerProjection.BuildModel(reconstruction);
                });

                var control = new BlockingChainControl();
                control.LoadModel(model, BlockingChainViewerProjection.EmptyStateDetail);
                GraphViewerWindow.ShowGraph(
                    Window.GetWindow(this),
                    control,
                    $"Blocking Chains — {_serverConnection.DisplayName}");
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
    }
}

/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer
{
    /// <summary>
    /// The per-collector collection-history drill window (W1i), copied from Lite's
    /// <c>Windows/CollectionLogWindow.xaml.cs</c>. The Collection Health "Health Summary" grid opens it
    /// on double-click. Purely data-driven; the one change from Lite is the data call — repointed to
    /// <see cref="ViewerDataService.GetCollectionLogByCollectorAsync"/> (Postgres). Copy/Export use the
    /// shared PerformanceMonitor.Ui <see cref="DataGridExport"/> with the viewer's comma separator.
    /// </summary>
    public partial class CollectionLogWindow : Window
    {
        private readonly string _collectorName;
        private readonly ViewerDataService _dataService;
        private readonly int _serverId;

        public CollectionLogWindow(ViewerDataService dataService, int serverId, string collectorName)
        {
            InitializeComponent();

            _dataService = dataService;
            _serverId = serverId;
            _collectorName = collectorName;

            CollectorNameText.Text = $"Collection History: {collectorName}";

            Loaded += CollectionLogWindow_Loaded;
        }

        private async void CollectionLogWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadCollectionLogAsync();
        }

        private async Task LoadCollectionLogAsync()
        {
            try
            {
                var logs = await _dataService.GetCollectionLogByCollectorAsync(_serverId, _collectorName);
                LogDataGrid.ItemsSource = logs;

                if (logs.Count > 0)
                {
                    var successCount = logs.Count(l => l.Status == "SUCCESS");
                    var errorCount = logs.Count(l => l.Status == "ERROR");
                    var avgDuration = logs.Where(l => l.Status == "SUCCESS" && l.DurationMs.HasValue)
                                           .Select(l => (double)l.DurationMs!.Value)
                                           .DefaultIfEmpty(0)
                                           .Average();

                    SummaryText.Text = $"Total Runs: {logs.Count} | Success: {successCount} | Errors: {errorCount} | Avg Duration: {avgDuration:F0} ms";
                }
                else
                {
                    SummaryText.Text = "No collection history found for this collector.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load collection history:\n\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private void CopyCell_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyCell(sender);
        private void CopyRow_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyRow(sender);
        private void CopyAllRows_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyAllRows(sender);
        private void ExportToCsv_Click(object sender, RoutedEventArgs e) => DataGridExport.ExportToCsv(sender, "collection_log", ",");

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

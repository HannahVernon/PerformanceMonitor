/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Daily Summary inner tab — a COPY of Lite's Daily Summary tab (ServerTab.xaml + the
/// <c>DailySummaryToday_Click</c> / <c>DailySummaryDate_Changed</c> / <c>DailySummaryRefresh_Click</c>
/// handlers and the <c>RefreshDailySummaryAsync</c> load), reads rewired to Postgres. The date picker
/// drives a single-row daily roll-up over the selected UTC day (default today). Matching Lite exactly,
/// changing the date only updates the indicator — the load fires on the Today button or Refresh. The
/// tab-refresh loop (MainWindow's 60-second timer, via <see cref="LoadDailySummaryAsync"/>) reloads
/// whatever date is selected.
/// </summary>
public partial class ViewerServerTab
{
    /// <summary>The selected summary day (null = today, UTC). Mirrors Lite's <c>_dailySummaryDate</c>.</summary>
    private DateTime? _dailySummaryDate;

    private async void DailySummaryToday_Click(object sender, RoutedEventArgs e)
    {
        _dailySummaryDate = null;
        DailySummaryDatePicker.SelectedDate = null;
        DailySummaryTodayButton.FontWeight = FontWeights.Bold;
        DailySummaryIndicator.Text = "Showing: Today (UTC)";
        await ReloadDailySummaryAsync();
    }

    private void DailySummaryDate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DailySummaryDatePicker.SelectedDate.HasValue)
        {
            _dailySummaryDate = DailySummaryDatePicker.SelectedDate.Value.Date;
            DailySummaryTodayButton.FontWeight = FontWeights.Normal;
            DailySummaryIndicator.Text = $"Showing: {_dailySummaryDate.Value:MMM d, yyyy}";
        }
    }

    private async void DailySummaryRefresh_Click(object sender, RoutedEventArgs e)
    {
        await ReloadDailySummaryAsync();
    }

    /// <summary>
    /// Button-path reload with its own error surfacing. The timer / tab-switch path calls
    /// <see cref="LoadDailySummaryAsync"/> through LoadInnerTabAsync, which owns that path's try/catch.
    /// </summary>
    private async Task ReloadDailySummaryAsync()
    {
        try
        {
            await LoadDailySummaryAsync();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Daily Summary refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Daily Summary load: the single-row aggregate for the selected day. Copied from Lite's
    /// <c>RefreshDailySummaryAsync</c> — the read is genuinely async (Npgsql), so no Task.Run wrap.
    /// A null result shows the no-data message; in practice the aggregate always returns a (possibly
    /// all-zero) row, so the message is the same parity-only fallback Lite carries.
    /// </summary>
    private async Task LoadDailySummaryAsync()
    {
        var result = await _dataService.GetDailySummaryAsync(_server.ServerId, _dailySummaryDate);
        DailySummaryGrid.ItemsSource = result != null
            ? new List<DailySummaryRow> { result }
            : null;
        DailySummaryNoData.Visibility = result == null ? Visibility.Visible : Visibility.Collapsed;
    }
}

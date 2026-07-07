/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Daily Summary inner tab — a COPY of Lite's Performance Calendar wiring, reads rewired to Postgres.
/// A month heatmap of composite daily health (the shared <see cref="PerformanceCalendar"/>); clicking a day
/// drills into its single-day roll-up. The tab-refresh loop (auto-refresh timer / tab switch, via
/// <see cref="LoadDailySummaryAsync"/>) reloads the currently displayed month.
/// </summary>
public partial class ViewerServerTab
{
    /// <summary>The currently loaded month's rows, keyed by date, for O(1) day-click drill-in.</summary>
    private Dictionary<DateTime, DailySummaryRow> _calendarRowsByDate = new();

    /// <summary>
    /// Entry point from LoadInnerTabAsync (index 14) and the auto-refresh loop: (re)load the calendar's
    /// currently displayed month. LoadInnerTabAsync owns this path's try/catch.
    /// </summary>
    private async Task LoadDailySummaryAsync()
    {
        await LoadCalendarMonthAsync(DailyCalendar.DisplayMonth);
    }

    /// <summary>
    /// Loads a month's daily summaries into the Performance Calendar. One banded cell per collected day;
    /// days with no collection are absent (the calendar renders them No-Data grey).
    /// </summary>
    private async Task LoadCalendarMonthAsync(DateTime month)
    {
        var start = new DateTime(month.Year, month.Month, 1);
        var end = start.AddMonths(1);

        var rows = await _dataService.GetDailySummaryRangeAsync(_server.ServerId, start, end);

        _calendarRowsByDate = rows.ToDictionary(r => r.SummaryDate.Date);
        DailyCalendar.Days = rows.Select(r => new PerformanceCalendarDay
        {
            Date = r.SummaryDate.Date,
            Band = r.HealthBand,
            Tooltip = $"{r.SummaryDate:MMM d, yyyy} - {r.OverallHealth}{Environment.NewLine}{r.SignalsTooltip}",
        }).ToList();

        // Preserve the drilled-in day if it's still in view; otherwise clear the detail pane.
        if (DailyCalendar.SelectedDate is DateTime sel && sel.Year == start.Year && sel.Month == start.Month)
            ShowDayDetail(sel);
        else
            HideDayDetail();
    }

    private async void DailyCalendar_MonthChanged(object? sender, PerformanceCalendarMonthEventArgs e)
    {
        try
        {
            await LoadCalendarMonthAsync(e.Month);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Daily Summary load failed: {ex.Message}");
        }
    }

    private void DailyCalendar_DayClicked(object? sender, PerformanceCalendarDayEventArgs e)
        => ShowDayDetail(e.Date);

    private async void DailySummaryRefresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadCalendarMonthAsync(DailyCalendar.DisplayMonth);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Daily Summary refresh failed: {ex.Message}");
        }
    }

    /// <summary>Shows the single-day roll-up for the clicked date (reusing the existing detail grid).</summary>
    private void ShowDayDetail(DateTime date)
    {
        DailyCalendar.SelectedDate = date.Date;

        if (_calendarRowsByDate.TryGetValue(date.Date, out var row))
        {
            DailySummaryGrid.ItemsSource = new List<DailySummaryRow> { row };
            DailySummaryDetailHeader.Text = $"Detail for {date:dddd, MMMM d, yyyy} (UTC)";
            DailySummaryDetailPanel.Visibility = Visibility.Visible;
            DailySummaryNoData.Visibility = Visibility.Collapsed;
        }
        else
        {
            DailySummaryGrid.ItemsSource = null;
            DailySummaryDetailPanel.Visibility = Visibility.Collapsed;
            DailySummaryNoData.Text = $"No data collected for {date:MMMM d, yyyy}.";
            DailySummaryNoData.Visibility = Visibility.Visible;
        }
    }

    private void HideDayDetail()
    {
        DailySummaryDetailPanel.Visibility = Visibility.Collapsed;
        DailySummaryNoData.Visibility = Visibility.Collapsed;
        DailySummaryGrid.ItemsSource = null;
    }
}

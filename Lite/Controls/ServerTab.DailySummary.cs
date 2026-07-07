/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PerformanceMonitorLite.Helpers;
using PerformanceMonitorLite.Services;
using PerformanceMonitor.Ui;

namespace PerformanceMonitorLite.Controls;

public partial class ServerTab : UserControl
{
    /// <summary>The currently loaded month's rows, keyed by date, for O(1) day-click drill-in.</summary>
    private Dictionary<DateTime, DailySummaryRow> _calendarRowsByDate = new();

    /// <summary>
    /// Loads a month's daily summaries into the Performance Calendar. One banded cell per collected day;
    /// days with no collection are simply absent (the calendar renders them No-Data grey). The heavy DuckDB
    /// read runs off the UI thread.
    /// </summary>
    private async Task LoadCalendarMonthAsync(DateTime month)
    {
        try
        {
            var start = new DateTime(month.Year, month.Month, 1);
            var end = start.AddMonths(1);

            var rows = await Task.Run(() => _dataService.GetDailySummaryRangeAsync(_serverId, start, end));

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
        catch (Exception ex)
        {
            AppLogger.Info("ServerTab", $"[{_server.DisplayName}] LoadCalendarMonthAsync failed: {ex.Message}");
        }
    }

    private async void DailyCalendar_MonthChanged(object? sender, PerformanceCalendarMonthEventArgs e)
        => await LoadCalendarMonthAsync(e.Month);

    private void DailyCalendar_DayClicked(object? sender, PerformanceCalendarDayEventArgs e)
        => ShowDayDetail(e.Date);

    private async void DailySummaryRefresh_Click(object sender, RoutedEventArgs e)
        => await LoadCalendarMonthAsync(DailyCalendar.DisplayMonth);

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

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
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The per-server toolbar's time-window + auto-refresh state (the header row added above the inner tab
/// strip), mirroring Lite's <c>ServerTab.TimeRange.cs</c> but adapted to the viewer: there is no
/// <c>ServerTimeHelper</c> / per-server UTC offset, so the custom-range pickers are read in the viewer's
/// one machine-LOCAL display convention (<see cref="ViewerDataService.ToLocalTime"/>) and inverted back
/// to the store's naive-UTC bounds here. This replaces the old hardcoded 24-hour <c>s_dataWindow</c>: every
/// inner-tab load now reads <see cref="GetWindowUtc"/> (preset 1h/4h/12h/24h/7d or a custom From/To), and
/// the auto-refresh cadence comes from the toolbar instead of MainWindow's fixed 60-second timer.
/// </summary>
public partial class ViewerServerTab
{
    /// <summary>The "Custom Range" slot in <c>TimeRangeCombo</c> (after 1h/4h/12h/24h/7d).</summary>
    private const int CustomRangeIndex = 5;

    /// <summary>Guards the range handlers while pickers/combo are set programmatically (Apply-to-all,
    /// custom-range defaults) so one external change drives exactly one reload, not a cascade.</summary>
    private bool _suppressRangeEvents;

    private DispatcherTimer? _autoRefreshTimer;

    /// <summary>
    /// Raised when the user clicks "Apply to All": MainWindow broadcasts the selected range (index plus,
    /// for a custom range, the From/To in machine-local display time) to every other open server tab. The
    /// source tab is carried so the broadcast can skip it (it already holds the range).
    /// </summary>
    public event Action<ViewerServerTab, int, DateTime?, DateTime?>? ApplyTimeRangeRequested;

    /// <summary>Preset combo index → hours back. Index 5 (custom) and any stray value fall to the
    /// viewer's historical 24-hour default. Pure + static so the mapping is unit-testable.</summary>
    internal static int TimeRangeIndexToHours(int index) => index switch
    {
        0 => 1,
        1 => 4,
        2 => 12,
        3 => 24,
        4 => 168,
        _ => 24,
    };

    /// <summary>Populates the custom-range hour (00:00–23:00) and minute (:00/:15/:30/:45) combos,
    /// matching Lite's <c>InitializeTimeComboBoxes</c>. Called from the constructor after
    /// <c>InitializeComponent</c>.</summary>
    private void InitializeTimeComboBoxes()
    {
        var hours = new List<string>();
        for (int h = 0; h < 24; h++)
        {
            hours.Add(DateTime.Today.AddHours(h).ToString("HH:00"));
        }

        FromHourCombo.ItemsSource = hours;
        ToHourCombo.ItemsSource = hours;
        FromHourCombo.SelectedIndex = 0;   // 00:00
        ToHourCombo.SelectedIndex = 23;    // 23:00

        var minutes = new List<string> { ":00", ":15", ":30", ":45" };
        FromMinuteCombo.ItemsSource = minutes;
        ToMinuteCombo.ItemsSource = minutes;
        FromMinuteCombo.SelectedIndex = 0; // :00
        ToMinuteCombo.SelectedIndex = 3;   // :45
    }

    /// <summary>Wires the auto-refresh timer (default 1 minute, matching the old fixed 60-second cadence)
    /// and starts it when the toolbar's checkbox is checked. Called from the constructor.</summary>
    private void InitializeAutoRefreshTimer()
    {
        _autoRefreshTimer = new DispatcherTimer();
        UpdateAutoRefreshInterval();
        _autoRefreshTimer.Tick += OnAutoRefreshTick;
        if (AutoRefreshCheckBox.IsChecked == true)
        {
            _autoRefreshTimer.Start();
        }
    }

    private async void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        /* Only the VISIBLE server tab auto-refreshes (the viewer's visible-only rule); a background tab's
           timer no-ops until the user switches to it, at which point MainWindow reloads it on activation. */
        if (!IsVisible)
        {
            return;
        }

        await RefreshActiveInnerTabAsync();
    }

    // ── Window computation ───────────────────────────────────────────────────────────

    /// <summary>True when the user picked "Custom Range" AND both date pickers hold a value.</summary>
    private bool IsCustomRange => TimeRangeCombo.SelectedIndex == CustomRangeIndex
        && FromDatePicker?.SelectedDate != null
        && ToDatePicker?.SelectedDate != null;

    /// <summary>
    /// The current window as store-native naive-UTC bounds, driving every inner-tab load (it replaces the
    /// old fixed 24-hour <c>s_dataWindow</c>). A preset ends "now"; a valid custom range uses its From/To.
    /// </summary>
    private (DateTime startUtc, DateTime endUtc) GetWindowUtc()
    {
        if (IsCustomRange)
        {
            var (fromUtc, toUtc) = GetCustomRangeUtc();
            if (fromUtc.HasValue && toUtc.HasValue)
            {
                return (fromUtc.Value, toUtc.Value);
            }
        }

        var endUtc = DateTime.UtcNow;
        return (endUtc.AddHours(-TimeRangeIndexToHours(TimeRangeCombo.SelectedIndex)), endUtc);
    }

    /// <summary>Hours-back for the reads that take an integer window (the Overview lanes + Collection
    /// Health log). A custom range collapses to its rounded-up span so those surfaces still track it.</summary>
    private int GetWindowHoursBack()
    {
        if (IsCustomRange)
        {
            var (fromUtc, toUtc) = GetCustomRangeUtc();
            if (fromUtc.HasValue && toUtc.HasValue)
            {
                var hours = (int)Math.Ceiling((toUtc.Value - fromUtc.Value).TotalHours);
                return hours < 1 ? 1 : hours;
            }
        }

        return TimeRangeIndexToHours(TimeRangeCombo.SelectedIndex);
    }

    /// <summary>Custom From/To as naive-UTC bounds for the Overview lanes (null,null when not a valid
    /// custom range — the lanes then fall back to their hours-back window).</summary>
    private (DateTime? fromUtc, DateTime? toUtc) GetOverviewCustomRange()
        => IsCustomRange ? GetCustomRangeUtc() : (null, null);

    /// <summary>Reads the custom From/To pickers as naive-UTC bounds, or (null,null) when either is unset.</summary>
    private (DateTime? fromUtc, DateTime? toUtc) GetCustomRangeUtc()
    {
        var fromLocal = GetDateTimeFromPickers(FromDatePicker!, FromHourCombo, FromMinuteCombo);
        var toLocal = GetDateTimeFromPickers(ToDatePicker!, ToHourCombo, ToMinuteCombo);
        if (!fromLocal.HasValue || !toLocal.HasValue)
        {
            return (null, null);
        }

        return (LocalDisplayToNaiveUtc(fromLocal.Value), LocalDisplayToNaiveUtc(toLocal.Value));
    }

    /// <summary>
    /// Inverts <see cref="ViewerDataService.ToLocalTime"/>: the pickers show machine-local wall-clock time
    /// (the viewer's one display convention), and the store windows on naive UTC, so a picked local time
    /// converts to UTC and drops its kind for the reads (which re-stamp it Unspecified).
    /// </summary>
    private static DateTime LocalDisplayToNaiveUtc(DateTime localDisplay)
    {
        var utc = DateTime.SpecifyKind(localDisplay, DateTimeKind.Local).ToUniversalTime();
        return DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
    }

    private static DateTime? GetDateTimeFromPickers(DatePicker datePicker, ComboBox hourCombo, ComboBox minuteCombo)
    {
        if (datePicker?.SelectedDate is not { } date)
        {
            return null;
        }

        int hour = hourCombo.SelectedIndex >= 0 ? hourCombo.SelectedIndex : 0;
        int minute = minuteCombo.SelectedIndex >= 0 ? minuteCombo.SelectedIndex * 15 : 0;
        return date.Date.AddHours(hour).AddMinutes(minute);
    }

    // ── Toolbar handlers ─────────────────────────────────────────────────────────────

    private async void TimeRangeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressRangeEvents)
        {
            return;
        }

        var isCustom = TimeRangeCombo.SelectedIndex == CustomRangeIndex;
        var visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

        if (FromDatePicker != null)
        {
            FromDatePicker.Visibility = visibility;
            FromHourCombo.Visibility = visibility;
            FromMinuteCombo.Visibility = visibility;
            ToLabel.Visibility = visibility;
            ToDatePicker.Visibility = visibility;
            ToHourCombo.Visibility = visibility;
            ToMinuteCombo.Visibility = visibility;

            if (isCustom && FromDatePicker.SelectedDate == null)
            {
                /* Seed a sensible default so switching to Custom shows a real window; the picker changes
                   below drive the reload. Suppress so the two picker writes coalesce into one load. */
                _suppressRangeEvents = true;
                FromDatePicker.SelectedDate = DateTime.Today.AddDays(-1);
                ToDatePicker.SelectedDate = DateTime.Today;
                _suppressRangeEvents = false;
            }
        }

        /* Presets reload here; a custom range reloads off the picker changes (below), except the seeded
           default above which is suppressed — so drive that one reload explicitly. */
        if (!isCustom || FromDatePicker?.SelectedDate != null)
        {
            await RefreshActiveInnerTabAsync();
        }
    }

    private async void CustomDateRange_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressRangeEvents)
        {
            return;
        }

        if (FromDatePicker?.SelectedDate != null && ToDatePicker?.SelectedDate != null)
        {
            await RefreshActiveInnerTabAsync();
        }
    }

    private async void CustomTimeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressRangeEvents)
        {
            return;
        }

        if (FromDatePicker?.SelectedDate != null && ToDatePicker?.SelectedDate != null)
        {
            await RefreshActiveInnerTabAsync();
        }
    }

    private async void RefreshDataButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshDataButton.IsEnabled = false;
        try
        {
            await RefreshActiveInnerTabAsync();
        }
        finally
        {
            RefreshDataButton.IsEnabled = true;
        }
    }

    private void AutoRefreshCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_autoRefreshTimer == null)
        {
            return;
        }

        if (AutoRefreshCheckBox.IsChecked == true)
        {
            UpdateAutoRefreshInterval();
            _autoRefreshTimer.Start();
        }
        else
        {
            _autoRefreshTimer.Stop();
        }
    }

    private void AutoRefreshInterval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_autoRefreshTimer == null)
        {
            return;
        }

        UpdateAutoRefreshInterval();
    }

    private void UpdateAutoRefreshInterval()
    {
        if (_autoRefreshTimer == null || AutoRefreshIntervalCombo == null)
        {
            return;
        }

        _autoRefreshTimer.Interval = AutoRefreshIntervalCombo.SelectedIndex switch
        {
            0 => TimeSpan.FromSeconds(30),
            1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(5),
            _ => TimeSpan.FromMinutes(1),
        };
    }

    private void ApplyTimeRangeToAll_Click(object sender, RoutedEventArgs e)
    {
        DateTime? fromLocal = null;
        DateTime? toLocal = null;
        if (TimeRangeCombo.SelectedIndex == CustomRangeIndex)
        {
            fromLocal = GetDateTimeFromPickers(FromDatePicker!, FromHourCombo, FromMinuteCombo);
            toLocal = GetDateTimeFromPickers(ToDatePicker!, ToHourCombo, ToMinuteCombo);
        }

        ApplyTimeRangeRequested?.Invoke(this, TimeRangeCombo.SelectedIndex, fromLocal, toLocal);
    }

    /// <summary>
    /// Applies a range chosen on another server tab (the "Apply to All" broadcast). Sets the pickers and
    /// combo under the suppress guard so the copy doesn't cascade multiple reloads, then drives exactly
    /// one reload of this tab's active inner tab.
    /// </summary>
    public void ApplyExternalTimeRange(int index, DateTime? customFromLocal, DateTime? customToLocal)
    {
        _suppressRangeEvents = true;
        try
        {
            var isCustom = index == CustomRangeIndex && customFromLocal.HasValue && customToLocal.HasValue;
            var visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

            if (FromDatePicker != null)
            {
                FromDatePicker.Visibility = visibility;
                FromHourCombo.Visibility = visibility;
                FromMinuteCombo.Visibility = visibility;
                ToLabel.Visibility = visibility;
                ToDatePicker.Visibility = visibility;
                ToHourCombo.Visibility = visibility;
                ToMinuteCombo.Visibility = visibility;
            }

            if (isCustom)
            {
                FromDatePicker!.SelectedDate = customFromLocal!.Value.Date;
                FromHourCombo.SelectedIndex = customFromLocal.Value.Hour;
                FromMinuteCombo.SelectedIndex = customFromLocal.Value.Minute / 15;
                ToDatePicker!.SelectedDate = customToLocal!.Value.Date;
                ToHourCombo.SelectedIndex = customToLocal.Value.Hour;
                ToMinuteCombo.SelectedIndex = customToLocal.Value.Minute / 15;
            }

            TimeRangeCombo.SelectedIndex = index;
        }
        finally
        {
            _suppressRangeEvents = false;
        }

        _ = RefreshActiveInnerTabAsync();
    }

    // ── Custom-range DatePicker calendar theming (viewer is fixed Dark) ───────────────

    private void DatePicker_CalendarOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not DatePicker datePicker)
        {
            return;
        }

        /* Defer so the popup's visual tree exists before we re-color it. */
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (datePicker.Template.FindName("PART_Popup", datePicker) is Popup { Child: Calendar calendar })
            {
                ApplyDarkThemeToCalendar(calendar);
            }
        }));
    }

    private static void ApplyDarkThemeToCalendar(Calendar calendar)
    {
        var primaryBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111217")!);
        var fg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E4E6EB")!);
        var borderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2a2d35")!);

        calendar.Background = primaryBg;
        calendar.Foreground = fg;
        calendar.BorderBrush = borderBrush;
        ApplyDarkThemeRecursively(calendar, primaryBg, fg);
    }

    private static void ApplyDarkThemeRecursively(DependencyObject parent, Brush primaryBg, Brush fg)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            switch (child)
            {
                case CalendarItem calendarItem:
                    calendarItem.Background = primaryBg;
                    calendarItem.Foreground = fg;
                    break;
                case CalendarDayButton dayButton:
                    dayButton.Background = Brushes.Transparent;
                    dayButton.Foreground = fg;
                    break;
                case CalendarButton calButton:
                    calButton.Background = Brushes.Transparent;
                    calButton.Foreground = fg;
                    break;
                case Button button:
                    button.Background = Brushes.Transparent;
                    button.Foreground = fg;
                    break;
                case TextBlock textBlock:
                    textBlock.Foreground = fg;
                    break;
                case Border { Background: SolidColorBrush bg } border when bg.Color is { R: > 200, G: > 200, B: > 200 }:
                    border.Background = primaryBg;
                    break;
                case Grid { Background: SolidColorBrush gridBg } grid when gridBg.Color is { R: > 200, G: > 200, B: > 200 }:
                    grid.Background = primaryBg;
                    break;
            }

            ApplyDarkThemeRecursively(child, primaryBg, fg);
        }
    }
}

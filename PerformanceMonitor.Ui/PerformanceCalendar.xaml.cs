/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Ui
{
    /// <summary>
    /// A navigable month heatmap of composite daily health bands, shared by Lite and the Darling viewer so
    /// the Performance Calendar can't drift between them. The host supplies <see cref="Days"/> (one banded
    /// <see cref="PerformanceCalendarDay"/> per collected day) and the displayed month; the control lays out
    /// a 6x7 grid, washes each in-month cell with its band color, and raises <see cref="MonthChanged"/> when
    /// the user navigates (so the host can fetch that month) and <see cref="DayClicked"/> when a day is
    /// clicked (so the host can drill into that day's detail).
    /// </summary>
    public partial class PerformanceCalendar : UserControl
    {
        private const int WeeksShown = 6;
        private const int DaysPerWeek = 7;

        public PerformanceCalendar()
        {
            InitializeComponent();
            BuildWeekdayHeaders();
            RebuildCells();
        }

        /// <summary>The banded days to render (one per collected day in the displayed month).</summary>
        public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
            nameof(Days), typeof(IEnumerable<PerformanceCalendarDay>), typeof(PerformanceCalendar),
            new PropertyMetadata(null, OnVisualInputChanged));
        public IEnumerable<PerformanceCalendarDay>? Days
        {
            get => (IEnumerable<PerformanceCalendarDay>?)GetValue(DaysProperty);
            set => SetValue(DaysProperty, value);
        }

        /// <summary>The month to display (any day within it; normalized to the first). Defaults to the current
        /// UTC month — the calendar operates in UTC day-space to match the collected data (naive-UTC collection times).</summary>
        public static readonly DependencyProperty DisplayMonthProperty = DependencyProperty.Register(
            nameof(DisplayMonth), typeof(DateTime), typeof(PerformanceCalendar),
            new PropertyMetadata(FirstOfMonth(DateTime.UtcNow), OnVisualInputChanged));
        public DateTime DisplayMonth
        {
            get => (DateTime)GetValue(DisplayMonthProperty);
            set => SetValue(DisplayMonthProperty, value);
        }

        /// <summary>The currently selected/drilled day, framed with an accent ring. Null when none.</summary>
        public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(
            nameof(SelectedDate), typeof(DateTime?), typeof(PerformanceCalendar),
            new PropertyMetadata(null, OnVisualInputChanged));
        public DateTime? SelectedDate
        {
            get => (DateTime?)GetValue(SelectedDateProperty);
            set => SetValue(SelectedDateProperty, value);
        }

        /// <summary>Raised when a user clicks an in-month day cell.</summary>
        public event EventHandler<PerformanceCalendarDayEventArgs>? DayClicked;

        /// <summary>Raised when the user navigates to a different month (prev / next / Today), carrying the new month's first day.</summary>
        public event EventHandler<PerformanceCalendarMonthEventArgs>? MonthChanged;

        /// <summary>The weekday column headers in the current culture's week order.</summary>
        public ObservableCollection<string> WeekdayHeaders { get; } = new();

        /// <summary>The 42 rendered day cells (6 weeks x 7 days).</summary>
        public ObservableCollection<PerformanceCalendarCell> Cells { get; } = new();

        private static DateTime FirstOfMonth(DateTime value) => new(value.Year, value.Month, 1);

        private static void OnVisualInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((PerformanceCalendar)d).RebuildCells();
        }

        private void BuildWeekdayHeaders()
        {
            WeekdayHeaders.Clear();
            var culture = CultureInfo.CurrentCulture;
            var firstDay = (int)culture.DateTimeFormat.FirstDayOfWeek;
            var names = culture.DateTimeFormat.AbbreviatedDayNames; // index 0 = Sunday
            for (var i = 0; i < DaysPerWeek; i++)
            {
                WeekdayHeaders.Add(names[(firstDay + i) % DaysPerWeek]);
            }
        }

        private void RebuildCells()
        {
            // MonthLabel/Cells may be referenced before the template is applied during construction.
            var month = FirstOfMonth(DisplayMonth);
            if (MonthLabel != null)
            {
                MonthLabel.Text = month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
            }

            // Index the supplied days by date for O(1) lookup.
            var byDate = new Dictionary<DateTime, PerformanceCalendarDay>();
            if (Days != null)
            {
                foreach (var day in Days)
                {
                    byDate[day.Date.Date] = day;
                }
            }

            var today = DateTime.UtcNow.Date; // calendar days are UTC-bucketed; highlight the UTC "today"
            var selected = SelectedDate?.Date;

            // The grid starts on the first day-of-week on or before the 1st of the month.
            var firstDayOfWeek = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            var offset = ((int)month.DayOfWeek - firstDayOfWeek + DaysPerWeek) % DaysPerWeek;
            var gridStart = month.AddDays(-offset);

            Cells.Clear();
            for (var i = 0; i < WeeksShown * DaysPerWeek; i++)
            {
                var date = gridStart.AddDays(i);
                var inMonth = date.Year == month.Year && date.Month == month.Month;

                var band = DailyHealthBand.NoData;
                var tooltip = string.Empty;
                if (inMonth && byDate.TryGetValue(date, out var day))
                {
                    band = day.Band;
                    tooltip = day.Tooltip;
                }
                else if (inMonth)
                {
                    tooltip = "No data collected.";
                }

                Cells.Add(new PerformanceCalendarCell(
                    date,
                    inMonth,
                    isToday: date == today,
                    isSelected: selected.HasValue && date == selected.Value,
                    band,
                    tooltip));
            }
        }

        private void PrevMonthButton_Click(object sender, RoutedEventArgs e) =>
            NavigateToMonth(FirstOfMonth(DisplayMonth).AddMonths(-1));

        private void NextMonthButton_Click(object sender, RoutedEventArgs e) =>
            NavigateToMonth(FirstOfMonth(DisplayMonth).AddMonths(1));

        private void TodayButton_Click(object sender, RoutedEventArgs e) =>
            NavigateToMonth(FirstOfMonth(DateTime.UtcNow));

        private void NavigateToMonth(DateTime month)
        {
            DisplayMonth = month; // triggers RebuildCells via the DP callback
            MonthChanged?.Invoke(this, new PerformanceCalendarMonthEventArgs(month));
        }

        private void Cell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: PerformanceCalendarCell cell } && cell.IsInDisplayMonth)
            {
                DayClicked?.Invoke(this, new PerformanceCalendarDayEventArgs(cell.Date));
            }
        }
    }

    /// <summary>
    /// The render model for one calendar cell. Immutable; the control rebuilds the whole set on any change,
    /// so no change notification is needed. The presentation opacities/label are derived from the band here
    /// (rather than in XAML) so the cell template stays declarative.
    /// </summary>
    public sealed class PerformanceCalendarCell
    {
        public PerformanceCalendarCell(
            DateTime date, bool isInDisplayMonth, bool isToday, bool isSelected,
            DailyHealthBand band, string tooltip)
        {
            Date = date;
            IsInDisplayMonth = isInDisplayMonth;
            IsToday = isToday;
            IsSelected = isSelected;
            Band = band;
            Tooltip = tooltip;
            DayNumber = date.Day.ToString(CultureInfo.CurrentCulture);

            if (!isInDisplayMonth)
            {
                WashOpacity = 0.0;
                NumberOpacity = 0.45;
            }
            else if (band == DailyHealthBand.NoData)
            {
                WashOpacity = 0.14;
                NumberOpacity = 0.55;
            }
            else
            {
                WashOpacity = 0.55;
                NumberOpacity = 1.0;
            }

            // A textual band label on non-healthy in-month days aids at-a-glance scanning and color-blind
            // legibility; healthy / no-data cells stay uncluttered (color and emptiness carry the meaning).
            BandLabel = isInDisplayMonth && (band == DailyHealthBand.Warning || band == DailyHealthBand.Critical)
                ? DailyHealthBandCalculator.Label(band)
                : string.Empty;
        }

        public DateTime Date { get; }
        public bool IsInDisplayMonth { get; }
        public bool IsToday { get; }
        public bool IsSelected { get; }
        public DailyHealthBand Band { get; }
        public string Tooltip { get; }
        public string DayNumber { get; }
        public string BandLabel { get; }
        public double WashOpacity { get; }
        public double NumberOpacity { get; }
    }
}

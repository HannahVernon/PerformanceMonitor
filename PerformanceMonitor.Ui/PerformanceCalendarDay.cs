/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Ui
{
    /// <summary>
    /// One day's worth of banded data supplied to <see cref="PerformanceCalendar"/>. Apps build one of
    /// these per day that had any collection (from their own <c>DailySummaryRow</c> range read), setting the
    /// <see cref="Band"/> they already computed via <see cref="DailyHealthBandCalculator"/> and a hover
    /// <see cref="Tooltip"/>. Days absent from the supplied set render as <see cref="DailyHealthBand.NoData"/>.
    /// </summary>
    public sealed class PerformanceCalendarDay
    {
        /// <summary>The calendar date (date component only; time ignored).</summary>
        public DateTime Date { get; init; }

        /// <summary>The composite health band that colors the cell.</summary>
        public DailyHealthBand Band { get; init; }

        /// <summary>Multi-line hover text summarizing the day's signals (see <see cref="DailyHealthBandCalculator.Describe"/>).</summary>
        public string Tooltip { get; init; } = string.Empty;
    }

    /// <summary>Event args carrying the day a user clicked on the calendar.</summary>
    public sealed class PerformanceCalendarDayEventArgs : EventArgs
    {
        public PerformanceCalendarDayEventArgs(DateTime date)
        {
            Date = date;
        }

        /// <summary>The clicked date (date component only).</summary>
        public DateTime Date { get; }
    }

    /// <summary>Event args carrying the month the calendar wants data for (its first day, at midnight).</summary>
    public sealed class PerformanceCalendarMonthEventArgs : EventArgs
    {
        public PerformanceCalendarMonthEventArgs(DateTime month)
        {
            Month = month;
        }

        /// <summary>The first day of the month now being displayed.</summary>
        public DateTime Month { get; }
    }
}

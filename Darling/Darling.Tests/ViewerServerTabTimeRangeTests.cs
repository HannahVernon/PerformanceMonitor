/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the per-server toolbar's time-range dropdown → hours-back mapping (the pure core that replaced
/// the viewer's old hardcoded 24-hour <c>s_dataWindow</c>). The combo lists 1h / 4h / 12h / 24h / 7d /
/// Custom; a preset index resolves to its hours, and any non-preset slot (Custom, or a stray value)
/// falls back to the viewer's historical 24-hour default so no surface is ever left window-less.
/// </summary>
public sealed class ViewerServerTabTimeRangeTests
{
    [Theory]
    [InlineData(0, 1)]     // Last 1 hour
    [InlineData(1, 4)]     // Last 4 hours
    [InlineData(2, 12)]    // Last 12 hours
    [InlineData(3, 24)]    // Last 24 hours
    [InlineData(4, 168)]   // Last 7 days
    public void PresetIndex_MapsToItsHours(int index, int expectedHours)
    {
        Assert.Equal(expectedHours, ViewerServerTab.TimeRangeIndexToHours(index));
    }

    [Theory]
    [InlineData(5)]   // "Custom Range" — the absolute From/To drives the window, not hours-back
    [InlineData(6)]   // out of range
    [InlineData(-1)]  // out of range
    public void NonPresetIndex_FallsBackTo24Hours(int index)
    {
        Assert.Equal(24, ViewerServerTab.TimeRangeIndexToHours(index));
    }
}

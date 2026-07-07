/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using PerformanceMonitor.Common;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Session Stats inner tab — the Dashboard-parity port of ResourceMetricsContent's Session Stats
/// sub-tab (Dashboard/Controls/ResourceMetricsContent.SessionStats.cs) into the Darling viewer. A single
/// trend chart plots the server-wide session-status counts over the settable window — Total / Running /
/// Sleeping / Background / Dormant / Idle &gt;30m / Waiting for Memory — and a summary strip below it shows
/// the non-chartable attribution the latest snapshot carries: the top application and top host by
/// connection count, and the number of distinct databases with connections.
/// <para>
/// The feed is <see cref="ViewerDataService.GetSessionStatsAsync"/> over <c>v_session_summary_stats</c>
/// (the server-wide summary collector, 1:1 with the Dashboard's <c>session_stats</c> table) — NOT the
/// per-application <c>v_session_stats</c> FinOps' Application Connections read uses. Each status series
/// rides its own fixed <see cref="ChartPalette.SeriesColor"/> (the shared "Session*" keys the Dashboard
/// chart also uses), and, mirroring the Dashboard, a series is only drawn when it has a non-zero value in
/// the window so all-zero states (e.g. Background/Waiting) don't clutter the legend. Chart chrome / legend
/// / line polish / Y-floor-at-0 flow through the shared <see cref="ChartStyle"/> and the
/// <c>ViewerServerTab.ChartHelpers.cs</c> bridge, exactly like the sibling trend tabs; the chart's
/// Copy/Save/CSV right-click menu is wired in <c>ViewerServerTab.ChartContextMenu.cs</c>.
/// </para>
/// </summary>
public partial class ViewerServerTab
{
    private ChartHoverHelper? _sessionStatsHover;

    /// <summary>One chart series: its legend label, its shared-palette color key, and the accessor that
    /// pulls its status count out of a <see cref="SessionStatsPoint"/>. Pure data so the mapping (which
    /// column drives which colored series) is unit-testable without a WpfPlot; the render loop and the
    /// tests share this single source of truth.</summary>
    internal readonly record struct SessionSeriesSpec(string Legend, string PaletteKey, Func<SessionStatsPoint, double> Value);

    /// <summary>
    /// The seven session-status series in the Dashboard's order (Total first, then the status breakdown),
    /// each pinned to the shared <c>Session*</c> palette key the Dashboard chart uses. The tuple's accessor
    /// is the verified <c>session_summary_stats</c> column each series reads.
    /// </summary>
    internal static IReadOnlyList<SessionSeriesSpec> SessionSeriesSpecs { get; } = new[]
    {
        new SessionSeriesSpec("Total", "SessionTotal", p => p.TotalSessions),
        new SessionSeriesSpec("Running", "SessionRunning", p => p.RunningSessions),
        new SessionSeriesSpec("Sleeping", "SessionSleeping", p => p.SleepingSessions),
        new SessionSeriesSpec("Background", "SessionBackground", p => p.BackgroundSessions),
        new SessionSeriesSpec("Dormant", "SessionDormant", p => p.DormantSessions),
        new SessionSeriesSpec("Idle >30m", "SessionIdle", p => p.IdleSessionsOver30Min),
        new SessionSeriesSpec("Waiting for Memory", "SessionWaiting", p => p.SessionsWaitingForMemory),
    };

    /// <summary>Applies the shared chrome + hover to the Session Stats chart up front (constructor), so it
    /// doesn't flash white before the tab's first load — matching the CPU/Memory/latch charts.</summary>
    private void InitializeSessionStatsChart()
    {
        ApplyTheme(SessionStatsChart);
        SessionStatsChart.Refresh();
        _sessionStatsHover = new ChartHoverHelper(SessionStatsChart, "sessions");
    }

    /// <summary>Loads the Session Stats tab: the session-summary snapshots over the toolbar's settable
    /// window, then renders the status-count chart and the top-app / top-host / databases summary.</summary>
    private async Task LoadSessionStatsAsync()
    {
        var (startUtc, endUtc) = GetWindowUtc();
        var data = await _dataService.GetSessionStatsAsync(_server.ServerId, startUtc, endUtc);
        RenderSessionStatsChart(data);
    }

    private void RenderSessionStatsChart(List<SessionStatsPoint> data)
    {
        ClearChart(SessionStatsChart);
        _sessionStatsHover?.Clear();
        ApplyTheme(SessionStatsChart);

        var (startUtc, endUtc) = GetWindowUtc();
        SessionStatsChart.Plot.YLabel("Session Count");

        var ordered = data.OrderBy(d => d.CollectionTime).ToList();
        if (ordered.Count == 0)
        {
            UpdateSessionStatsSummary(null);
            FinishSessionStatsChart(startUtc, endUtc, 0);
            return;
        }

        var times = ordered.Select(d => ViewerTimeHelper.ForDisplay(d.CollectionTime).ToOADate()).ToArray();

        double globalMax = 0;
        foreach (var spec in SessionSeriesSpecs)
        {
            var values = ordered.Select(spec.Value).ToArray();

            /* Mirror the Dashboard: only draw a series that is non-zero somewhere in the window, so
               all-zero states (often Background / Dormant / Waiting for Memory) stay out of the legend. */
            if (!values.Any(v => v > 0))
            {
                continue;
            }

            var plot = SessionStatsChart.Plot.Add.Scatter(times, values);
            plot.LegendText = spec.Legend;
            plot.Color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor(spec.PaletteKey));
            ChartStyle.StyleScatter(plot);
            _sessionStatsHover?.Add(plot, spec.Legend);

            globalMax = Math.Max(globalMax, values.Max());
        }

        /* Summary panel reflects the latest snapshot in the window (Dashboard uses the last data point). */
        UpdateSessionStatsSummary(ordered[^1]);
        FinishSessionStatsChart(startUtc, endUtc, globalMax);
    }

    /// <summary>Shared chart finish for the Session Stats trend: date axis, window X-limits, Y floor at 0
    /// with legend padding, and the legend — the one place the window bounds + Y-floor fix apply.</summary>
    private void FinishSessionStatsChart(DateTime startUtc, DateTime endUtc, double globalMax)
    {
        SessionStatsChart.Plot.Axes.DateTimeTicksBottomDateChange();
        var rangeStart = ViewerTimeHelper.ForDisplay(startUtc);
        var rangeEnd = ViewerTimeHelper.ForDisplay(endUtc);
        SessionStatsChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
        ReapplyAxisColors(SessionStatsChart);
        SetChartYLimitsWithLegendPadding(SessionStatsChart, 0, globalMax > 0 ? globalMax : 10);
        ShowChartLegend(SessionStatsChart);
        SessionStatsChart.Refresh();
    }

    /// <summary>Writes the latest snapshot's attribution into the summary strip (Dashboard parity: the
    /// non-chartable Top Application / Top Host / Databases values), delegating the formatting to the pure
    /// <see cref="FormatSessionSummary"/> so the "name (count)" / N/A shaping is unit-tested.</summary>
    private void UpdateSessionStatsSummary(SessionStatsPoint? data)
    {
        var (topApp, topHost, databases) = FormatSessionSummary(data);
        SessionStatsTopAppText.Text = topApp;
        SessionStatsTopHostText.Text = topHost;
        SessionStatsDatabasesText.Text = databases;
    }

    /// <summary>
    /// Pure formatter for the summary strip (mirrors the Dashboard's <c>UpdateSessionStatsSummary</c>):
    /// Top Application / Top Host render as "<c>name (count)</c>" when the latest snapshot has one and
    /// "N/A" otherwise; Databases is the distinct-databases count. A null point (no data in the window)
    /// yields "N/A" for all three.
    /// </summary>
    internal static (string TopApplication, string TopHost, string Databases) FormatSessionSummary(SessionStatsPoint? data)
    {
        if (data is null)
        {
            return ("N/A", "N/A", "N/A");
        }

        var topApp = !string.IsNullOrEmpty(data.TopApplicationName)
            ? $"{data.TopApplicationName} ({data.TopApplicationConnections ?? 0})"
            : "N/A";
        var topHost = !string.IsNullOrEmpty(data.TopHostName)
            ? $"{data.TopHostName} ({data.TopHostConnections ?? 0})"
            : "N/A";
        var databases = data.DatabasesWithConnections.ToString(CultureInfo.CurrentCulture);

        return (topApp, topHost, databases);
    }

    /// <summary>Tears down the Session Stats hover helper (mirrors the other tabs' dispose) so its tooltip
    /// popup + chart event handlers don't outlive a closed server tab.</summary>
    public void DisposeSessionStatsHelpers()
    {
        _sessionStatsHover?.Dispose();
    }
}

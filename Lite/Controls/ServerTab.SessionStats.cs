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
using System.Windows.Controls;
using PerformanceMonitorLite.Services;
using PerformanceMonitor.Common;
using PerformanceMonitor.Ui;

namespace PerformanceMonitorLite.Controls;

/// <summary>
/// The Session Stats tab — the Lite port of the Darling viewer's Session Stats surface (itself the
/// Dashboard-parity port of ResourceMetricsContent's Session Stats sub-tab). A single trend chart plots
/// the server-wide session-status counts over the settable window — Total / Running / Sleeping /
/// Background / Dormant / Idle &gt;30m / Waiting for Memory — and a summary strip below shows the
/// non-chartable attribution the latest snapshot carries: the top application and top host by connection
/// count, and the number of distinct databases with connections. The feed is the server-wide
/// v_session_summary_stats (1:1 with the Dashboard's session_stats table) — NOT the per-application
/// v_session_stats FinOps' Application Connections read uses. Each status series rides its own fixed
/// <see cref="ChartPalette"/> "Session*" key (the same the Dashboard chart uses), and — mirroring the
/// Dashboard — a series is only drawn when it has a non-zero value in the window so all-zero states don't
/// clutter the legend. Loads on tab activation via the RefreshVisibleTabAsync switch.
/// </summary>
public partial class ServerTab : UserControl
{
    private ChartHoverHelper? _sessionStatsHover;

    /// <summary>One chart series: its legend label, its shared-palette color key, and the accessor that
    /// pulls its status count out of a <see cref="SessionStatsPoint"/>. Pure data so the mapping is
    /// unit-testable without a WpfPlot; the render loop and any tests share this single source of truth.</summary>
    internal readonly record struct SessionSeriesSpec(string Legend, string PaletteKey, Func<SessionStatsPoint, double> Value);

    /// <summary>The seven session-status series in the Dashboard's order (Total first, then the status
    /// breakdown), each pinned to the shared <c>Session*</c> palette key the Dashboard chart uses.</summary>
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

    /// <summary>Loads the Session Stats tab over the toolbar's settable window: the session-summary
    /// snapshots, then renders the status-count chart and the top-app / top-host / databases summary.</summary>
    private async System.Threading.Tasks.Task RefreshSessionStatsAsync(int hoursBack, DateTime? fromDate, DateTime? toDate)
    {
        try
        {
            var data = await Helpers.MethodProfiler.TimeAsync("SessionStats.Trend", () => Task.Run(() => SafeQueryAsync(() => _dataService.GetSessionStatsAsync(_serverId, hoursBack, fromDate, toDate))));
            UpdateSessionStatsChart(data, hoursBack, fromDate, toDate);
        }
        catch (Exception ex)
        {
            AppLogger.Info("ServerTab", $"[{_server.DisplayName}] RefreshSessionStatsAsync failed: {ex.Message}");
        }
    }

    /// <summary>The session-status trend: one series per non-zero status (heaviest states drawn on their
    /// fixed palette keys), plus the summary strip from the latest snapshot. Mirrors Darling's
    /// RenderSessionStatsChart.</summary>
    private void UpdateSessionStatsChart(List<SessionStatsPoint> data, int hoursBack, DateTime? fromDate, DateTime? toDate)
    {
        ClearChart(SessionStatsChart);
        ApplyTheme(SessionStatsChart);
        _sessionStatsHover?.Clear();

        DateTime rangeStart, rangeEnd;
        if (fromDate.HasValue && toDate.HasValue)
        {
            rangeStart = fromDate.Value;
            rangeEnd = toDate.Value;
        }
        else
        {
            rangeEnd = DateTime.UtcNow.AddMinutes(UtcOffsetMinutes);
            rangeStart = rangeEnd.AddHours(-hoursBack);
        }

        var ordered = data.OrderBy(d => d.CollectionTime).ToList();

        double globalMax = 0;
        if (ordered.Count > 0)
        {
            var times = ordered.Select(d => d.CollectionTime.AddMinutes(UtcOffsetMinutes).ToOADate()).ToArray();

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

            /* Summary panel reflects the latest snapshot in the window (Dashboard uses the last point). */
            UpdateSessionStatsSummary(ordered[^1]);
        }
        else
        {
            UpdateSessionStatsSummary(null);
        }

        SessionStatsChart.Plot.Axes.DateTimeTicksBottomDateChange();
        SessionStatsChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
        ReapplyAxisColors(SessionStatsChart);
        SessionStatsChart.Plot.YLabel("Session Count");
        SetChartYLimitsWithLegendPadding(SessionStatsChart, 0, globalMax > 0 ? globalMax : 10);
        ShowChartLegend(SessionStatsChart);
        SessionStatsChart.Refresh();
    }

    /// <summary>Writes the latest snapshot's attribution into the summary strip (Dashboard parity: the
    /// non-chartable Top Application / Top Host / Databases values), delegating the shaping to the pure
    /// <see cref="FormatSessionSummary"/>.</summary>
    private void UpdateSessionStatsSummary(SessionStatsPoint? data)
    {
        var (topApp, topHost, databases) = FormatSessionSummary(data);
        SessionStatsTopAppText.Text = topApp;
        SessionStatsTopHostText.Text = topHost;
        SessionStatsDatabasesText.Text = databases;
    }

    /// <summary>
    /// Pure formatter for the summary strip (mirrors the Darling/Dashboard UpdateSessionStatsSummary):
    /// Top Application / Top Host render as "<c>name (count)</c>" when the latest snapshot has one and
    /// "N/A" otherwise; Databases is the distinct-databases count. A null point (no data in the window)
    /// yields "N/A" for all three. Internal + static so the shaping is unit-testable.
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
        var databases = data.DatabasesWithConnections.ToString();

        return (topApp, topHost, databases);
    }

    /// <summary>Tears down the Session Stats hover helper (mirrors the other tabs' dispose) so its tooltip
    /// popup + chart event handlers don't outlive a closed server tab.</summary>
    public void DisposeSessionStatsHelpers()
    {
        _sessionStatsHover?.Dispose();
    }
}

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
/// The CPU Scheduler tab — the Lite port of the Darling viewer's CPU Scheduler surface (itself the
/// Dashboard-parity port of the cpu_scheduler_stats reporting). A point-in-time snapshot collector, so
/// the trend chart plots the runnable / blocked / queued task counts directly over the settable window
/// (no delta math) and the latest snapshot renders as a metric/value grid whose warning rows highlight
/// (the collector's own CASE-computed pressure flags). Loads on tab activation via the
/// RefreshVisibleTabAsync switch (mirroring tempdb / Latches &amp; Spinlocks), so no SelectionChanged
/// handler is needed. The chart rides the shared ChartStyle / ChartHoverHelper + the same SeriesColors +
/// Y-floor-at-0 helpers as Lite's other multi-series trends.
/// </summary>
public partial class ServerTab : UserControl
{
    private ChartHoverHelper? _cpuSchedulerHover;

    /// <summary>Applies the shared chrome + hover to the scheduler chart up front (constructor), so it
    /// doesn't flash white before the tab's first load — matching the CPU/Memory charts.</summary>
    private void InitializeCpuSchedulerChart()
    {
        ApplyTheme(CpuSchedulerChart);
        CpuSchedulerChart.Refresh();
        _cpuSchedulerHover = new ChartHoverHelper(CpuSchedulerChart, "tasks");
    }

    /// <summary>
    /// Loads the CPU Scheduler tab over the toolbar's settable window: the pressure trend and the
    /// latest-snapshot metric read fire concurrently, then the chart and metric grid render. Mirrors the
    /// tempdb tab's full-refresh branch.
    /// </summary>
    private async System.Threading.Tasks.Task RefreshCpuSchedulerAsync(int hoursBack, DateTime? fromDate, DateTime? toDate)
    {
        try
        {
            var trendTask = Helpers.MethodProfiler.TimeAsync("CpuScheduler.Trend", () => Task.Run(() => SafeQueryAsync(() => _dataService.GetCpuSchedulerTrendAsync(_serverId, hoursBack, fromDate, toDate))));
            var snapshotTask = Helpers.MethodProfiler.TimeAsync("CpuScheduler.Snapshot", () => Task.Run(() => _dataService.GetCpuSchedulerSnapshotAsync(_serverId, hoursBack, fromDate, toDate)));

            await System.Threading.Tasks.Task.WhenAll(trendTask, snapshotTask);

            UpdateCpuSchedulerChart(trendTask.Result, hoursBack, fromDate, toDate);
            CpuSchedulerGrid.ItemsSource = BuildCpuSchedulerMetrics(snapshotTask.Result);
        }
        catch (Exception ex)
        {
            AppLogger.Info("ServerTab", $"[{_server.DisplayName}] RefreshCpuSchedulerAsync failed: {ex.Message}");
        }
    }

    /// <summary>The scheduler pressure trend: three fixed series (Runnable / Blocked / Queued task counts)
    /// plotted directly (point-in-time collector), a flat labelled zero-line window when the range has no
    /// data. Mirrors Darling's RenderCpuSchedulerChart.</summary>
    private void UpdateCpuSchedulerChart(List<CpuSchedulerTrendPoint> data, int hoursBack, DateTime? fromDate, DateTime? toDate)
    {
        ClearChart(CpuSchedulerChart);
        ApplyTheme(CpuSchedulerChart);
        _cpuSchedulerHover?.Clear();

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

        double globalMax = 0;
        if (data.Count > 0)
        {
            var ordered = data.OrderBy(d => d.CollectionTime).ToList();
            var times = ordered.Select(d => d.CollectionTime.AddMinutes(UtcOffsetMinutes).ToOADate()).ToArray();

            var series = new (string Name, Func<CpuSchedulerTrendPoint, double> Selector)[]
            {
                ("Runnable Tasks", d => d.RunnableTasks),
                ("Blocked Tasks", d => d.BlockedTasks),
                ("Queued Requests", d => d.QueuedRequests),
            };

            int colorIdx = 0;
            foreach (var s in series)
            {
                var values = ordered.Select(s.Selector).ToArray();
                var plot = CpuSchedulerChart.Plot.Add.Scatter(times, values);
                plot.LegendText = s.Name;
                plot.Color = ScottPlot.Color.FromHex(SeriesColors[colorIdx % SeriesColors.Length]);
                ChartStyle.StyleScatter(plot);
                _cpuSchedulerHover?.Add(plot, s.Name);
                colorIdx++;
                if (values.Length > 0) globalMax = Math.Max(globalMax, values.Max());
            }
        }

        CpuSchedulerChart.Plot.Axes.DateTimeTicksBottomDateChange();
        CpuSchedulerChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
        ReapplyAxisColors(CpuSchedulerChart);
        CpuSchedulerChart.Plot.YLabel("Task Count");
        SetChartYLimitsWithLegendPadding(CpuSchedulerChart, 0, globalMax > 0 ? globalMax : 5);
        ShowChartLegend(CpuSchedulerChart);
        CpuSchedulerChart.Refresh();
    }

    /// <summary>Projects the latest snapshot into the metric grid's labelled rows (with the collector's
    /// pressure flags driving the row highlight). An empty window yields an empty grid. Internal + static
    /// so the projection (warning flags, worker-utilization math, memory formatting) is unit-testable.
    /// Ported from Darling's BuildCpuSchedulerMetrics.</summary>
    internal static List<CpuSchedulerMetricRow> BuildCpuSchedulerMetrics(CpuSchedulerSnapshot? s)
    {
        if (s is null)
        {
            return new List<CpuSchedulerMetricRow>();
        }

        double workerUtil = s.MaxWorkersCount > 0
            ? (double)s.TotalCurrentWorkersCount / s.MaxWorkersCount * 100.0
            : 0;

        /* Rolled-up pressure badge + recommendation (install/47 report.cpu_scheduler_pressure parity): the
           raw warning booleans are kept below, but these two headline rows give the one-glance severity +
           next step. The level row highlights whenever it is not NORMAL. */
        var pressure = ClassifyCpuPressure(s);

        var rows = new List<CpuSchedulerMetricRow>
        {
            new("Pressure Level", pressure.Level, !pressure.Level.StartsWith("NORMAL", StringComparison.Ordinal)),
            new("Recommendation", pressure.Recommendation, false),
            new("Schedulers", FormatInt(s.SchedulerCount), false),
            new("Logical CPUs", FormatInt(s.CpuCount), false),
            new("NUMA Nodes (online / total)", $"{s.NodesOnlineCount} / {s.TotalNodeCount}", false),
            new("Offline CPUs", FormatInt(s.OfflineCpuCount), s.OfflineCpuWarning),
            new("Max Worker Threads", FormatInt(s.MaxWorkersCount), false),
            new("Current Workers", FormatInt(s.TotalCurrentWorkersCount), s.WorkerThreadExhaustionWarning),
            new("Worker Utilization %", workerUtil.ToString("F1"), s.WorkerThreadExhaustionWarning),
            new("Runnable Tasks", FormatInt(s.TotalRunnableTasksCount), s.RunnableTasksWarning),
            new("Avg Runnable / Scheduler", s.AvgRunnableTasksCount.ToString("F2"), s.RunnableTasksWarning),
            new("Work Queue Length", FormatLong(s.TotalWorkQueueCount), false),
            new("Active Requests", FormatInt(s.TotalActiveRequestCount), false),
            new("Queued Requests", FormatInt(s.TotalQueuedRequestCount), s.QueuedRequestsWarning),
            new("Blocked Tasks", FormatInt(s.TotalBlockedTaskCount), s.BlockedTasksWarning),
            new("Active Parallel Threads", FormatLong(s.TotalActiveParallelThreadCount), false),
            new("Runnable Requests", FormatNullableInt(s.RunnableRequestCount), false),
            new("Total Requests", FormatNullableInt(s.TotalRequestCount), false),
            new("Runnable %", s.RunnablePercent.HasValue ? s.RunnablePercent.Value.ToString("F2") : "N/A", false),
            new("Total Physical Memory", FormatKbAsGb(s.TotalPhysicalMemoryKb), false),
            new("Available Physical Memory", FormatKbAsGb(s.AvailablePhysicalMemoryKb), s.PhysicalMemoryPressureWarning),
            new("System Memory State", string.IsNullOrEmpty(s.SystemMemoryStateDesc) ? "N/A" : s.SystemMemoryStateDesc, s.PhysicalMemoryPressureWarning),
            new("Physical Memory Pressure", s.PhysicalMemoryPressureWarning ? "Yes" : "No", s.PhysicalMemoryPressureWarning),
        };

        return rows;
    }

    /// <summary>
    /// The rolled-up CPU-scheduler pressure classification, a pure client-side derivation mirroring
    /// install/47's <c>report.cpu_scheduler_pressure</c> and the Darling viewer's ClassifyCpuPressure. The
    /// banding is the proc's own CASE, in the same order: runnable task queue &gt; 50 CRITICAL / &gt; 20
    /// HIGH / &gt; 10 MEDIUM, then worker-utilization &gt; 90% HIGH, then the collector's worker-exhaustion
    /// / runnable-tasks / queued-requests warning flags. Static + pure so it is unit-testable.
    /// </summary>
    internal static CpuPressure ClassifyCpuPressure(CpuSchedulerSnapshot s)
    {
        double workerUtil = s.MaxWorkersCount > 0
            ? (double)s.TotalCurrentWorkersCount / s.MaxWorkersCount * 100.0
            : 0;

        var level =
            s.TotalRunnableTasksCount > 50 ? "CRITICAL - High runnable task queue" :
            s.TotalRunnableTasksCount > 20 ? "HIGH - Moderate runnable task queue" :
            s.TotalRunnableTasksCount > 10 ? "MEDIUM - Some runnable tasks queued" :
            workerUtil > 90 ? "HIGH - Worker thread exhaustion" :
            s.WorkerThreadExhaustionWarning ? "CRITICAL - Worker thread exhaustion warning" :
            s.RunnableTasksWarning ? "HIGH - Runnable tasks warning" :
            s.QueuedRequestsWarning ? "MEDIUM - Queued requests warning" :
            "NORMAL";

        var recommendation =
            s.TotalRunnableTasksCount > 20 ? "CPU pressure detected - check for CPU-intensive queries, consider adding CPU cores" :
            s.WorkerThreadExhaustionWarning ? "Worker thread exhaustion - check max worker threads setting" :
            s.TotalQueuedRequestCount > 0 ? "Requests queued for execution - CPU or worker thread pressure" :
            "No CPU scheduler pressure detected";

        return new CpuPressure(level, recommendation);
    }

    private static string FormatInt(int value) => value.ToString("N0");

    private static string FormatLong(long value) => value.ToString("N0");

    private static string FormatNullableInt(int? value) => value.HasValue ? value.Value.ToString("N0") : "N/A";

    private static string FormatKbAsGb(long kb)
    {
        double gb = kb / 1024.0 / 1024.0;
        return gb >= 1 ? $"{gb:F1} GB" : $"{kb / 1024.0:F0} MB";
    }

    /// <summary>Tears down the scheduler hover helper (mirrors the other tabs' dispose) so its tooltip
    /// popup + chart event handlers don't outlive a closed server tab.</summary>
    public void DisposeCpuSchedulerHelpers()
    {
        _cpuSchedulerHover?.Dispose();
    }
}

/// <summary>One row of the CPU Scheduler latest-snapshot metric grid: a labelled scalar from the most
/// recent cpu_scheduler_stats row, plus whether the collector flagged it as pressure (drives the row
/// highlight). A single-row point-in-time collector has no natural top-N, so its "latest snapshot" is
/// presented as this metric/value list.</summary>
public sealed record CpuSchedulerMetricRow(string Metric, string Value, bool IsWarning);

/// <summary>The rolled-up CPU-scheduler pressure classification for the metric grid's headline rows: the
/// banded pressure level plus its paired recommendation, mirroring install/47's
/// report.cpu_scheduler_pressure.</summary>
public sealed record CpuPressure(string Level, string Recommendation);

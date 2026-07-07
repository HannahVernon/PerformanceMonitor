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

/// <summary>One row of the CPU Scheduler latest-snapshot metric grid: a labelled scalar from the most
/// recent cpu_scheduler_stats row, plus whether the collector flagged it as pressure (drives the row
/// highlight). A single-row point-in-time collector has no natural top-N, so its "latest snapshot" is
/// presented as this metric/value list — the grid analog of the Dashboard's CPU-pressure summary strip.</summary>
public sealed record CpuSchedulerMetricRow(string Metric, string Value, bool IsWarning);

/// <summary>The rolled-up CPU-scheduler pressure classification for the metric grid's headline rows: the
/// banded pressure level plus its paired recommendation, mirroring install/47's report.cpu_scheduler_pressure
/// and the Dashboard's <c>CpuPressureItem.PressureLevel/Recommendation</c>.</summary>
public sealed record CpuPressure(string Level, string Recommendation);

/// <summary>
/// The CPU Scheduler sub-tab (under the CPU tab, alongside CPU Utilization) — the Darling-viewer surface
/// for the cpu_scheduler_stats collector (scheduler / worker-thread / runnable-task / NUMA / OS-memory
/// pressure), mirroring the Dashboard's CPU-pressure reporting. A point-in-time collector, so the trend
/// chart plots the runnable / blocked / queued task counts directly over the settable window and the
/// latest snapshot renders as a metric/value grid whose warning rows highlight (the collector's own
/// CASE-computed pressure flags). Loaded from <c>LoadCpuAsync</c> alongside CPU Utilization (the CPU
/// tab's full-refresh branch), so the CPU sub-TabControl needs no SelectionChanged handler. Chart chrome
/// flows through the shared <see cref="ChartStyle"/> / <see cref="ChartPalette"/> and the ChartHelpers
/// bridge, so the Y-floor-at-0 fix applies.
/// </summary>
public partial class ViewerServerTab
{
    private ChartHoverHelper? _cpuSchedulerHover;

    /// <summary>Applies the shared chrome + hover to the scheduler chart up front (constructor).</summary>
    private void InitializeCpuSchedulerChart()
    {
        ApplyTheme(CpuSchedulerChart);
        CpuSchedulerChart.Refresh();
        _cpuSchedulerHover = new ChartHoverHelper(CpuSchedulerChart, "tasks");
    }

    /// <summary>
    /// Loads the CPU Scheduler sub-tab over the toolbar's settable window: the pressure trend and the
    /// latest-snapshot metric read fire concurrently, then the chart and metric grid render.
    /// </summary>
    private async Task LoadCpuSchedulerAsync()
    {
        var (startUtc, endUtc) = GetWindowUtc();

        var trendTask = _dataService.GetCpuSchedulerTrendAsync(_server.ServerId, startUtc, endUtc);
        var snapshotTask = _dataService.GetCpuSchedulerSnapshotAsync(_server.ServerId, startUtc, endUtc);
        await Task.WhenAll(trendTask, snapshotTask);

        RenderCpuSchedulerChart(trendTask.Result);
        CpuSchedulerGrid.ItemsSource = BuildCpuSchedulerMetrics(snapshotTask.Result);
    }

    private void RenderCpuSchedulerChart(List<CpuSchedulerTrendPoint> data)
    {
        ClearChart(CpuSchedulerChart);
        _cpuSchedulerHover?.Clear();
        ApplyTheme(CpuSchedulerChart);

        var (startUtc, endUtc) = GetWindowUtc();
        CpuSchedulerChart.Plot.YLabel("Task Count");

        double globalMax = 0;
        if (data.Count > 0)
        {
            var times = data.Select(d => ViewerTimeHelper.ForDisplay(d.CollectionTime).ToOADate()).ToArray();

            var series = new (string Name, Func<CpuSchedulerTrendPoint, double> Selector)[]
            {
                ("Runnable Tasks", d => d.RunnableTasks),
                ("Blocked Tasks", d => d.BlockedTasks),
                ("Queued Requests", d => d.QueuedRequests),
            };

            int colorIdx = 0;
            foreach (var s in series)
            {
                var values = data.Select(s.Selector).ToArray();
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
        var rangeStart = ViewerTimeHelper.ForDisplay(startUtc);
        var rangeEnd = ViewerTimeHelper.ForDisplay(endUtc);
        CpuSchedulerChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
        ReapplyAxisColors(CpuSchedulerChart);
        SetChartYLimitsWithLegendPadding(CpuSchedulerChart, 0, globalMax > 0 ? globalMax : 5);
        ShowChartLegend(CpuSchedulerChart);
        CpuSchedulerChart.Refresh();
    }

    /// <summary>Projects the latest snapshot into the metric grid's labelled rows (with the collector's
    /// pressure flags driving the highlight). An empty window yields an empty grid. Internal so the
    /// projection (warning flags, worker-utilization math, memory formatting) is unit-testable.</summary>
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
           next step the Dashboard surfaces. The level row highlights whenever it is not NORMAL. */
        var pressure = ClassifyCpuPressure(s);

        var rows = new List<CpuSchedulerMetricRow>
        {
            new("Pressure Level", pressure.Level, !pressure.Level.StartsWith("NORMAL", StringComparison.Ordinal)),
            new("Recommendation", pressure.Recommendation, false),
            new("Schedulers", Int(s.SchedulerCount), false),
            new("Logical CPUs", Int(s.CpuCount), false),
            new("NUMA Nodes (online / total)", $"{s.NodesOnlineCount} / {s.TotalNodeCount}", false),
            new("Offline CPUs", Int(s.OfflineCpuCount), s.OfflineCpuWarning),
            new("Max Worker Threads", Int(s.MaxWorkersCount), false),
            new("Current Workers", Int(s.TotalCurrentWorkersCount), s.WorkerThreadExhaustionWarning),
            new("Worker Utilization %", workerUtil.ToString("F1", CultureInfo.CurrentCulture), s.WorkerThreadExhaustionWarning),
            new("Runnable Tasks", Int(s.TotalRunnableTasksCount), s.RunnableTasksWarning),
            new("Avg Runnable / Scheduler", s.AvgRunnableTasksCount.ToString("F2", CultureInfo.CurrentCulture), s.RunnableTasksWarning),
            new("Work Queue Length", Long(s.TotalWorkQueueCount), false),
            new("Active Requests", Int(s.TotalActiveRequestCount), false),
            new("Queued Requests", Int(s.TotalQueuedRequestCount), s.QueuedRequestsWarning),
            new("Blocked Tasks", Int(s.TotalBlockedTaskCount), s.BlockedTasksWarning),
            new("Active Parallel Threads", Long(s.TotalActiveParallelThreadCount), false),
            new("Runnable Requests", NullableInt(s.RunnableRequestCount), false),
            new("Total Requests", NullableInt(s.TotalRequestCount), false),
            new("Runnable %", s.RunnablePercent.HasValue ? s.RunnablePercent.Value.ToString("F2", CultureInfo.CurrentCulture) : "N/A", false),
            new("Total Physical Memory", FormatKbAsGb(s.TotalPhysicalMemoryKb), false),
            new("Available Physical Memory", FormatKbAsGb(s.AvailablePhysicalMemoryKb), s.PhysicalMemoryPressureWarning),
            new("System Memory State", string.IsNullOrEmpty(s.SystemMemoryStateDesc) ? "N/A" : s.SystemMemoryStateDesc, s.PhysicalMemoryPressureWarning),
            new("Physical Memory Pressure", s.PhysicalMemoryPressureWarning ? "Yes" : "No", s.PhysicalMemoryPressureWarning),
        };

        return rows;
    }

    /// <summary>
    /// The rolled-up CPU-scheduler pressure classification, a pure client-side derivation mirroring
    /// install/47's <c>report.cpu_scheduler_pressure</c> (pressure_level + recommendation) and the
    /// Dashboard's <c>CpuPressureItem</c>. The banding is the proc's own CASE, in the same order: runnable
    /// task queue &gt; 50 CRITICAL / &gt; 20 HIGH / &gt; 10 MEDIUM, then worker-utilization &gt; 90% HIGH,
    /// then the collector's worker-exhaustion / runnable-tasks / queued-requests warning flags. Static + pure
    /// so it is unit-testable without Postgres.
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

    private static string Int(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string Long(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string NullableInt(int? value) => value.HasValue ? value.Value.ToString("N0", CultureInfo.CurrentCulture) : "N/A";

    private static string FormatKbAsGb(long kb)
    {
        double gb = kb / 1024.0 / 1024.0;
        return gb >= 1 ? $"{gb:F1} GB" : $"{kb / 1024.0:F0} MB";
    }

    /// <summary>Tears down the scheduler hover helper (mirrors the other tabs' dispose).</summary>
    public void DisposeCpuSchedulerHelpers()
    {
        _cpuSchedulerHover?.Dispose();
    }
}

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
using PerformanceMonitor.Notifications;

namespace PerformanceMonitor.Alerting;

/// <summary>
/// The six pure alert-context builders (Phase-5 slice A) both apps' alert engines call, plus the
/// small pure helpers they share (<see cref="ContextToDetailText"/>, <see cref="TruncateText"/>,
/// <see cref="GetBreachedVolumes"/>, <see cref="FormatLowDiskThreshold"/>). Moved verbatim from the
/// line-identical private copies in Lite's and the Dashboard's <c>MainWindow.AlertEngine.cs</c> so
/// the rendered alert detail (and the #1140 dedup fingerprints) can no longer drift between the
/// apps — and so the headless Darling alert engine renders the same alerts from the same rows.
/// <para>
/// The ONE reconciled difference at extraction time: <see cref="BuildLongRunningQueryContext"/>
/// adopts the Dashboard's version, which renders a ("Program", ProgramName) detail item Lite's
/// copy lacked — so Lite's long-running-query alerts gain the Program field.
/// </para>
/// <para>
/// The two async builders (blocking/deadlock) intentionally stay app-side: they query each app's
/// own store and carry app-specific event shapes. They are a later slice.
/// </para>
/// </summary>
public static class AlertContextBuilders
{
    public static AlertContext? BuildPoisonWaitContext(List<PoisonWaitDelta> triggeredWaits)
    {
        if (triggeredWaits.Count == 0) return null;

        var context = new AlertContext();
        foreach (var w in triggeredWaits)
        {
            context.Details.Add(new AlertDetailItem
            {
                Heading = w.WaitType,
                Fields = new()
                {
                    ("Avg ms/wait", $"{w.AvgMsPerWait:F1}"),
                    ("Delta wait ms", $"{w.DeltaMs:N0}"),
                    ("Delta tasks", $"{w.DeltaTasks:N0}")
                }
            });
        }
        return context;
    }

    public static AlertContext? BuildLongRunningQueryContext(string serverName, List<LongRunningQueryInfo> queries)
    {
        if (queries.Count == 0) return null;

        var context = new AlertContext();
        var shown = queries.GetRange(0, Math.Min(3, queries.Count));
        foreach (var q in shown)
        {
            var item = new AlertDetailItem
            {
                Heading = $"Session #{q.SessionId} — {q.ElapsedSeconds / 60}m {q.ElapsedSeconds % 60}s",
                Fields = new()
            };

            if (!string.IsNullOrEmpty(q.DatabaseName))
                item.Fields.Add(("Database", q.DatabaseName));
            if (!string.IsNullOrEmpty(q.ProgramName))
                item.Fields.Add(("Program", q.ProgramName));
            if (!string.IsNullOrEmpty(q.QueryText))
                item.Fields.Add(("Query", TruncateText(q.QueryText)));
            item.Fields.Add(("CPU Time", $"{q.CpuTimeMs:N0} ms"));
            item.Fields.Add(("Reads", $"{q.Reads:N0}"));
            item.Fields.Add(("Writes", $"{q.Writes:N0}"));
            if (!string.IsNullOrEmpty(q.WaitType))
                item.Fields.Add(("Wait Type", q.WaitType));
            if (q.BlockingSessionId.HasValue && q.BlockingSessionId.Value > 0)
                item.Fields.Add(("Blocked By", $"Session #{q.BlockingSessionId.Value}"));

            context.Details.Add(item);
        }

        /* #1140: dedup key = query_hash (stable across literals/plans). Null hash -> no incident. */
        AlertIncidentRenderer.Apply(context, shown
            .Select(q => AlertFingerprint.ForKey(serverName, AlertFingerprint.Query, q.QueryHash ?? "",
                string.IsNullOrEmpty(q.DatabaseName) ? System.Array.Empty<string>() : new[] { q.DatabaseName }))
            .Where(i => i is not null).Select(i => i!).ToList());
        return context;
    }

    /* Returns the volumes whose free space is under the configured % or GB threshold (a 0 threshold
       disables that dimension), worst (lowest free %) first, so the alert names the tightest volume. */
    public static List<VolumeFreeSpaceInfo> GetBreachedVolumes(List<VolumeFreeSpaceInfo> volumes, double thresholdPercent, double thresholdGb)
    {
        double pct = thresholdPercent;
        double gb = thresholdGb;
        return volumes
            .Where(v => (pct > 0 && v.FreePercent < pct) || (gb > 0 && v.FreeGb < gb))
            .OrderBy(v => v.FreePercent)
            .ToList();
    }

    public static string FormatLowDiskThreshold(double thresholdPercent, double thresholdGb)
    {
        var parts = new List<string>();
        if (thresholdPercent > 0) parts.Add($"{thresholdPercent}%");
        if (thresholdGb > 0) parts.Add($"{thresholdGb} GB");
        return parts.Count > 0 ? string.Join(" / ", parts) : "—";
    }

    public static AlertContext? BuildVolumeFreeSpaceContext(string serverName, List<VolumeFreeSpaceInfo> volumes)
    {
        if (volumes.Count == 0) return null;

        var context = new AlertContext();
        var shown = volumes.GetRange(0, Math.Min(5, volumes.Count));
        foreach (var v in shown)
        {
            context.Details.Add(new AlertDetailItem
            {
                Heading = $"{v.MountPoint} — {v.FreePercent:F0}% Free",
                Fields = new()
                {
                    ("Free Space", $"{v.FreeGb:F1} GB"),
                    ("Total Size", $"{v.TotalMb / 1024.0:F1} GB"),
                    ("Used", $"{(v.TotalMb - v.FreeMb) / 1024.0:F1} GB")
                }
            });
        }

        /* #1140: dedup key per volume (the drive/mount point). */
        AlertIncidentRenderer.Apply(context, shown
            .Select(v => AlertFingerprint.ForKey(serverName, AlertFingerprint.Disk, v.MountPoint, new[] { v.MountPoint }))
            .Where(i => i is not null).Select(i => i!).ToList());
        return context;
    }

    public static AlertContext? BuildTempDbSpaceContext(TempDbSpaceInfo tempDb)
    {
        var context = new AlertContext();
        context.Details.Add(new AlertDetailItem
        {
            Heading = $"tempdb — {tempDb.UsedPercent:F0}% Used",
            Fields = new()
            {
                ("Total Reserved", $"{tempDb.TotalReservedMb:F0} MB"),
                ("Unallocated", $"{tempDb.UnallocatedMb:F0} MB"),
                ("User Objects", $"{tempDb.UserObjectReservedMb:F0} MB"),
                ("Internal Objects", $"{tempDb.InternalObjectReservedMb:F0} MB"),
                ("Version Store", $"{tempDb.VersionStoreReservedMb:F0} MB"),
                ("Top Consumer", tempDb.TopConsumerSessionId > 0
                    ? $"Session #{tempDb.TopConsumerSessionId} ({tempDb.TopConsumerMb:F0} MB)"
                    : "None")
            }
        });
        return context;
    }

    public static AlertContext? BuildAnomalousJobContext(string serverName, List<AnomalousJobInfo> jobs)
    {
        if (jobs.Count == 0) return null;

        var context = new AlertContext();
        var shown = jobs.GetRange(0, Math.Min(3, jobs.Count));
        foreach (var j in shown)
        {
            context.Details.Add(new AlertDetailItem
            {
                Heading = j.JobName,
                Fields = new()
                {
                    ("Current Duration", FormatDuration(j.CurrentDurationSeconds)),
                    ("Avg Duration", FormatDuration(j.AvgDurationSeconds)),
                    ("P95 Duration", FormatDuration(j.P95DurationSeconds)),
                    ("% of Average", j.PercentOfAverage.HasValue ? $"{j.PercentOfAverage:F0}%" : "N/A"),
                    ("Started", j.StartTime.ToString("yyyy-MM-dd HH:mm:ss"))
                }
            });
        }

        /* #1140: dedup key per job (job name, scoped to the instance via serverName). */
        AlertIncidentRenderer.Apply(context, shown
            .Select(j => AlertFingerprint.ForKey(serverName, AlertFingerprint.Job, j.JobName, new[] { j.JobName }))
            .Where(i => i is not null).Select(i => i!).ToList());
        return context;
    }

    public static AlertContext? BuildFailedJobContext(string serverName, List<FailedJobInfo> jobs)
    {
        if (jobs.Count == 0) return null;

        var context = new AlertContext();
        var shown = jobs.GetRange(0, Math.Min(5, jobs.Count));
        foreach (var j in shown)
        {
            var item = new AlertDetailItem { Heading = j.JobName, Fields = new() };
            item.Fields.Add(("Job", j.JobName));
            item.Fields.Add(("Failed At", j.RunDateTimeFormatted));
            if (j.StepId > 0 && !string.IsNullOrEmpty(j.StepName))
                item.Fields.Add(("Step", $"{j.StepId} — {j.StepName}"));
            if (!string.IsNullOrEmpty(j.Message))
                item.Fields.Add(("Message", TruncateText(j.Message, 300)));
            context.Details.Add(item);
        }

        /* #1140: dedup key per job (job name, scoped to the instance via serverName) — mirrors
           BuildAnomalousJobContext so two distinct failed jobs are distinct incidents under the
           #1154 per-fingerprint cooldown instead of coalescing on the metric key. */
        AlertIncidentRenderer.Apply(context, shown
            .Select(j => AlertFingerprint.ForKey(serverName, AlertFingerprint.Job, j.JobName, new[] { j.JobName }))
            .Where(i => i is not null).Select(i => i!).ToList());
        return context;
    }

    /// <summary>
    /// Flattens an <see cref="AlertContext"/> into the plain-text detail block persisted in alert
    /// history and rendered in plain-text notification bodies. Null when there is nothing to render.
    /// </summary>
    public static string? ContextToDetailText(AlertContext? context)
    {
        if (context == null || context.Details.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var detail in context.Details)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine(detail.Heading);
            foreach (var (label, value) in detail.Fields)
                sb.AppendLine($"  {label}: {value}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Collapses newlines to spaces, trims, and truncates to <paramref name="maxLength"/> with a
    /// trailing ellipsis — the single-line preview treatment for query text / job messages.
    /// </summary>
    public static string TruncateText(string text, int maxLength = 300)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m {seconds % 60}s";
        return $"{seconds / 3600}h {(seconds % 3600) / 60}m";
    }
}

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

namespace PerformanceMonitor.Analysis.Baselines;

/// <summary>
/// The store-agnostic baseline math shared by the two active providers (Lite <c>BaselineProvider</c>,
/// Darling <c>PgBaselineProvider</c>): the rolling-window / tier-selection thresholds, the tier
/// selection with hysteresis (<see cref="SelectBucket"/>), the pooled-statistics collapse
/// (hour-only / flat), and the bounded-metric dispersion floor. Each provider keeps only the
/// genuinely dialect-specific part — the per-metric baseline SQL and its parameter binding. Extracted
/// verbatim so the two apps' baseline behavior cannot drift.
/// </summary>
public static class BaselineMath
{
    /// <summary>Rolling window for baseline computation.</summary>
    public const int BaselineWindowDays = 30;

    /// <summary>Collapse to hour-only when full bucket has fewer than this many samples.</summary>
    public const int CollapseThreshold = 10;

    /// <summary>Restore to full bucket when sample count reaches this level (hysteresis).</summary>
    public const int RestoreThreshold = 15;

    /// <summary>
    /// Selects the most specific baseline bucket available for the (hour, day-of-week) coordinate,
    /// collapsing to less-specific tiers as sample density falls: Full (hour+dow) -> Hour-only ->
    /// Flat. Hysteresis keeps a Full bucket in the 10-14 sample band (RestoreThreshold vs
    /// CollapseThreshold) instead of flapping to hour-only. Pure over the precomputed bucket map,
    /// so it lives here rather than in either store provider.
    /// </summary>
    public static BaselineBucket SelectBucket(
        IReadOnlyDictionary<(int HourOfDay, int DayOfWeek), BaselineBucket> baselines,
        int hourOfDay,
        int dayOfWeek)
    {
        // Try full bucket (hour + day-of-week)
        var fullKey = (hourOfDay, dayOfWeek);
        if (baselines.TryGetValue(fullKey, out var fullBucket) && fullBucket.SampleCount >= RestoreThreshold)
            return fullBucket;

        // If full bucket exists but below restore threshold, check if it's above collapse threshold
        // (hysteresis: don't collapse if we're between 10-14 samples and were previously using full)
        if (fullBucket != null && fullBucket.SampleCount >= CollapseThreshold)
            return fullBucket;

        // Collapse to hour-only: aggregate all days for this hour
        var hourBuckets = baselines
            .Where(kvp => kvp.Key.HourOfDay == hourOfDay)
            .Select(kvp => kvp.Value)
            .ToList();

        if (hourBuckets.Count > 0)
        {
            var collapsed = CollapseToHourOnly(hourBuckets);
            if (collapsed.SampleCount >= CollapseThreshold)
                return collapsed;
        }

        // Collapse to flat: aggregate everything
        var allBuckets = baselines.Values.ToList();
        if (allBuckets.Count > 0)
        {
            var flat = CollapseToFlat(allBuckets);
            if (flat.SampleCount >= 3) // Minimum viable baseline
                return flat;
        }

        return BaselineBucket.Empty;
    }

    /// <summary>
    /// Bounded-metric absolute dispersion floor (see BaselineBucket.AbsStdDevFloor). Server-relative
    /// metrics have no universal floor and return 0. Tunable — calibrate on the SQL2025/HammerDB box.
    /// </summary>
    public static double AbsStdDevFloorFor(string metricName) => metricName switch
    {
        MetricNames.Cpu => 5.0,        // CPU utilization %
        MetricNames.Memory => 4.0,     // memory pressure %
        MetricNames.IoLatency => 2.5,  // I/O latency ms
        _ => 0.0,                       // batch/query-duration/sessions/waits/blocking/deadlock — server-relative
    };

    /// <summary>
    /// Collapses multiple day-of-week buckets for the same hour into a single
    /// hour-only bucket using pooled statistics.
    /// </summary>
    private static BaselineBucket CollapseToHourOnly(List<BaselineBucket> hourBuckets)
    {
        var totalSamples = hourBuckets.Sum(b => b.SampleCount);
        if (totalSamples == 0)
            return BaselineBucket.Empty;

        // Weighted mean across all day-of-week buckets for this hour
        var weightedMean = hourBuckets.Sum(b => b.Mean * b.SampleCount) / totalSamples;

        // Pooled standard deviation
        var pooledVariance = PoolVariance(hourBuckets, weightedMean);

        return new BaselineBucket
        {
            HourOfDay = hourBuckets[0].HourOfDay,
            DayOfWeek = -1, // Indicates hour-only
            Mean = weightedMean,
            StdDev = Math.Sqrt(pooledVariance),
            SampleCount = totalSamples,
            // Each calendar day lands in exactly one day-of-week bucket, so summing distinct-days
            // across the dow buckets for this hour is exact (no double-count).
            DistinctDays = hourBuckets.Sum(b => b.DistinctDays),
            AbsStdDevFloor = hourBuckets[0].AbsStdDevFloor,
            Tier = BaselineTier.HourOnly
        };
    }

    /// <summary>
    /// Collapses all buckets into a single flat baseline (equivalent to old 24h behavior).
    /// </summary>
    private static BaselineBucket CollapseToFlat(List<BaselineBucket> allBuckets)
    {
        var totalSamples = allBuckets.Sum(b => b.SampleCount);
        if (totalSamples == 0)
            return BaselineBucket.Empty;

        var weightedMean = allBuckets.Sum(b => b.Mean * b.SampleCount) / totalSamples;
        var pooledVariance = PoolVariance(allBuckets, weightedMean);

        return new BaselineBucket
        {
            HourOfDay = -1,
            DayOfWeek = -1,
            Mean = weightedMean,
            StdDev = Math.Sqrt(pooledVariance),
            SampleCount = totalSamples,
            // A calendar day recurs across the 24 hour buckets, so summing would double-count;
            // MAX is a ~5 ceiling (each (hour, dow) bucket holds at most ~5 same-weekday dates in a
            // 30-day window) — a cheap proxy that avoids an extra global DISTINCT-days query.
            DistinctDays = allBuckets.Max(b => b.DistinctDays),
            AbsStdDevFloor = allBuckets[0].AbsStdDevFloor,
            Tier = BaselineTier.Flat
        };
    }

    /// <summary>
    /// Computes pooled variance from multiple buckets, accounting for both
    /// within-bucket variance and between-bucket mean differences.
    /// </summary>
    private static double PoolVariance(List<BaselineBucket> buckets, double grandMean)
    {
        var totalSamples = buckets.Sum(b => b.SampleCount);
        if (totalSamples <= 1) return 0;

        double totalSumSq = 0;
        foreach (var b in buckets)
        {
            if (b.SampleCount <= 0) continue;
            // Within-bucket variance contribution
            totalSumSq += (b.StdDev * b.StdDev) * (b.SampleCount - 1);
            // Between-bucket mean difference contribution
            totalSumSq += b.SampleCount * (b.Mean - grandMean) * (b.Mean - grandMean);
        }

        return totalSumSq / (totalSamples - 1);
    }
}

/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

// Sub-namespace ON PURPOSE: the deprecated Full Dashboard keeps its OWN BaselineBucket/BaselineTier in
// PerformanceMonitorDashboard.Analysis, and several Dashboard files import PerformanceMonitor.Analysis
// alongside it. `using` is not recursive, so a plain `using PerformanceMonitor.Analysis;` does NOT pull
// in this .Baselines sub-namespace — which is what keeps the (untouched) Dashboard free of a CS0104
// ambiguous-reference break. Do NOT hoist these types up to PerformanceMonitor.Analysis. The two ACTIVE
// apps (Lite + Darling) consume this single shared copy; the Dashboard's frozen copy stays as-is.
namespace PerformanceMonitor.Analysis.Baselines;

/// <summary>
/// Represents the computed baseline statistics for a single time bucket. Shared by the two active
/// baseline providers (Lite <c>BaselineProvider</c>, Darling <c>PgBaselineProvider</c>) so the model
/// and its z-score floor semantics cannot drift; the store-specific baseline SQL stays per-provider.
/// </summary>
public class BaselineBucket
{
    public int HourOfDay { get; init; }
    public int DayOfWeek { get; init; }
    public double Mean { get; init; }
    public double StdDev { get; init; }
    public long SampleCount { get; init; }
    public BaselineTier Tier { get; init; }

    /// <summary>
    /// Distinct calendar days observed in this bucket — the baseline-QUALITY signal the old
    /// quantity-only warmup gate lacked (a bucket with many samples but few distinct days is one
    /// busy day, not a trend). Carried through collapse by CollapseToHourOnly (SUM — each calendar
    /// day lands in exactly one day-of-week bucket, so the sum is exact) and CollapseToFlat (MAX —
    /// a calendar day recurs across the 24 hour buckets; MAX is a ~5 ceiling, not the true pooled
    /// distinct-day count, but a cheap proxy that avoids a second global query).
    /// </summary>
    public long DistinctDays { get; init; }

    /// <summary>
    /// Absolute dispersion floor for BOUNDED metrics (CPU %, memory %, I/O ms) so a
    /// variance-collapsed baseline can't manufacture a giant z-score. 0 for server-relative
    /// metrics (batch/query-duration/sessions/waits/blocking), which have no universal floor and
    /// rely on the detector magnitude floors + the quality gate instead. Set per metric by the provider.
    /// </summary>
    public double AbsStdDevFloor { get; init; }

    // Baseline-quality tier gates (see IsTrustworthy). Day-mins are tier-aware: a Full (hour × dow)
    // bucket only sees ~5 same-weekday dates in a 30-day window, so its day-min is a modest 3. The Flat
    // tier's DistinctDays is a MAX-over-hour-buckets proxy (CollapseToFlat) capped at that SAME ~5
    // ceiling, so it can't demand more than a Full bucket — a >=15 floor was structurally unreachable and
    // left the Flat trust branch permanently dead, so match Full at 3. Sample-mins mirror the provider's
    // per-tier selection floors.
    private const long FullSampleMin = 10;
    private const long FullDayMin = 3;
    private const long HourOnlySampleMin = 10;
    private const long HourOnlyDayMin = 10;
    private const long FlatSampleMin = 3;
    private const long FlatDayMin = 3;

    public static BaselineBucket Empty => new()
    {
        HourOfDay = -1, DayOfWeek = -1, Mean = 0, StdDev = 0,
        SampleCount = 0, DistinctDays = 0, Tier = BaselineTier.Flat
    };

    /// <summary>
    /// Returns the effective stddev with a proportional minimum floor plus, for bounded metrics,
    /// an absolute floor — both prevent division-by-zero AND a variance-collapsed baseline from
    /// producing a giant z-score. When both mean and stddev are 0 (zero activity), returns 0 —
    /// callers should skip scoring (or fall back to the absolute-threshold path).
    /// </summary>
    public double EffectiveStdDev
    {
        get
        {
            if (Mean == 0 && StdDev <= 0) return 0; // Zero activity — skip scoring
            return Math.Max(Math.Max(StdDev, Mean * 0.01), AbsStdDevFloor);
        }
    }

    /// <summary>
    /// Whether this baseline is dense enough to trust a z-score / ratio against. Requires real
    /// dispersion, the tier's sample floor, AND enough DISTINCT days. A low-quality baseline is NOT
    /// silenced — the detector falls back to an absolute-threshold bar instead. This gate and the
    /// #1486 magnitude floors are COMPLEMENTARY, not both-mandatory: a trustworthy baseline trusts z
    /// with the magnitude floor as a sanity ceiling; an untrustworthy one fires only on the higher
    /// absolute bar — they must never AND into blindness on a young store.
    /// </summary>
    public bool IsTrustworthy
    {
        get
        {
            if (EffectiveStdDev <= 0) return false;
            var (sampleMin, dayMin) = Tier switch
            {
                BaselineTier.Full => (FullSampleMin, FullDayMin),
                BaselineTier.HourOnly => (HourOnlySampleMin, HourOnlyDayMin),
                _ => (FlatSampleMin, FlatDayMin),
            };
            return SampleCount >= sampleMin && DistinctDays >= dayMin;
        }
    }
}

public enum BaselineTier
{
    Full,     // hour + day-of-week (168 buckets)
    HourOnly, // hour only (24 buckets)
    Flat      // global mean/stddev
}

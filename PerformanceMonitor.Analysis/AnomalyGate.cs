/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitor.Analysis;

/// <summary>
/// The shared z-score anomaly gate — the one place the "interaction trap" decision lives so the
/// three triplicated detectors (Lite <c>AnomalyDetector</c>, Darling <c>PgAnomalyDetector</c>,
/// Dashboard <c>SqlServerAnomalyDetector</c>) cannot drift.
///
/// <para>
/// The #1486 absolute-magnitude floors and the baseline-quality gate (BaselineBucket.IsTrustworthy)
/// are COMPLEMENTARY, not both-mandatory — if they were AND-ed, a young store with a thin baseline
/// would go blind (the z-path is untrustworthy AND a trivial value clears no floor). Instead:
/// </para>
/// <list type="bullet">
///   <item>Trustworthy baseline → trust the z-score, with the magnitude floor as a sanity ceiling
///     (a huge z on a trivial value still doesn't fire).</item>
///   <item>Untrustworthy baseline → do NOT trust z (a z-score against a non-existent baseline is
///     meaningless); fire only on the HIGHER absolute-fallback bar. Not silence — absolute rules
///     preserve new-deployment coverage.</item>
/// </list>
/// The displayed sigma is always computed from whatever dispersion the baseline has (capped), so a
/// fallback-fired anomaly still shows how far above baseline it landed.
/// </summary>
public static class AnomalyGate
{
    /// <summary>Outcome of a z-vs-absolute-fallback decision.</summary>
    /// <param name="Fire">Whether the anomaly should be emitted.</param>
    /// <param name="Sigma">Capped deviation-in-sigmas for display (0 when the baseline has no dispersion).</param>
    /// <param name="LowQualityBaseline">True when the decision took the absolute-fallback path (thin baseline).</param>
    public readonly record struct ZDecision(bool Fire, double Sigma, bool LowQualityBaseline);

    /// <summary>
    /// Decides whether a z-score anomaly fires. Callers pass the baseline's <paramref name="mean"/>,
    /// its <paramref name="effectiveStdDev"/> (already floored), and its <paramref name="isTrustworthy"/>
    /// flag (BaselineBucket carries all three; it is triplicated, so this helper takes primitives).
    /// </summary>
    /// <param name="magnitudeFloor">#1486 sanity ceiling — in the z-path the observed peak must also
    /// clear this, so a giant z on a trivial value can't surface.</param>
    /// <param name="absoluteFallbackBar">The higher bar the observed peak must clear when the baseline
    /// is untrustworthy. Should be strictly above <paramref name="magnitudeFloor"/>.</param>
    /// <param name="sigmaCap">Display cap for the reported sigma (#1486's 25σ cap).</param>
    public static ZDecision EvaluateZScore(
        double mean,
        double effectiveStdDev,
        bool isTrustworthy,
        double peak,
        double deviationThreshold,
        double magnitudeFloor,
        double absoluteFallbackBar,
        double sigmaCap)
    {
        var sigma = effectiveStdDev > 0
            ? Math.Min((peak - mean) / effectiveStdDev, sigmaCap)
            : 0.0;

        if (isTrustworthy)
        {
            // effectiveStdDev > 0 is guaranteed when trustworthy (IsTrustworthy requires it).
            var deviation = (peak - mean) / effectiveStdDev;
            var fire = deviation >= deviationThreshold && peak >= magnitudeFloor;
            return new ZDecision(fire, Math.Min(deviation, sigmaCap), LowQualityBaseline: false);
        }

        // Untrustworthy baseline → absolute-threshold fallback (NOT silence).
        return new ZDecision(peak >= absoluteFallbackBar, sigma, LowQualityBaseline: true);
    }
}

/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// Delta computation contract for cumulative DMV counters. Definitions own WHICH fields are
/// delta'd, under WHICH counter-group names and keys, and with WHICH gap policy — that is
/// parity-critical monitoring brain. Hosts supply the stateful implementation (Lite:
/// <c>DeltaCalculator</c>'s in-memory per-server cache; Darling: its service-side equivalent).
/// Semantics contract (mirrors Lite's DeltaCalculator): first sighting returns 0 and baselines;
/// counter reset (decrease) returns 0; a gap larger than <paramref name="maxGapSeconds"/> returns
/// 0 to avoid inflated deltas after restarts.
/// </summary>
public interface ICollectorDeltaCalculator
{
    long CalculateDelta(int serverId, string collectorName, string key, long currentValue,
        DateTime? collectionTime = null, int maxGapSeconds = 0);

    long CalculateDeltaWithInterval(int serverId, string collectorName, string key, long currentValue,
        out int intervalSeconds, DateTime? collectionTime = null, int maxGapSeconds = 0);
}

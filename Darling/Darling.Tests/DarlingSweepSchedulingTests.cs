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
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1553 (Tier 2 of the 24-server field-incident response): the bounded-parallel fire-and-track sweep's
/// pure, testable seams — the cadence-jitter phase offset and the hardcoded concurrency cap. The stateful
/// pieces (Retired containment, the outer-thread-owned skip-log) live in a private async body with heavy
/// dependencies and are asserted by full-diff review, not contorted into unit tests (the plan's simplicity
/// rule); these two seams are the ones with a clean, dependency-free surface.
/// </summary>
public sealed class DarlingSweepSchedulingTests
{
    /* The cadence periods the jitter is applied against (seconds): 30/60/300/1800 span the collector
       frequencies, 150 is the fixed post-connect analysis window (the plan's jitter site 3). Exactly the
       spread Section C item 1 pins. */
    private static readonly int[] Periods = { 30, 60, 150, 300, 1800 };

    /// <summary>
    /// A spread of realistic, FNV-shaped server ids — the SAME derivation the loop keys on
    /// (<see cref="ServerIdHelper.GetDeterministicHashCode"/>, a process-independent FNV-1a hash), over real
    /// server-name shapes (boxes, named instances, Azure SQL DB storage names). Full signed-int range: roughly
    /// half are negative, which is exactly why <see cref="DarlingWorker.CadencePhaseOffset"/> casts to uint.
    /// </summary>
    private static readonly int[] RealisticServerIds =
        new[]
        {
            "PROD-SQL-01",
            "PROD-SQL-02",
            "PROD-SQL-03",
            "SQL2022-CORE\\INSTANCE1",
            "SQL2019-RPT\\REPORTING",
            "azure-eastus.database.windows.net:SalesDb",
            "azure-eastus.database.windows.net:InventoryDb",
            "10.0.14.72",
            "10.0.14.73:RO",
            "hammerdb-bench-01",
            "dr-secondary-west",
            "analytics-warehouse-primary",
        }
        .Select(ServerIdHelper.GetDeterministicHashCode)
        .ToArray();

    /// <summary>
    /// Deterministic and restart-stable: the offset is a PURE function of (id, period) with no
    /// <see cref="Random"/> and no process-specific state, so a service restart re-derives the SAME phase for
    /// the same server and does not re-herd the fleet (the whole point of the jitter). Repeated calls must be
    /// byte-identical; the ids themselves come from the process-independent FNV helper.
    /// </summary>
    [Fact]
    public void CadencePhaseOffset_IsDeterministic_ForSameInputs()
    {
        foreach (var id in RealisticServerIds)
        {
            foreach (var period in Periods)
            {
                var first = DarlingWorker.CadencePhaseOffset(id, period);
                var second = DarlingWorker.CadencePhaseOffset(id, period);
                Assert.Equal(first, second);
            }
        }
    }

    /// <summary>
    /// Pins the exact formula <c>(uint)serverId % periodSeconds</c> seconds, INCLUDING the uint cast that keeps a
    /// NEGATIVE FNV id (GetDeterministicHashCode returns a signed int) mapped into [0, period). A signed modulo
    /// would produce a negative offset and pull the collector's first due time into the PAST — re-herding it
    /// exactly like no jitter at all. Boundary ids exercise the cast directly.
    /// </summary>
    [Fact]
    public void CadencePhaseOffset_IsUnsignedModulo_MappingNegativeIdsIntoRange()
    {
        int[] boundaryIds = { 0, 1, 61, -1, -61, -12345, int.MaxValue, int.MinValue };
        foreach (var id in boundaryIds)
        {
            foreach (var period in Periods)
            {
                var expected = TimeSpan.FromSeconds((uint)id % period);
                var actual = DarlingWorker.CadencePhaseOffset(id, period);
                Assert.Equal(expected, actual);
                /* A negative id must never yield a negative (past) offset. */
                Assert.True(actual >= TimeSpan.Zero, $"offset for id {id}, period {period} must be non-negative");
            }
        }
    }

    /// <summary>
    /// Bounded within [0, period) for every realistic FNV id and every period: the phase can never push a due
    /// time a full cadence out (that would skip a cycle) nor before now (that would re-herd). This is the only
    /// "spread" property the plan pins — deliberately NOT a distribution or collision pin (pigeonhole makes
    /// those unachievable and brittle; reviewed out twice).
    /// </summary>
    [Fact]
    public void CadencePhaseOffset_IsBounded_WithinZeroInclusiveToPeriodExclusive()
    {
        foreach (var id in RealisticServerIds)
        {
            foreach (var period in Periods)
            {
                var offset = DarlingWorker.CadencePhaseOffset(id, period);
                Assert.True(offset >= TimeSpan.Zero, $"offset for id {id}, period {period} must be >= 0");
                Assert.True(offset < TimeSpan.FromSeconds(period), $"offset for id {id}, period {period} must be < the period");
            }
        }
    }

    /// <summary>
    /// The herd is actually broken: across the ≥10 distinct realistic ids, the offsets for a given period are
    /// NOT all identical (at least two distinct phases). This is the minimal "de-herded" pin — again NOT a
    /// distribution claim, just that a single shared instant is not what the function produces.
    /// </summary>
    [Fact]
    public void CadencePhaseOffset_IsNotAllIdentical_AcrossDistinctIds_PerPeriod()
    {
        Assert.True(RealisticServerIds.Length >= 10, "need ≥10 distinct ids to make the not-all-identical pin meaningful");

        foreach (var period in Periods)
        {
            var distinctOffsets = RealisticServerIds
                .Select(id => DarlingWorker.CadencePhaseOffset(id, period))
                .Distinct()
                .Count();

            Assert.True(distinctOffsets > 1, $"period {period}: all {RealisticServerIds.Length} ids phased to the same instant — the fleet would still fire in lockstep");
        }
    }

    /// <summary>
    /// A non-positive period yields no offset (TimeSpan.Zero) rather than a divide-by-zero or an undefined
    /// modulo — guards the on-load / recompute call sites where a frequency could in principle be zero and keeps
    /// the function total for callers.
    /// </summary>
    [Fact]
    public void CadencePhaseOffset_NonPositivePeriod_IsZero()
    {
        foreach (var id in RealisticServerIds)
        {
            Assert.Equal(TimeSpan.Zero, DarlingWorker.CadencePhaseOffset(id, 0));
            Assert.Equal(TimeSpan.Zero, DarlingWorker.CadencePhaseOffset(id, -1));
            Assert.Equal(TimeSpan.Zero, DarlingWorker.CadencePhaseOffset(id, -3600));
        }
    }

    /// <summary>
    /// The bounded per-server sweep concurrency is pinned at 4 (a cheap drift tripwire). Hardcoded by design —
    /// defaults-over-config, no control-plane knob: 4 clears a 24-server worst case in ~6 waves while the 120s
    /// analysis budget stays de-clustered by the cadence jitter (the plan's N=4 rationale). A change here is a
    /// deliberate capacity decision, not an incidental edit, so it must break this test first.
    /// </summary>
    [Fact]
    public void MaxConcurrentServerSweeps_IsFour()
    {
        Assert.Equal(4, DarlingWorker.MaxConcurrentServerSweeps);
    }
}

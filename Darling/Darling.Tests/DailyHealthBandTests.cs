/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using PerformanceMonitor.Common;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Table-driven tests for the shared Performance Calendar day-banding (<see cref="DailyHealthBandCalculator"/>).
/// This is the one classification both apps color their month heatmap by, so it is pinned here (Darling) and,
/// identically, in Lite.Tests. Covers the no-data / healthy / warning / critical tiers, first-match-wins
/// priority, the threshold boundaries, and the label / brush-key / tooltip mappings.
/// </summary>
public class DailyHealthBandTests
{
    private static DailyHealthSignals Signals(
        bool hasData = true, long deadlocks = 0, long collectionErrors = 0, long highCpu = 0,
        long blocking = 0, long memPressure = 0, long memCritical = 0, long alerts = 0) => new()
    {
        HasData = hasData,
        Deadlocks = deadlocks,
        CollectionErrors = collectionErrors,
        HighCpuEvents = highCpu,
        BlockingEvents = blocking,
        MemoryPressureEvents = memPressure,
        MemoryCriticalEvents = memCritical,
        AlertCount = alerts,
    };

    [Fact]
    public void NoCollection_IsNoData_RegardlessOfOtherCounts()
    {
        var s = Signals(hasData: false, deadlocks: 99, collectionErrors: 99, highCpu: 99, blocking: 99, memCritical: 99, alerts: 99);
        Assert.Equal(DailyHealthBand.NoData, DailyHealthBandCalculator.Classify(s));
    }

    [Fact]
    public void Collected_AndNothingElevated_IsHealthy()
    {
        Assert.Equal(DailyHealthBand.Healthy, DailyHealthBandCalculator.Classify(Signals()));
    }

    [Theory]
    [InlineData("deadlock", 1, 0, 0, 0, 0, 0)]
    [InlineData("collection-error", 0, 1, 0, 0, 0, 0)]
    [InlineData("memory-critical", 0, 0, 0, 0, 1, 0)]
    [InlineData("sustained-cpu", 0, 0, 6, 0, 0, 0)]
    [InlineData("heavy-blocking", 0, 0, 0, 11, 0, 0)]
    public void CriticalTriggers_EachAloneIsCritical(string _, long deadlocks, long collErrors, long highCpu, long blocking, long memCritical, long alerts)
    {
        var s = Signals(deadlocks: deadlocks, collectionErrors: collErrors, highCpu: highCpu, blocking: blocking, memCritical: memCritical, alerts: alerts);
        Assert.Equal(DailyHealthBand.Critical, DailyHealthBandCalculator.Classify(s));
    }

    [Theory]
    [InlineData("moderate-cpu", 3, 0, 0, 0)]
    [InlineData("some-blocking", 0, 5, 0, 0)]
    [InlineData("memory-pressure", 0, 0, 1, 0)]
    [InlineData("alert", 0, 0, 0, 1)]
    public void WarningTriggers_EachAloneIsWarning(string _, long highCpu, long blocking, long memPressure, long alerts)
    {
        var s = Signals(highCpu: highCpu, blocking: blocking, memPressure: memPressure, alerts: alerts);
        Assert.Equal(DailyHealthBand.Warning, DailyHealthBandCalculator.Classify(s));
    }

    [Fact]
    public void CriticalBeatsWarning_WhenBothPresent()
    {
        var s = Signals(deadlocks: 1, highCpu: 3, alerts: 4);
        Assert.Equal(DailyHealthBand.Critical, DailyHealthBandCalculator.Classify(s));
    }

    [Theory]
    [InlineData(5, DailyHealthBand.Warning)]
    [InlineData(6, DailyHealthBand.Critical)]
    public void HighCpu_CriticalThreshold_IsSixSamples(long highCpu, DailyHealthBand expected)
    {
        Assert.Equal(expected, DailyHealthBandCalculator.Classify(Signals(highCpu: highCpu)));
    }

    [Theory]
    [InlineData(10, DailyHealthBand.Warning)]
    [InlineData(11, DailyHealthBand.Critical)]
    public void Blocking_CriticalThreshold_IsElevenEvents(long blocking, DailyHealthBand expected)
    {
        Assert.Equal(expected, DailyHealthBandCalculator.Classify(Signals(blocking: blocking)));
    }

    [Fact]
    public void Thresholds_AreOverridable()
    {
        var strict = new DailyHealthThresholds { HighCpuCriticalSamples = 3 };
        Assert.Equal(DailyHealthBand.Critical, DailyHealthBandCalculator.Classify(Signals(highCpu: 3), strict));
        Assert.Equal(DailyHealthBand.Warning, DailyHealthBandCalculator.Classify(Signals(highCpu: 3)));
    }

    [Theory]
    [InlineData(DailyHealthBand.NoData, "No Data", "ForegroundMutedBrush")]
    [InlineData(DailyHealthBand.Healthy, "Healthy", "SuccessBrush")]
    [InlineData(DailyHealthBand.Warning, "Warning", "WarningBrush")]
    [InlineData(DailyHealthBand.Critical, "Critical", "ErrorBrush")]
    public void Label_And_BrushKey_MapEachBand(DailyHealthBand band, string label, string brushKey)
    {
        Assert.Equal(label, DailyHealthBandCalculator.Label(band));
        Assert.Equal(brushKey, DailyHealthBandCalculator.BrushKey(band));
    }

    [Fact]
    public void Describe_NoData_And_NoIssues_And_MultiSignal()
    {
        Assert.Equal("No data collected.", DailyHealthBandCalculator.Describe(Signals(hasData: false)));
        Assert.Equal("No issues detected.", DailyHealthBandCalculator.Describe(Signals()));

        var described = DailyHealthBandCalculator.Describe(Signals(deadlocks: 2, blocking: 1, alerts: 3));
        Assert.Contains("2 deadlocks", described);
        Assert.Contains("1 blocking event", described);
        Assert.Contains("3 alerts", described);
    }

    [Fact]
    public void Describe_MemoryPressure_DoesNotDoubleCountSevere()
    {
        // 3 pressure events, 1 severe -> "1 severe" + "2 (non-severe)", never "3 memory-pressure events".
        var described = DailyHealthBandCalculator.Describe(Signals(memPressure: 3, memCritical: 1));
        Assert.Contains("1 severe memory-pressure event", described);
        Assert.Contains("2 memory-pressure events", described);
        Assert.DoesNotContain("3 memory-pressure events", described);
    }
}

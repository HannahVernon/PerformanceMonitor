/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using PerformanceMonitor.Common;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the shared Default Trace significant-set gate (PerformanceMonitor.Common) — the ONE classifier both
/// the viewer's System Events surface and the headless get_default_trace_events MCP tool reference. The
/// collector already curated the event set server-side; the ONE refinement this layer adds is the ErrorLog
/// severity floor, so every non-ErrorLog curated category is significant as collected and only ErrorLog is
/// severity-gated (a null severity kept, not dropped).
/// </summary>
public sealed class DefaultTraceEventSignificanceTests
{
    [Theory]
    [InlineData("Server Memory Change", DefaultTraceEventCategory.MemoryChange)]
    [InlineData("Data File Auto Grow", DefaultTraceEventCategory.AutoGrowShrink)]
    [InlineData("Log File Auto Grow", DefaultTraceEventCategory.AutoGrowShrink)]
    [InlineData("Data File Auto Shrink", DefaultTraceEventCategory.AutoGrowShrink)]
    [InlineData("Log File Auto Shrink", DefaultTraceEventCategory.AutoGrowShrink)]
    [InlineData("ErrorLog", DefaultTraceEventCategory.ErrorLog)]
    [InlineData("Object:Created", DefaultTraceEventCategory.SchemaChange)]
    [InlineData("Object:Altered", DefaultTraceEventCategory.SchemaChange)]
    [InlineData("Object:Deleted", DefaultTraceEventCategory.SchemaChange)]
    [InlineData("Audit Change Audit Event", DefaultTraceEventCategory.SecurityAudit)]
    [InlineData("Audit DBCC Event", DefaultTraceEventCategory.SecurityAudit)]
    [InlineData("Audit Server Alter Trace Event", DefaultTraceEventCategory.SecurityAudit)]
    [InlineData("Some Unknown Event", DefaultTraceEventCategory.Other)]
    [InlineData("", DefaultTraceEventCategory.Other)]
    [InlineData(null, DefaultTraceEventCategory.Other)]
    public void Classify_MapsEventNameToCategory(string? eventName, DefaultTraceEventCategory expected)
    {
        Assert.Equal(expected, DefaultTraceEventSignificance.Classify(eventName));
    }

    [Fact]
    public void SevereErrorLogFloor_IsSixteen()
    {
        Assert.Equal(16, DefaultTraceEventSignificance.SevereErrorLogMinSeverity);
    }

    [Theory]
    [InlineData(19, true)]   /* fatal-range error: significant */
    [InlineData(16, true)]   /* exactly the floor: significant */
    [InlineData(15, false)]  /* below the floor: routine, dropped */
    [InlineData(0, false)]   /* informational log write: dropped */
    [InlineData(null, true)] /* no severity node: kept rather than silently dropped */
    public void IsSignificant_ErrorLog_GatedBySeverity(int? severity, bool expected)
    {
        Assert.Equal(expected, DefaultTraceEventSignificance.IsSignificant("ErrorLog", severity));
        Assert.Equal(expected, DefaultTraceEventSignificance.IsSignificant(DefaultTraceEventCategory.ErrorLog, severity));
    }

    [Theory]
    [InlineData("Data File Auto Grow")]
    [InlineData("Object:Altered")]
    [InlineData("Audit DBCC Event")]
    [InlineData("Server Memory Change")]
    public void IsSignificant_NonErrorLogCategories_AlwaysSignificant_RegardlessOfSeverity(string eventName)
    {
        /* These carry no severity, but even a below-floor value must not gate them — only ErrorLog is gated. */
        Assert.True(DefaultTraceEventSignificance.IsSignificant(eventName, null));
        Assert.True(DefaultTraceEventSignificance.IsSignificant(eventName, 0));
    }
}

/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Common;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Behavioral pin for the shared <see cref="SessionStatsSummary"/> — the Session Stats summary-strip projection
/// that used to be the byte-identical <c>FormatSessionSummary</c> in Lite (<c>ServerTab.SessionStats.cs</c>) and
/// the Darling viewer (<c>ViewerServerTab.SessionStats.cs</c>). Drives the shared entry point directly over a
/// stub <see cref="ISessionStatsPoint"/> — proving the collapsed shaping still renders "name (count)" for a
/// named Top Application / Top Host, degrades a missing name (null or empty) to "N/A", keeps the distinct-
/// databases count independently, and falls a null connection count back to 0. Pure — no DuckDB, no WPF.
/// </summary>
public class SessionStatsSummaryTests
{
    /// <summary>A zeroed snapshot; each test sets only the summary fields it exercises (the seven status
    /// counts are irrelevant to the summary strip).</summary>
    private sealed class StubPoint : ISessionStatsPoint
    {
        public DateTime CollectionTime { get; init; }
        public int TotalSessions { get; init; }
        public int RunningSessions { get; init; }
        public int SleepingSessions { get; init; }
        public int BackgroundSessions { get; init; }
        public int DormantSessions { get; init; }
        public int IdleSessionsOver30Min { get; init; }
        public int SessionsWaitingForMemory { get; init; }
        public int DatabasesWithConnections { get; init; }
        public string? TopApplicationName { get; init; }
        public int? TopApplicationConnections { get; init; }
        public string? TopHostName { get; init; }
        public int? TopHostConnections { get; init; }
    }

    [Fact]
    public void Format_Null_YieldsNAForAllThree()
    {
        var (topApp, topHost, databases) = SessionStatsSummary.Format(null);

        Assert.Equal("N/A", topApp);
        Assert.Equal("N/A", topHost);
        Assert.Equal("N/A", databases);
    }

    [Fact]
    public void Format_FullPoint_RendersNameAndConnectionCount()
    {
        var point = new StubPoint
        {
            TopApplicationName = "SQLAgent",
            TopApplicationConnections = 42,
            TopHostName = "APPSRV01",
            TopHostConnections = 18,
            DatabasesWithConnections = 9,
        };

        var (topApp, topHost, databases) = SessionStatsSummary.Format(point);

        Assert.Equal("SQLAgent (42)", topApp);
        Assert.Equal("APPSRV01 (18)", topHost);
        Assert.Equal("9", databases);
    }

    [Fact]
    public void Format_MissingApplicationOrHost_YieldsNA_ButKeepsDatabaseCount()
    {
        var point = new StubPoint
        {
            TopApplicationName = null,
            TopApplicationConnections = null,
            TopHostName = "",
            TopHostConnections = null,
            DatabasesWithConnections = 3,
        };

        var (topApp, topHost, databases) = SessionStatsSummary.Format(point);

        Assert.Equal("N/A", topApp);
        Assert.Equal("N/A", topHost);
        Assert.Equal("3", databases);
    }

    [Fact]
    public void Format_NamedButNullConnectionCount_FallsBackToZero()
    {
        /* The `?? 0` branch: a named app/host whose connection count is null still renders "name (0)"
           (no existing test reaches this — the missing-name case short-circuits to "N/A" before the count). */
        var point = new StubPoint
        {
            TopApplicationName = "AppOnly",
            TopApplicationConnections = null,
            TopHostName = "HostOnly",
            TopHostConnections = null,
            DatabasesWithConnections = 1,
        };

        var (topApp, topHost, _) = SessionStatsSummary.Format(point);

        Assert.Equal("AppOnly (0)", topApp);
        Assert.Equal("HostOnly (0)", topHost);
    }
}

/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Darling.Service.Mcp;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1562 (live web dashboard toggle): the web host's supervisor decision table and the worker→host state
/// seam — the exact twin of <see cref="DarlingMcpSupervisorTests"/>. The viewer's Settings toggle writes
/// config_service.web_enabled / web_port; the worker publishes the live values to <see cref="WebRuntimeState"/>
/// on every reload; the host's supervisor polls and starts/stops/rebinds WITHOUT a service restart. These pins
/// hold the decision table and the seam's coherence; the Kestrel lifecycle itself is exercised by field use.
/// </summary>
public sealed class DarlingWebSupervisorTests
{
    [Theory]
    /* not running: start only when enabled */
    [InlineData(false, 0, false, 5153, DarlingWebHostService.WebSupervisorAction.None)]
    [InlineData(false, 0, true, 5153, DarlingWebHostService.WebSupervisorAction.Start)]
    /* running: stop on disable — the no-restart kill switch, network-exposed included */
    [InlineData(true, 5153, false, 5153, DarlingWebHostService.WebSupervisorAction.Stop)]
    /* running + enabled: rebind only on a port change */
    [InlineData(true, 5153, true, 5153, DarlingWebHostService.WebSupervisorAction.None)]
    [InlineData(true, 5153, true, 5199, DarlingWebHostService.WebSupervisorAction.Restart)]
    public void DecideWebAction_CoversTheWholeTable(
        bool running, int runningPort, bool enabled, int desiredPort, DarlingWebHostService.WebSupervisorAction expected)
    {
        Assert.Equal(expected, DarlingWebHostService.DecideWebAction(running, runningPort, enabled, desiredPort));
    }

    [Fact]
    public void WebRuntimeState_UnpublishedReadsNull_ThenLatestCoherentSnapshot()
    {
        var state = new WebRuntimeState();

        /* Pre-publish: the host falls back to the FILE values. */
        Assert.Null(state.Read());

        state.Publish(enabled: true, port: 5153);
        var first = state.Read();
        Assert.NotNull(first);
        Assert.True(first!.Enabled);
        Assert.Equal(5153, first.Port);

        /* A re-publish swaps ONE immutable snapshot reference — a reader can never see a torn
           (old Enabled, new Port) mix. */
        state.Publish(enabled: false, port: 5199);
        var second = state.Read();
        Assert.NotNull(second);
        Assert.False(second!.Enabled);
        Assert.Equal(5199, second.Port);
        Assert.NotSame(first, second);
    }

    /// <summary>Cadence tripwires: the supervisor reacts within ~one reload beacon (5s poll) and a persistent
    /// start failure retries on a calm 30s backoff, not every tick — pinned equal to the MCP host's cadences.</summary>
    [Fact]
    public void SupervisorCadences_ArePinned()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), DarlingWebHostService.SupervisorPollInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), DarlingWebHostService.FailedStartBackoff);
    }
}

/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using PerformanceMonitorLite.Models;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// Regression tests for #1506.
///
/// Reported by a user monitoring Azure SQL DB whose public IP rotates daily, so the server's
/// firewall rule stops matching and connections are rejected with error 40615. Restoring the rule
/// brought the server back, but collection stayed broken until Lite was restarted.
///
/// The cause: 40615 (firewall) and 40613 (database temporarily unavailable) were classified as
/// "this login cannot read master" — a verdict that was cached per server and never expired. One
/// unlucky moment during an outage permanently demoted a healthy server to single-database
/// collection, or, when the connection had no explicit database, to collecting nothing at all.
///
/// The fixes pinned here: reachability errors are not permission errors; the verdict expires; and a
/// server coming back from an outage discards it immediately.
///
/// Only in-memory state is touched, so the service is constructed with null dependencies — the same
/// approach as <see cref="XeSessionHealthTests"/>.
/// </summary>
public class AzureMasterFallbackTests
{
    private const int ServerId = 7;

    private static RemoteCollectorService CreateService() =>
        new(duckDb: null!, serverManager: null!, scheduleManager: null!);

    private static ServerConnection Server(string name = "azure-logical-server") =>
        new() { ServerName = name, DisplayName = name };

    /// <summary>
    /// The bug itself. 40615 is "client with IP address ... is not allowed to access the server":
    /// a firewall rejection at the logical server, which says nothing about master, and which no
    /// fallback can route around because the same rule blocks every other database too.
    /// 40613 is "not currently available, please retry later" — transient on its face.
    /// </summary>
    [Theory]
    [InlineData(40615)] // Cannot open server — client IP not allowed (firewall)
    [InlineData(40613)] // Database not currently available — retry later
    public void Reachability_Errors_Are_Not_Master_Access_Denied(int errorNumber)
    {
        Assert.False(RemoteCollectorService.IsMasterAccessDeniedError(errorNumber));
    }

    /// <summary>
    /// #857 must keep working: a contained user that exists only in a user database really does get
    /// these when it opens master, and single-database fallback is the right answer for them.
    /// </summary>
    [Theory]
    [InlineData(229)]   // Permission denied on object
    [InlineData(230)]   // Permission denied on column
    [InlineData(916)]   // Principal cannot access the database under the current security context
    [InlineData(4060)]  // Cannot open database requested by the login
    [InlineData(18456)] // Login failed for user
    public void Permission_Errors_Are_Still_Master_Access_Denied(int errorNumber)
    {
        Assert.True(RemoteCollectorService.IsMasterAccessDeniedError(errorNumber));
    }

    /// <summary>
    /// The invariant that would have caught this at authoring time: no error number may be both
    /// "retry, this is temporary" and "stop, this is permanent". 40613 was on both lists.
    /// </summary>
    [Fact]
    public void No_Error_Is_Both_Transient_And_Permanently_Fatal()
    {
        var contradictory = RetryHelper.TransientErrorNumbers
            .Where(RemoteCollectorService.IsMasterAccessDeniedError)
            .ToList();

        Assert.True(
            contradictory.Count == 0,
            $"Error(s) {string.Join(", ", contradictory)} are treated as retryable-transient AND as a " +
            "permanent master-access verdict. Pick one — see #1506.");
    }

    [Fact]
    public void Fresh_Verdict_Suppresses_Re_Probing_Master()
    {
        var service = CreateService();

        service.MarkMasterInaccessible(ServerId);

        /* #857: a login that genuinely cannot read master shouldn't retry it every cycle. */
        Assert.True(service.IsMasterProbeThrottled(ServerId));
    }

    [Fact]
    public void Verdict_Expires_So_A_Server_Recovers_Without_A_Restart()
    {
        var service = CreateService();

        service.MarkMasterInaccessible(ServerId, DateTime.UtcNow.AddMinutes(-16));

        /* Past the 15-minute recheck window: probe master again rather than trusting a stale verdict.
           This is the backstop for an outage that never produced a clean offline->online transition. */
        Assert.False(service.IsMasterProbeThrottled(ServerId));
    }

    [Fact]
    public void Server_Returning_From_An_Outage_Discards_The_Verdict_Immediately()
    {
        var service = CreateService();
        var server = Server();
        var serverId = RemoteCollectorService.GetServerId(server);

        service.MarkMasterInaccessible(serverId);
        service.NoteServerOffline(server);
        service.NoteServerOnline(server);

        /* The reporter's exact path: firewall rule restored, server answers again, and database-scoped
           collection resumes on the next cycle instead of waiting for an app restart. */
        Assert.False(service.IsMasterProbeThrottled(serverId));
    }

    [Fact]
    public void Verdict_Survives_A_Server_That_Was_Never_Offline()
    {
        var service = CreateService();
        var server = Server();
        var serverId = RemoteCollectorService.GetServerId(server);

        service.MarkMasterInaccessible(serverId);
        service.NoteServerOnline(server);

        /* The #857 steady state: reachable server, login simply lacks master rights. There was no
           outage to invalidate the verdict, so it stands and master is not re-probed every minute. */
        Assert.True(service.IsMasterProbeThrottled(serverId));
    }

    [Fact]
    public void Offline_Tracking_Is_Per_Server()
    {
        var service = CreateService();
        var recovered = Server("recovered-server");
        var stillDenied = Server("still-denied-server");

        var recoveredId = RemoteCollectorService.GetServerId(recovered);
        var stillDeniedId = RemoteCollectorService.GetServerId(stillDenied);

        service.MarkMasterInaccessible(recoveredId);
        service.MarkMasterInaccessible(stillDeniedId);

        service.NoteServerOffline(recovered);
        service.NoteServerOnline(recovered);

        Assert.False(service.IsMasterProbeThrottled(recoveredId));
        Assert.True(service.IsMasterProbeThrottled(stillDeniedId));
    }
}

/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitorLite.Services;

/// <summary>The connection transition one poll observed for one server.</summary>
public enum ConnectionAlertEdge
{
    /// <summary>No transition to announce: the steady states (online→online, offline→offline) and the
    /// first-ever observation, which is a silent baseline.</summary>
    None,

    /// <summary>online → offline.</summary>
    Lost,

    /// <summary>offline → online.</summary>
    Restored
}

/// <summary>
/// The connection-alert edge trigger (#1506), lifted out of <c>MainWindow.CheckConnectionsAndNotify</c>'s
/// inline condition so the property that actually matters is testable: an alert fires ONCE per transition,
/// never once per poll. A server that is down for eight hours is observed offline hundreds of times, but
/// every observation after the first is offline→offline — <see cref="ConnectionAlertEdge.None"/> — so it
/// produces one "Server Unreachable", not one per cycle.
///
/// <para>Semantics are the pre-existing ones (previous-state dictionary, skip the first observation) and
/// match the Dashboard's skip-first-check and Darling's <c>Unknown/Online/Offline</c> machine: a first
/// sighting only establishes the baseline, so an app that starts up with a server already down does not
/// immediately alert about a state it never saw change.</para>
/// </summary>
public static class ConnectionEdgeDetector
{
    /// <param name="previous">The last observed state, or null when this server has never been observed.</param>
    /// <param name="current">The state this poll observed.</param>
    public static ConnectionAlertEdge Detect(bool? previous, bool current) => previous switch
    {
        null => ConnectionAlertEdge.None,
        true when !current => ConnectionAlertEdge.Lost,
        false when current => ConnectionAlertEdge.Restored,
        _ => ConnectionAlertEdge.None
    };
}

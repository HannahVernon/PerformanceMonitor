/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;

namespace PerformanceMonitor.Common;

/// <summary>
/// The single source of truth for how a SQL Server error number is judged.
///
/// Two questions are asked about errors across the products, and they must never disagree:
///
///   "Is this temporary — should we retry?"          <see cref="IsTransient"/>
///   "Is this permanent — should we give up?"        <see cref="IsMasterAccessDenied"/>
///
/// The two sets MUST be disjoint. Issue #1506 is what happens when they aren't: error 40613
/// ("database not currently available, please retry later") sat on the retryable list AND on the
/// give-up list at the same time, so the same error meant both "this will pass" and "this is
/// forever" depending on which code path saw it first.
///
/// These lists lived separately in Lite and in Darling, which is how the bug was ported from one
/// to the other. They live here now so that cannot happen again, and so the disjointness invariant
/// can be asserted from both test suites. Plain integers, no SqlClient dependency, so every project
/// can reference this regardless of how it talks to SQL Server.
/// </summary>
public static class SqlErrorClassification
{
    /// <summary>
    /// Errors that are temporary by nature: the operation may well succeed if tried again.
    /// </summary>
    public static readonly IReadOnlySet<int> TransientErrorNumbers = new HashSet<int>
    {
        -2,     // Timeout
        -1,     // General network error
        2,      // Timeout (alternative)
        53,     // Named pipe / network not found
        233,    // Connection closed
        10053,  // Transport-level error (connection was forcibly closed)
        10054,  // Connection reset by peer
        10060,  // Connection timed out
        10061,  // Connection refused
        40143,  // Azure SQL transient
        40197,  // Azure SQL transient - service encountered an error
        40501,  // Azure SQL - service is currently busy
        40613,  // Azure SQL - database not currently available
        49918,  // Azure SQL - not enough resources to process request
        49919,  // Azure SQL - cannot process create/update request
        49920   // Azure SQL - cannot process request (too many operations)
    };

    /// <summary>
    /// Errors meaning a login genuinely cannot read <c>master</c>, so database-scoped collectors on
    /// Azure SQL DB must fall back to the connection's own catalog. This is the #857 case: a contained
    /// user that exists only inside a user database really does get these when it opens master.
    ///
    /// Every entry must describe the login's RIGHTS, never the server's REACHABILITY. Two reachability
    /// errors used to be here and caused #1506:
    ///
    ///   40613 — "not currently available, please retry later". Transient on its face, and listed as
    ///           retryable above — the contradiction this class exists to prevent.
    ///   40615 — "client with IP address ... is not allowed to access the server". A firewall rejection
    ///           at the logical server. It says nothing about master specifically, and falling back to
    ///           a user database is futile because the same rule blocks that connection too.
    ///
    /// Reported by a user whose public IP rotated daily: each rotation rejected the connection, the
    /// rejection was misread as "this login may not read master", and the verdict was cached — so
    /// collection stayed broken until the app was restarted.
    ///
    /// 4060 and 18456 stay, because a contained user really does get them on master. Callers must
    /// still treat the verdict as revocable — both can also be thrown transiently.
    /// </summary>
    public static readonly IReadOnlySet<int> MasterAccessDeniedErrorNumbers = new HashSet<int>
    {
        229,   // Permission denied on object
        230,   // Permission denied on column
        916,   // Server principal cannot access the database under the current security context
        4060,  // Cannot open database requested by the login
        18456  // Login failed for user
    };

    /// <summary>
    /// True when the error is temporary and the operation is worth retrying.
    /// </summary>
    public static bool IsTransient(int errorNumber) => TransientErrorNumbers.Contains(errorNumber);

    /// <summary>
    /// True when the error means this login cannot read <c>master</c>. Never true for an error that
    /// merely means the server was unreachable — see the remarks on
    /// <see cref="MasterAccessDeniedErrorNumbers"/>.
    /// </summary>
    public static bool IsMasterAccessDenied(int errorNumber) => MasterAccessDeniedErrorNumbers.Contains(errorNumber);
}

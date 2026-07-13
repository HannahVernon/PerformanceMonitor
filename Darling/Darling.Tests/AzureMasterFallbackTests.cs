/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Linq;
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Darling's half of the #1506 pin.
///
/// Darling inherited this bug by copy: it carried its own duplicate of the master-access error list,
/// commented "mirroring Lite's #857 behavior", including the same two reachability errors (40615
/// firewall, 40613 temporarily-unavailable) that must never be read as a permanent statement about a
/// login's right to read master.
///
/// Both SKUs now classify through the shared <see cref="SqlErrorClassification"/>, so the list cannot
/// be edited in one and not the other. These tests exist so the invariant is enforced from Darling's
/// suite too — Lite's copy of it protects Lite, and protecting only Lite is how this happened.
/// </summary>
public class AzureMasterFallbackTests
{
    /// <summary>
    /// The bug. 40615 is a firewall rejection at the logical server and 40613 is "retry later" —
    /// neither says anything about whether this login may read master.
    /// </summary>
    [Theory]
    [InlineData(40615)]
    [InlineData(40613)]
    public void Reachability_Errors_Are_Not_Master_Access_Denied(int errorNumber)
    {
        Assert.False(DarlingCollectorRunner.IsMasterAccessDeniedError(errorNumber));
    }

    /// <summary>
    /// #857 still holds: a contained user that exists only in a user database really does get these
    /// when it opens master, and single-database fallback is the right answer.
    /// </summary>
    [Theory]
    [InlineData(229)]
    [InlineData(230)]
    [InlineData(916)]
    [InlineData(4060)]
    [InlineData(18456)]
    public void Permission_Errors_Are_Still_Master_Access_Denied(int errorNumber)
    {
        Assert.True(DarlingCollectorRunner.IsMasterAccessDeniedError(errorNumber));
    }

    /// <summary>
    /// No error may mean both "retry, this will pass" and "give up, this is permanent". 40613 meant
    /// both, which is the whole bug.
    /// </summary>
    [Fact]
    public void No_Error_Is_Both_Transient_And_Permanently_Fatal()
    {
        var contradictory = SqlErrorClassification.TransientErrorNumbers
            .Where(SqlErrorClassification.IsMasterAccessDenied)
            .ToList();

        Assert.True(
            contradictory.Count == 0,
            $"Error(s) {string.Join(", ", contradictory)} are treated as retryable-transient AND as a " +
            "permanent master-access verdict. Pick one — see #1506.");
    }

    /// <summary>
    /// Lite and Darling must reach the same verdict for every error either one can see. They used to
    /// hold separate lists; this asserts the shared one is genuinely the only one in play.
    /// </summary>
    [Fact]
    public void Darling_Classifies_Identically_To_The_Shared_Source_Of_Truth()
    {
        int[] everyErrorWeReasonAbout =
        [
            229, 230, 297, 300, 916, 4060, 18456, 40613, 40615,
            -2, -1, 2, 53, 233, 10053, 10054, 10060, 10061,
            40143, 40197, 40501, 49918, 49919, 49920
        ];

        foreach (var errorNumber in everyErrorWeReasonAbout)
        {
            Assert.Equal(
                SqlErrorClassification.IsMasterAccessDenied(errorNumber),
                DarlingCollectorRunner.IsMasterAccessDeniedError(errorNumber));
        }
    }
}

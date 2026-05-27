/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitorDashboard.Services;

/// <summary>
/// Deterministic int server-id derivation used by the analysis pipeline.
///
/// <para>
/// <c>string.GetHashCode()</c> is randomized per process on .NET Core / .NET 10,
/// so persisted rows in <c>config.analysis_findings</c> and <c>config.analysis_muted</c>
/// would not match the next launch's value for the same server name. This helper
/// produces a stable FNV-1a hash so writes survive restart and are consistent across
/// the MCP entry points and any scheduled-analysis path.
/// </para>
/// </summary>
internal static class ServerIdHelper
{
    /// <summary>
    /// Process-independent FNV-1a hash of a string. Identical implementation to
    /// <c>Lite/Services/RemoteCollectorService.GetDeterministicHashCode</c> so
    /// Dashboard and Lite produce the same id for the same server name.
    /// </summary>
    public static int GetDeterministicHashCode(string value)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in value)
            {
                hash = (hash ^ c) * 16777619;
            }
            return hash;
        }
    }
}

/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Collectors;

/// <summary>
/// What a definition may need to know about the monitored server to build its query.
/// Grown deliberately as the sweep demands (engine edition today; version gates arrive with
/// the collectors that need them) — every added field is parity-critical target logic.
/// </summary>
public sealed class CollectorTargetInfo
{
    /// <summary>True when the target is Azure SQL Database (engine edition 5).</summary>
    public bool IsAzureSqlDb { get; init; }

    /// <summary>True when the target is Azure SQL Managed Instance (engine edition 8).</summary>
    public bool IsAzureManagedInstance { get; init; }

    /// <summary>
    /// True when the target is an Amazon RDS for SQL Server instance (detected via
    /// <c>DB_ID('rdsadmin') IS NOT NULL</c>). RDS does not expose the underlying OS, so DMVs that
    /// read OS/service state — notably <c>sys.dm_server_services</c> (used by agent_status) — and the
    /// restricted msdb surface running_jobs needs (<c>msdb.dbo.syssessions</c>) are unavailable there.
    /// Definitions gate those collectors off via <see cref="AppliesTo"/> so both hosts skip them.
    /// </summary>
    public bool IsAwsRds { get; init; }

    /// <summary>
    /// SQL Server major version (13 = 2016 … 17 = 2025); 0 when unknown. Definitions gate
    /// version-specific columns on this (database_config treats 0 as "assume newest" to match
    /// the original collector).
    /// </summary>
    public int SqlMajorVersion { get; init; }
}

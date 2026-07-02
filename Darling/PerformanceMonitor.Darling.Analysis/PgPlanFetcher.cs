/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Analysis;

namespace PerformanceMonitor.Darling.Analysis;

/// <summary>
/// Darling's <see cref="IPlanFetcher"/> — fetches execution plan XML live from the MONITORED
/// SQL Server (the plan cache, on demand; plan XML is never stored in Postgres), the
/// Dashboard's SqlServerPlanFetcher ported with one seam change: where the Dashboard has a
/// single connection string and Lite looks servers up in its ServerManager, Darling monitors
/// many servers from one service, so the constructor takes a
/// <c>Func&lt;int serverId, string?&gt;</c> resolver.
///
/// <para>
/// THE SEAM: Darling's worker supplies the resolver from its per-server runtimes (each
/// connected <c>ServerRuntime</c> carries the built connection string, keyed by the
/// storage-name-hash ServerId the findings persist) — this project deliberately does NOT
/// reference PerformanceMonitor.Darling.Service, so the dependency points service → analysis
/// and the resolver is the only bridge. Returning null for an unknown/disconnected serverId
/// degrades the fetch to null exactly like Lite's ServerManager miss.
/// </para>
///
/// <para>
/// Same query, timeouts, and degrade semantics as the Dashboard implementation: 10-second
/// connect / 15-second command budgets clamped onto the resolved connection string, null for
/// a plan no longer in cache, and any failure logs and returns null — a plan fetch can never
/// fail an analysis run.
/// </para>
/// </summary>
public sealed class PgPlanFetcher : IPlanFetcher
{
    /// <summary>The Dashboard twin's query verbatim — plan_handle arrives as the '0x...' hex
    /// string the collectors persist and converts server-side (CONVERT style 1).</summary>
    public const string PlanQuery = @"
SET NOCOUNT ON;
SELECT query_plan
FROM sys.dm_exec_query_plan(CONVERT(varbinary(64), @plan_handle, 1));";

    private readonly Func<int, string?> _connectionStringResolver;
    private readonly ILogger? _logger;

    public PgPlanFetcher(Func<int, string?> connectionStringResolver, ILogger? logger = null)
    {
        _connectionStringResolver = connectionStringResolver ?? throw new ArgumentNullException(nameof(connectionStringResolver));
        _logger = logger;
    }

    public async Task<string?> FetchPlanXmlAsync(int serverId, string planHandle)
    {
        if (string.IsNullOrEmpty(planHandle))
        {
            return null;
        }

        try
        {
            var connectionString = _connectionStringResolver(serverId);
            if (string.IsNullOrEmpty(connectionString))
            {
                /* Unknown or not-currently-connected server — degrade like Lite's lookup miss. */
                return null;
            }

            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                ConnectTimeout = 10,
                CommandTimeout = 15
            };

            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();

            await using var command = new SqlCommand(PlanQuery, connection);
            command.CommandTimeout = 15;
            command.Parameters.AddWithValue("@plan_handle", planHandle);

            var result = await command.ExecuteScalarAsync();
            if (result == null || result is DBNull)
            {
                return null;
            }

            return result.ToString();
        }
        catch (Exception ex)
        {
            _logger?.LogError("[PgPlanFetcher] Failed to fetch plan for handle {PlanHandle}: {Message}", planHandle, ex.Message);
            return null;
        }
    }
}

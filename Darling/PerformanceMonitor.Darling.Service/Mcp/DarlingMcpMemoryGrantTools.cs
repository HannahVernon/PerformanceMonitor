/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Npgsql;
using PerformanceMonitor.Common;

#pragma warning disable CA1707 // MCP tools use snake_case naming convention

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// The memory-grant MCP tools — get_resource_semaphore, get_memory_grants — served over Darling's Postgres
/// store, both reading the LATEST <c>memory_grant_stats</c> snapshot through
/// <see cref="DarlingMemoryGrantReader"/> (STORED reads, no live monitored-server hit). The two names are two
/// lenses on the one collector table, so Darling hosts BOTH: get_resource_semaphore is the Dashboard's
/// semaphore/ceiling shape (per resource semaphore, with the workspace-memory target/max-target ceiling);
/// get_memory_grants is Lite's per-pool grant-detail shape. A client familiar with either SKU finds its tool.
/// </summary>
[McpServerToolType]
public sealed class DarlingMcpMemoryGrantTools
{
    [McpServerTool(Name = "get_resource_semaphore"), Description("Gets resource semaphore statistics showing granted vs available workspace memory against the target/max-target ceiling, waiter counts, and timeout/forced grant pressure indicators. High waiter counts or rising timeout/forced deltas indicate memory grant pressure affecting query performance.")]
    public static async Task<string> GetResourceSemaphore(
        NpgsqlDataSource postgres,
        [Description("Server name or display name.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24)
    {
        var (resolved, error) = await DarlingServerResolver.ResolveOrErrorAsync(postgres, server_name);
        if (error != null) return error;

        var validation = McpHelpers.ValidateHoursBack(hours_back);
        if (validation != null) return validation;

        try
        {
            var now = DateTime.UtcNow;
            var rows = await DarlingMemoryGrantReader.GetResourceSemaphoreLatestAsync(
                postgres, resolved.Value.ServerId, now.AddHours(-hours_back), now);
            if (rows.Count == 0)
                return McpHelpers.Status("unavailable", "No memory grant data available.");

            var grants = rows.Select(r => new
            {
                collection_time = r.CollectionTime.ToString("o"),
                resource_semaphore_id = r.ResourceSemaphoreId,
                pool_id = r.PoolId,
                target_memory_mb = r.TargetMemoryMb,
                max_target_memory_mb = r.MaxTargetMemoryMb,
                total_memory_mb = r.TotalMemoryMb,
                available_memory_mb = r.AvailableMemoryMb,
                granted_memory_mb = r.GrantedMemoryMb,
                used_memory_mb = r.UsedMemoryMb,
                grantee_count = r.GranteeCount,
                waiter_count = r.WaiterCount,
                timeout_error_count = r.TimeoutErrorCount,
                forced_grant_count = r.ForcedGrantCount,
                timeout_error_count_delta = r.TimeoutErrorCountDelta,
                forced_grant_count_delta = r.ForcedGrantCountDelta
            });

            return JsonSerializer.Serialize(new
            {
                server = resolved.Value.ServerName,
                grants
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_resource_semaphore", ex);
        }
    }

    [McpServerTool(Name = "get_memory_grants"), Description("Gets resource semaphore statistics showing granted vs available workspace memory per resource pool, waiter counts, and timeout/forced grant deltas. High waiter counts or rising timeout deltas indicate memory grant pressure affecting query performance.")]
    public static async Task<string> GetMemoryGrants(
        NpgsqlDataSource postgres,
        [Description("Server name or display name.")] string? server_name = null,
        [Description("Hours of history. Default 1.")] int hours_back = 1)
    {
        var (resolved, error) = await DarlingServerResolver.ResolveOrErrorAsync(postgres, server_name);
        if (error != null) return error;

        var validation = McpHelpers.ValidateHoursBack(hours_back);
        if (validation != null) return validation;

        try
        {
            var now = DateTime.UtcNow;
            var rows = await DarlingMemoryGrantReader.GetMemoryGrantsLatestAsync(
                postgres, resolved.Value.ServerId, now.AddHours(-hours_back), now);
            if (rows.Count == 0)
                return McpHelpers.Status("unavailable", "No memory grant data available.");

            var grants = rows.Select(r => new
            {
                collection_time = r.CollectionTime.ToString("o"),
                pool_id = r.PoolId,
                available_memory_mb = Math.Round(r.AvailableMemoryMb, 2),
                granted_memory_mb = Math.Round(r.GrantedMemoryMb, 2),
                used_memory_mb = Math.Round(r.UsedMemoryMb, 2),
                grantee_count = r.GranteeCount,
                waiter_count = r.WaiterCount,
                timeout_error_count_delta = r.TimeoutErrorCountDelta,
                forced_grant_count_delta = r.ForcedGrantCountDelta
            });

            return JsonSerializer.Serialize(new
            {
                server = resolved.Value.ServerName,
                grants
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_memory_grants", ex);
        }
    }
}

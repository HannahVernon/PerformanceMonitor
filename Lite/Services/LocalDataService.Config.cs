/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DuckDB.NET.Data;

namespace PerformanceMonitorLite.Services;

public partial class LocalDataService
{
    /// <summary>
    /// Gets the latest server configuration snapshot (sys.configurations).
    /// </summary>
    public async Task<List<ServerConfigRow>> GetLatestServerConfigAsync(int serverId)
    {
        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT configuration_name, value_configured, value_in_use, is_dynamic, is_advanced
FROM server_config
WHERE server_id = $1
AND   capture_time = (SELECT MAX(capture_time) FROM server_config WHERE server_id = $1)
ORDER BY configuration_name";

        command.Parameters.Add(new DuckDBParameter { Value = serverId });

        var items = new List<ServerConfigRow>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new ServerConfigRow
            {
                ConfigurationName = reader.GetString(0),
                ValueConfigured = reader.IsDBNull(1) ? 0 : ToInt64(reader.GetValue(1)),
                ValueInUse = reader.IsDBNull(2) ? 0 : ToInt64(reader.GetValue(2)),
                IsDynamic = !reader.IsDBNull(3) && reader.GetBoolean(3),
                IsAdvanced = !reader.IsDBNull(4) && reader.GetBoolean(4)
            });
        }

        return items;
    }

    /// <summary>
    /// Gets the latest database configuration snapshot (sys.databases).
    /// </summary>
    public async Task<List<DatabaseConfigRow>> GetLatestDatabaseConfigAsync(int serverId)
    {
        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT database_name, compatibility_level, recovery_model,
       is_auto_close_on, is_auto_shrink_on, is_query_store_on,
       page_verify_option, target_recovery_time_seconds, delayed_durability
FROM database_config
WHERE server_id = $1
AND   capture_time = (SELECT MAX(capture_time) FROM database_config WHERE server_id = $1)
ORDER BY database_name";

        command.Parameters.Add(new DuckDBParameter { Value = serverId });

        var items = new List<DatabaseConfigRow>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new DatabaseConfigRow
            {
                DatabaseName = reader.GetString(0),
                CompatibilityLevel = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                RecoveryModel = reader.IsDBNull(2) ? "" : reader.GetString(2),
                IsAutoCloseOn = !reader.IsDBNull(3) && reader.GetBoolean(3),
                IsAutoShrinkOn = !reader.IsDBNull(4) && reader.GetBoolean(4),
                IsQueryStoreOn = !reader.IsDBNull(5) && reader.GetBoolean(5),
                PageVerifyOption = reader.IsDBNull(6) ? "" : reader.GetString(6),
                TargetRecoveryTimeSeconds = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                DelayedDurability = reader.IsDBNull(8) ? "" : reader.GetString(8)
            });
        }

        return items;
    }

    /// <summary>
    /// Gets the latest database-scoped configuration snapshot.
    /// </summary>
    public async Task<List<DatabaseScopedConfigRow>> GetLatestDatabaseScopedConfigAsync(int serverId)
    {
        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT database_name, configuration_name, value, value_for_secondary
FROM database_scoped_config
WHERE server_id = $1
AND   capture_time = (SELECT MAX(capture_time) FROM database_scoped_config WHERE server_id = $1)
ORDER BY database_name, configuration_name";

        command.Parameters.Add(new DuckDBParameter { Value = serverId });

        var items = new List<DatabaseScopedConfigRow>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new DatabaseScopedConfigRow
            {
                DatabaseName = reader.GetString(0),
                ConfigurationName = reader.GetString(1),
                Value = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ValueForSecondary = reader.IsDBNull(3) ? "" : reader.GetString(3)
            });
        }

        return items;
    }

    /// <summary>
    /// Gets the latest trace flags snapshot.
    /// </summary>
    public async Task<List<TraceFlagRow>> GetLatestTraceFlagsAsync(int serverId)
    {
        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT trace_flag, status, is_global, is_session
FROM trace_flags
WHERE server_id = $1
AND   capture_time = (SELECT MAX(capture_time) FROM trace_flags WHERE server_id = $1)
ORDER BY trace_flag";

        command.Parameters.Add(new DuckDBParameter { Value = serverId });

        var items = new List<TraceFlagRow>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new TraceFlagRow
            {
                TraceFlag = reader.GetInt32(0),
                Status = !reader.IsDBNull(1) && reader.GetBoolean(1),
                IsGlobal = !reader.IsDBNull(2) && reader.GetBoolean(2),
                IsSession = !reader.IsDBNull(3) && reader.GetBoolean(3)
            });
        }

        return items;
    }
}

public class ServerConfigRow
{
    public string ConfigurationName { get; set; } = "";
    public long ValueConfigured { get; set; }
    public long ValueInUse { get; set; }
    public bool IsDynamic { get; set; }
    public bool IsAdvanced { get; set; }
    public string DynamicDisplay => IsDynamic ? "Yes" : "No";
    public string AdvancedDisplay => IsAdvanced ? "Yes" : "No";
    public bool ValuesMatch => ValueConfigured == ValueInUse;
}

public class DatabaseConfigRow
{
    public string DatabaseName { get; set; } = "";
    public int CompatibilityLevel { get; set; }
    public string RecoveryModel { get; set; } = "";
    public bool IsAutoCloseOn { get; set; }
    public bool IsAutoShrinkOn { get; set; }
    public bool IsQueryStoreOn { get; set; }
    public string PageVerifyOption { get; set; } = "";
    public int TargetRecoveryTimeSeconds { get; set; }
    public string DelayedDurability { get; set; } = "";
    public string AutoCloseDisplay => IsAutoCloseOn ? "Yes" : "No";
    public string AutoShrinkDisplay => IsAutoShrinkOn ? "Yes" : "No";
    public string QueryStoreDisplay => IsQueryStoreOn ? "Yes" : "No";
}

public class DatabaseScopedConfigRow
{
    public string DatabaseName { get; set; } = "";
    public string ConfigurationName { get; set; } = "";
    public string Value { get; set; } = "";
    public string ValueForSecondary { get; set; } = "";
}

public class TraceFlagRow
{
    public int TraceFlag { get; set; }
    public bool Status { get; set; }
    public bool IsGlobal { get; set; }
    public bool IsSession { get; set; }
    public string StatusDisplay => Status ? "Enabled" : "Disabled";
    public string GlobalDisplay => IsGlobal ? "Yes" : "No";
    public string SessionDisplay => IsSession ? "Yes" : "No";
}

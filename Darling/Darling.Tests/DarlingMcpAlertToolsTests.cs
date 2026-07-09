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
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Npgsql;
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Service.Mcp;
using PerformanceMonitor.Darling.Storage;
using Xunit;

using Reader = PerformanceMonitor.Darling.Service.Mcp.DarlingAlertReader;

namespace Darling.Tests;

/// <summary>
/// Pins the alerts MCP slice — get_alert_history, get_alert_settings, get_mute_rules over the Postgres store,
/// the fleet edition's biggest MCP win (what fired / what's configured / what's suppressed). Ungated: the tool
/// surface is EXACTLY the three names (all static, on a [McpServerToolType] class, returning Task&lt;string&gt;);
/// each param contract matches Lite's (plus the fleet-only optional server_name on get_alert_history); the
/// alert-history / alert-settings read SQL is Postgres-dialect, positional-param, reads config_alert_log /
/// config_alert_settings, and excludes dismissed rows; and the advertised tools/list schema is Gemini-clean
/// with no required params.
/// </summary>
public sealed class DarlingMcpAlertToolsSurfaceAndSqlTests
{
    private static readonly string[] AlertToolSurface =
    {
        "get_alert_history",
        "get_alert_settings",
        "get_mute_rules",
    };

    private static MethodInfo[] ToolMethods() => typeof(DarlingMcpAlertTools)
        .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
        .ToArray();

    [Fact]
    public void ToolSurface_ExactlyTheThreeAlertTools()
    {
        var toolMethods = ToolMethods();
        var names = toolMethods
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AlertToolSurface, names);
        Assert.NotNull(typeof(DarlingMcpAlertTools).GetCustomAttribute<McpServerToolTypeAttribute>());
        Assert.All(toolMethods, m => Assert.True(m.IsStatic, $"{m.Name} must be static"));
        Assert.All(toolMethods, m => Assert.True(m.ReturnType == typeof(Task<string>), $"{m.Name} must return Task<string>"));
    }

    private static (string Name, bool Optional)[] McpParams(string toolName)
    {
        var method = ToolMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name == toolName);
        return method.GetParameters()
            .Where(p => p.GetCustomAttribute<DescriptionAttribute>() is not null)
            .Select(p => (p.Name!, p.HasDefaultValue))
            .ToArray();
    }

    [Theory]
    [InlineData("get_alert_history", "server_name,hours_back,limit")]
    [InlineData("get_mute_rules", "enabled_only")]
    public void ParamContract_MatchesContract(string toolName, string expectedCsv)
    {
        Assert.Equal(expectedCsv.Split(','), McpParams(toolName).Select(p => p.Name).ToArray());
    }

    [Fact]
    public void ParamContract_AlertSettings_TakesNoInputParameters()
    {
        /* Only the injected NpgsqlDataSource, which is not [Description]-decorated — an empty input schema. */
        Assert.Empty(McpParams("get_alert_settings"));
    }

    [Fact]
    public void ParamContract_EveryDescribedParamIsOptional()
    {
        /* No required params anywhere: server_name auto-selects/omits, windows + enabled_only have defaults. */
        foreach (var tool in AlertToolSurface)
            Assert.All(McpParams(tool), p => Assert.True(p.Optional, $"{tool}.{p.Name} must be optional"));
    }

    /* ---------------- read SQL pins ---------------- */

    [Fact]
    public void AlertHistorySql_ReadsLog_ExcludesDismissed_ServerScoped()
    {
        var sql = Reader.AlertHistorySql;
        Assert.Contains("FROM config_alert_log", sql, StringComparison.Ordinal);
        Assert.Contains("dismissed = FALSE", sql, StringComparison.Ordinal);
        Assert.Contains("server_id = $2", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY alert_time DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $3", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AlertHistoryAllServersSql_ReadsLog_ExcludesDismissed_NoServerFilter()
    {
        var sql = Reader.AlertHistoryAllServersSql;
        Assert.Contains("FROM config_alert_log", sql, StringComparison.Ordinal);
        Assert.Contains("dismissed = FALSE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("server_id =", sql, StringComparison.Ordinal);   /* fleet-wide */
        Assert.Contains("LIMIT $2", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AlertSettingsSql_ReadsSingleGlobalRow()
    {
        var sql = Reader.AlertSettingsSelectSql;
        Assert.Contains("FROM config_alert_settings", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE id = 1", sql, StringComparison.Ordinal);
        Assert.Contains("cpu_threshold_percent", sql, StringComparison.Ordinal);
        Assert.Contains("delivery_mode", sql, StringComparison.Ordinal);
        Assert.Contains("notify_connection_changes", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(Reader.AlertHistorySql))]
    [InlineData(nameof(Reader.AlertHistoryAllServersSql))]
    [InlineData(nameof(Reader.AlertSettingsSelectSql))]
    public void Reads_ArePostgresDialect_NoTsqlIsms(string sqlName)
    {
        var sql = sqlName switch
        {
            nameof(Reader.AlertHistorySql) => Reader.AlertHistorySql,
            nameof(Reader.AlertHistoryAllServersSql) => Reader.AlertHistoryAllServersSql,
            _ => Reader.AlertSettingsSelectSql,
        };
        var lower = sql.ToLowerInvariant();
        Assert.DoesNotContain("getdate", lower);
        Assert.DoesNotContain("convert(", lower);
        Assert.DoesNotContain("top (", lower);
        Assert.DoesNotContain("isnull(", lower);
        Assert.DoesNotContain("N'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
    }

    /* ---------------- advertised MCP schema ---------------- */

    private static System.Collections.Generic.List<ModelContextProtocol.Protocol.Tool> BuildToolSchemas()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(NpgsqlDataSource), _ => null!);
        services.AddMcpServer().WithGeminiCompatibleTools<DarlingMcpAlertTools>();
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerTool>().Select(t => t.ProtocolTool).ToList();
    }

    [Fact]
    public void AdvertisedSchema_IsGeminiClean_ForAllThreeTools_NoRequiredParams()
    {
        var tools = BuildToolSchemas();
        Assert.Equal(3, tools.Count);
        var violations = tools.SelectMany(t => DarlingMcpSchemaAssert.Violations(t.Name, t.InputSchema)).ToList();
        Assert.True(violations.Count == 0, "Gemini-incompatible schema keywords leaked:\n" + string.Join("\n", violations));
        foreach (var t in tools)
            Assert.Empty(DarlingMcpSchemaAssert.RequiredOf(t.InputSchema));
    }
}

/// <summary>
/// Gated (DARLING_TEST_PG) live round-trips for the alert tools. Plants an alert-log row, seeds the single
/// alert-settings row (all defaults), and plants a mute rule, then asserts each tool surfaces its data — plus
/// that get_alert_history returns the row both server-scoped and fleet-wide.
/// </summary>
[Collection("live-postgres")]
public sealed class DarlingMcpAlertToolsLivePostgresTests
{
    private const string ServerName = "darling-mcp-alerts-e2e";
    private static readonly int ServerId = ServerIdHelper.GetDeterministicHashCode(ServerName);
    private const string MuteRuleId = "darling-mcp-alerts-e2e-rule";
    private static string? ConnectionString => Environment.GetEnvironmentVariable("DARLING_TEST_PG");

    [Fact]
    public async Task AlertTools_ReadPlantedRows_AgainstDevPostgres()
    {
        var cs = ConnectionString;
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "Set DARLING_TEST_PG to a Postgres connection string to run the live alert-tools test.");

        var ct = TestContext.Current.CancellationToken;
        using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);
        await DeleteRowsAsync(connection, ct);
        await using var postgres = NpgsqlDataSource.Create(cs!);

        try
        {
            await DarlingMcpTestData.RegisterServerAsync(connection, ServerId, ServerName, ct);
            var when = DarlingMcpTestData.TruncateToSeconds(DateTime.UtcNow).AddMinutes(-5);

            await DarlingMcpTestData.ExecAsync(connection, ct,
                @"INSERT INTO config_alert_log (alert_time, server_id, server_name, metric_name, current_value, threshold_value, alert_sent, notification_type, send_error, muted, detail_text)
VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)",
                when, ServerId, ServerName, "High CPU", 92.5, 80.0, true, "email", null, false, "CPU sustained above threshold");

            /* Seed the single global settings row — every column has a default, so id alone suffices. */
            await DarlingMcpTestData.ExecAsync(connection, ct,
                "INSERT INTO config_alert_settings (id) VALUES (1) ON CONFLICT (id) DO NOTHING");

            await DarlingMcpTestData.ExecAsync(connection, ct,
                @"INSERT INTO config_mute_rules (id, enabled, created_at_utc, expires_at_utc, reason, server_name, metric_name, database_pattern, query_text_pattern, wait_type_pattern, job_name_pattern)
VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)",
                MuteRuleId, true, DarlingMcpTestData.Naive(DateTime.UtcNow), null, "e2e rule", ServerName, "High CPU", null, null, null, null);

            /* Alert history — server-scoped + fleet-wide both surface the planted alert. */
            var scoped = await DarlingMcpAlertTools.GetAlertHistory(postgres, ServerName);
            DarlingMcpTestData.AssertEnvelope(scoped, ServerName, "alerts");
            Assert.Contains("High CPU", scoped, StringComparison.Ordinal);

            var fleet = await DarlingMcpAlertTools.GetAlertHistory(postgres);
            Assert.False(fleet.StartsWith("Error during", StringComparison.Ordinal), fleet);
            Assert.Contains("(all servers)", fleet, StringComparison.Ordinal);
            Assert.Contains("High CPU", fleet, StringComparison.Ordinal);

            /* Alert settings — the seeded row round-trips its default thresholds. */
            var settings = await DarlingMcpAlertTools.GetAlertSettings(postgres);
            Assert.False(settings.StartsWith("Error during", StringComparison.Ordinal), settings);
            Assert.Contains("threshold_percent", settings, StringComparison.Ordinal);
            Assert.Contains("delivery", settings, StringComparison.Ordinal);

            /* Mute rules — the planted rule surfaces. */
            var mutes = await DarlingMcpAlertTools.GetMuteRules(postgres);
            Assert.False(mutes.StartsWith("Error during", StringComparison.Ordinal), mutes);
            Assert.Contains(MuteRuleId, mutes, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteRowsAsync(connection, ct);
        }
    }

    private static async Task DeleteRowsAsync(NpgsqlConnection connection, System.Threading.CancellationToken ct)
    {
        var sql = $"DELETE FROM config_alert_log WHERE server_id = {ServerId};"
            + $" DELETE FROM config_mute_rules WHERE id = '{MuteRuleId}';"
            + $" DELETE FROM servers WHERE server_id = {ServerId};";
        using var cleanup = new NpgsqlCommand(sql, connection);
        await cleanup.ExecuteNonQueryAsync(ct);
    }
}

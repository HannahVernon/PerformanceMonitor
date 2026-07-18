/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using PerformanceMonitor.Darling.Analysis;
using PerformanceMonitor.Darling.Service.Mcp;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// The web dashboard's HTTP API surface (#1562). <see cref="DarlingWebHostService"/> owns the host, the
/// pipeline order, and the auth gate; this class owns the ROUTES. The two are split so the host (Builder 1's
/// foundation) and the endpoints (Builder 2) evolve independently against the stable <see cref="MapAll"/> seam.
///
/// <para><b>The read surface.</b> <c>GET /api/read/{tool_name}</c> is 1:1 with the READ-ONLY MCP tool catalog:
/// each endpoint calls the SAME public static tool method the MCP server exposes (the <c>[McpServerTool]</c>
/// attributes are inert — the tool bodies are plain statics), so there is ZERO SQL / projection drift between the
/// two surfaces. Query-string values bind to the tool's parameters (<c>server</c>/<c>server_name</c>,
/// <c>hours</c>/<c>hours_back</c>, <c>top</c>, <c>limit</c>, ...); missing optional parameters fall back to the
/// method's own defaults. The excluded tools are the ones that are not read-only-over-the-store:
/// <c>analyze_server</c> (a live monitored-server touch), <c>mute_analysis_finding</c> (a write), and the
/// <c>analyze_*_plan</c> compute family (phase 2). A reflection test pins the endpoint set == the tool catalog
/// minus exactly those exclusions, so a future tool cannot be silently missed.</para>
///
/// <para><b>Response mapping.</b> The tools always return a string: a serialized JSON object/array for data (and
/// for the <c>{"status", ...}</c> empty-result envelope), or a bare <c>"Error during ..."</c> string when the
/// body's try/catch caught an exception, or a bare validation/resolution message. A leading <c>{</c> or <c>[</c>
/// passes through verbatim as <c>application/json</c> (200); a <c>"Error during ..."</c> string maps to 500; any
/// other bare (non-JSON) string is a client-correctable error (bad parameter, unknown server) and maps to 400 —
/// both wrapped as <c>{"error": "..."}</c>.</para>
///
/// <para><b>The pre-banded fleet.</b> <c>GET /api/fleet</c> and the <c>get_fleet_overview</c> MCP tool both read
/// through <see cref="DarlingFleetReader"/> — the enriched per-server cards and the cross-server rollup, banded
/// once by the shared <c>ServerHealthClassifier</c>.</para>
/// </summary>
public static class DarlingWebEndpoints
{
    /// <summary>The tool names deliberately absent from the <c>/api/read/*</c> surface: not read-only over the
    /// store. <c>analyze_server</c> makes a live monitored-server connection; <c>mute_analysis_finding</c> writes;
    /// the <c>analyze_*_plan</c> family is the compute-heavy plan-analysis phase-2 work.</summary>
    public static readonly IReadOnlySet<string> ExcludedToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "analyze_server",
        "mute_analysis_finding",
        "analyze_query_plan",
        "analyze_procedure_plan",
        "analyze_query_store_plan",
        "analyze_plan_xml",
    };

    /// <summary>The window (hours) the fleet card blocking / deadlock counts default to — the WPF Overview's window.</summary>
    private const int DefaultFleetHours = 1;

    /// <summary>
    /// Maps the web dashboard's HTTP endpoints onto <paramref name="app"/>, reading from <paramref name="postgres"/>
    /// (the VIEWER-role store pool). Called ONCE from the web host's pipeline, after the auth middleware and before
    /// the static files. Every route lives under <c>/api/*</c> so the SPA's static surface never collides.
    /// </summary>
    public static void MapAll(WebApplication app, NpgsqlDataSource postgres)
    {
        /* Liveness probe (Builder 1's stub, kept): proves the host is up and the pipeline reaches the API. */
        app.MapGet("/api/ping", () => Results.Json(new { status = "ok" }));

        /* The four analysis-READ tools take a DarlingAnalysisService; the web host does not register one, so
           build it once here from the same VIEWER-role pool (its read methods — fact collection, period compare,
           persisted-finding read — need only the store; the optional plan fetcher / logger are for the excluded
           analyze/drill path). Shared across requests, like the MCP host's singleton. */
        var analysis = new DarlingAnalysisService(postgres);

        /* The pre-banded fleet roll-up (also surfaced as the get_fleet_overview MCP tool). */
        app.MapGet("/api/fleet", async (HttpContext context) =>
        {
            var hours = Math.Clamp(QueryInt(context, "hours_back", "hours", DefaultFleetHours), 1, 168);
            var worstCount = Math.Max(0, QueryInt(context, "worst_count", null, DarlingFleetReader.DefaultWorstCount));
            var now = DateTime.UtcNow;
            var result = await DarlingFleetReader.GetFleetOverviewAsync(
                postgres, now.AddHours(-hours), now, now, worstCount, context.RequestAborted);
            return Results.Json(result, DarlingFleetReader.JsonOptions);
        });

        /* One GET per read-only tool, calling the tool method directly (no SQL/projection re-implementation). */
        foreach (var (name, handler) in BuildReadDispatch())
        {
            app.MapGet("/api/read/" + name, async (HttpContext context) =>
            {
                string result;
                try
                {
                    result = await handler(context, postgres, analysis);
                }
                catch (Exception ex)
                {
                    /* The tools swallow their own exceptions into an "Error during ..." string; this is only a
                       backstop for a binding-layer throw, mapped the same way (-> HTTP 500). */
                    result = $"Error during {name}: {ex.Message}";
                }

                return ToHttpResult(result);
            });
        }
    }

    /// <summary>The signature every read endpoint's handler shares: bind the query string, call the tool.</summary>
    internal delegate Task<string> ReadToolHandler(HttpContext context, NpgsqlDataSource postgres, DarlingAnalysisService analysis);

    /// <summary>
    /// The EXPLICIT endpoint-name -> tool-call dispatch table (no runtime reflection): one entry per read-only
    /// tool, binding its parameters from the query string and calling the matching public static tool method. The
    /// keys ARE the <c>/api/read/*</c> surface — the parity test asserts they equal the <c>[McpServerTool]</c>
    /// catalog minus <see cref="ExcludedToolNames"/>.
    /// </summary>
    internal static IReadOnlyDictionary<string, ReadToolHandler> BuildReadDispatch()
    {
        return new Dictionary<string, ReadToolHandler>(StringComparer.Ordinal)
        {
            /* ── analysis reads (take the DarlingAnalysisService) ── */
            ["audit_config"] = (c, pg, an) => DarlingMcpTools.AuditConfig(an, pg, Server(c)),
            ["compare_analysis"] = (c, pg, an) => DarlingMcpTools.CompareAnalysis(an, pg, Server(c), Hours(c, 4), QueryInt(c, "baseline_hours_back", null, 28)),
            ["get_analysis_facts"] = (c, pg, an) => DarlingMcpTools.GetAnalysisFacts(an, pg, Server(c), Hours(c, 4), Str(c, "source"), QueryDouble(c, "min_severity", 0)),
            ["get_analysis_findings"] = (c, pg, an) => DarlingMcpTools.GetAnalysisFindings(an, pg, Server(c), Hours(c, 24)),

            /* ── sessions ── */
            ["get_active_queries"] = (c, pg, an) => DarlingMcpSessionTools.GetActiveQueries(pg, Server(c), Hours(c, 1), Str(c, "database_name"), QueryBool(c, "blocking_only", false), Rows(c, "limit", 50)),
            ["get_session_stats"] = (c, pg, an) => DarlingMcpSessionTools.GetSessionStats(pg, Server(c)),
            ["get_waiting_tasks"] = (c, pg, an) => DarlingMcpSessionTools.GetWaitingTasks(pg, Server(c), Hours(c, 1), Rows(c, "limit", 30)),

            /* ── alerts / mute rules ── */
            ["get_alert_history"] = (c, pg, an) => DarlingMcpAlertTools.GetAlertHistory(pg, Server(c), Hours(c, 24), Rows(c, "limit", 50)),
            ["get_alert_settings"] = (c, pg, an) => DarlingMcpAlertTools.GetAlertSettings(pg),
            ["get_mute_rules"] = (c, pg, an) => DarlingMcpAlertTools.GetMuteRules(pg, QueryBool(c, "enabled_only", true)),

            /* ── blocking / deadlocks ── */
            ["get_blocked_process_xml"] = (c, pg, an) => DarlingMcpBlockingTools.GetBlockedProcessXml(pg, Server(c), Hours(c, 24), Rows(c, "limit", 5)),
            ["get_blocking"] = (c, pg, an) => DarlingMcpBlockingTools.GetBlocking(pg, Server(c), Hours(c, 24), Rows(c, "limit", 30)),
            ["get_blocking_trend"] = (c, pg, an) => DarlingMcpBlockingTools.GetBlockingTrend(pg, Server(c), Hours(c, 24)),
            ["get_deadlock_detail"] = (c, pg, an) => DarlingMcpBlockingTools.GetDeadlockDetail(pg, Server(c), Hours(c, 24), Rows(c, "limit", 5)),
            ["get_deadlock_trend"] = (c, pg, an) => DarlingMcpBlockingTools.GetDeadlockTrend(pg, Server(c), Hours(c, 24)),
            ["get_deadlocks"] = (c, pg, an) => DarlingMcpBlockingTools.GetDeadlocks(pg, Server(c), Hours(c, 24), Rows(c, "limit", 20)),

            /* ── config (current + history) ── */
            ["get_database_config"] = (c, pg, an) => DarlingMcpConfigTools.GetDatabaseConfig(pg, Server(c), Str(c, "database_name")),
            ["get_server_config"] = (c, pg, an) => DarlingMcpConfigTools.GetServerConfig(pg, Server(c)),
            ["get_trace_flags"] = (c, pg, an) => DarlingMcpConfigTools.GetTraceFlags(pg, Server(c)),
            ["get_database_config_changes"] = (c, pg, an) => DarlingMcpConfigHistoryTools.GetDatabaseConfigChanges(pg, Server(c), Hours(c, 168)),
            ["get_database_scoped_config"] = (c, pg, an) => DarlingMcpConfigHistoryTools.GetDatabaseScopedConfig(pg, Server(c), Str(c, "database_name")),
            ["get_server_config_changes"] = (c, pg, an) => DarlingMcpConfigHistoryTools.GetServerConfigChanges(pg, Server(c), Hours(c, 168)),
            ["get_trace_flag_changes"] = (c, pg, an) => DarlingMcpConfigHistoryTools.GetTraceFlagChanges(pg, Server(c), Hours(c, 168)),

            /* ── core data reads ── */
            ["get_collection_health"] = (c, pg, an) => DarlingMcpDataTools.GetCollectionHealth(pg, Server(c)),
            ["get_cpu_utilization"] = (c, pg, an) => DarlingMcpDataTools.GetCpuUtilization(pg, Server(c), Hours(c, 4)),
            ["get_file_io_stats"] = (c, pg, an) => DarlingMcpDataTools.GetFileIoStats(pg, Server(c)),
            ["get_memory_clerks"] = (c, pg, an) => DarlingMcpDataTools.GetMemoryClerks(pg, Server(c)),
            ["get_memory_stats"] = (c, pg, an) => DarlingMcpDataTools.GetMemoryStats(pg, Server(c)),
            ["get_perfmon_stats"] = (c, pg, an) => DarlingMcpDataTools.GetPerfmonStats(pg, Server(c), Str(c, "counter_name"), Str(c, "instance_name")),
            ["get_query_store_top"] = (c, pg, an) => DarlingMcpDataTools.GetQueryStoreTop(pg, Server(c), Hours(c, 24), Rows(c, "top", 20), Str(c, "database_name")),
            ["get_long_query_completions"] = (c, pg, an) => DarlingMcpLongQueryTools.GetLongQueryCompletions(pg, Server(c), Hours(c, 24), Rows(c, "limit", 30)),
            ["get_server_properties"] = (c, pg, an) => DarlingMcpDataTools.GetServerProperties(pg, Server(c)),
            ["get_tempdb_trend"] = (c, pg, an) => DarlingMcpDataTools.GetTempDbTrend(pg, Server(c), Hours(c, 24)),
            ["get_top_procedures_by_cpu"] = (c, pg, an) => DarlingMcpDataTools.GetTopProceduresByCpu(pg, Server(c), Hours(c, 24), Rows(c, "top", 20), Str(c, "database_name")),
            ["get_top_queries_by_cpu"] = (c, pg, an) => DarlingMcpDataTools.GetTopQueriesByCpu(pg, Server(c), Hours(c, 24), Rows(c, "top", 20), Str(c, "database_name"), QueryBool(c, "parallel_only", false), QueryInt(c, "min_dop", null, 0)),
            ["get_wait_stats"] = (c, pg, an) => DarlingMcpDataTools.GetWaitStats(pg, Server(c), Hours(c, 24), Rows(c, "limit", 20)),
            ["get_wait_trend"] = (c, pg, an) => RequireText(c, "wait_type", out var waitType)
                ? DarlingMcpDataTools.GetWaitTrend(pg, waitType, Server(c), Hours(c, 24))
                : MissingParam("wait_type"),
            ["get_wait_types"] = (c, pg, an) => DarlingMcpDataTools.GetWaitTypes(pg, Server(c), Hours(c, 24)),
            ["list_servers"] = (c, pg, an) => DarlingMcpDataTools.ListServers(pg),

            /* ── trends ── */
            ["get_file_io_trend"] = (c, pg, an) => DarlingMcpTrendTools.GetFileIoTrend(pg, Server(c), Hours(c, 24)),
            ["get_memory_trend"] = (c, pg, an) => DarlingMcpTrendTools.GetMemoryTrend(pg, Server(c), Hours(c, 24)),
            ["get_perfmon_trend"] = (c, pg, an) => RequireText(c, "counter_name", out var counter)
                ? DarlingMcpTrendTools.GetPerfmonTrend(pg, counter, Server(c), Hours(c, 24))
                : MissingParam("counter_name"),
            ["get_query_duration_trend"] = (c, pg, an) => DarlingMcpTrendTools.GetQueryDurationTrend(pg, Server(c), Hours(c, 24)),
            ["get_query_trend"] = (c, pg, an) => RequireText(c, "query_hash", out var queryHash)
                ? (RequireText(c, "database_name", out var db)
                    ? DarlingMcpTrendTools.GetQueryTrend(pg, queryHash, db, Server(c), Hours(c, 24))
                    : MissingParam("database_name"))
                : MissingParam("query_hash"),

            /* ── health / overview ── */
            ["get_server_summary"] = (c, pg, an) => DarlingMcpHealthTools.GetServerSummary(pg, Server(c)),
            ["get_daily_summary"] = (c, pg, an) => DarlingMcpHealthTools.GetDailySummary(pg, Server(c), Str(c, "summary_date")),
            ["get_fleet_overview"] = (c, pg, an) => DarlingMcpFleetTools.GetFleetOverview(pg, Hours(c, DefaultFleetHours)),

            /* ── latch / spinlock ── */
            ["get_latch_stats"] = (c, pg, an) => DarlingMcpLatchSpinlockTools.GetLatchStats(pg, Server(c), Hours(c, 24), Rows(c, "top", 10)),
            ["get_spinlock_stats"] = (c, pg, an) => DarlingMcpLatchSpinlockTools.GetSpinlockStats(pg, Server(c), Hours(c, 24), Rows(c, "top", 10)),

            /* ── memory grants ── */
            ["get_memory_grants"] = (c, pg, an) => DarlingMcpMemoryGrantTools.GetMemoryGrants(pg, Server(c), Hours(c, 1)),
            ["get_memory_pressure_events"] = (c, pg, an) => DarlingMcpMemoryGrantTools.GetMemoryPressureEvents(pg, Server(c), Hours(c, 24)),
            ["get_resource_semaphore"] = (c, pg, an) => DarlingMcpMemoryGrantTools.GetResourceSemaphore(pg, Server(c), Hours(c, 24)),

            /* ── object / index stats ── */
            ["get_database_sizes"] = (c, pg, an) => DarlingMcpObjectStatsTools.GetDatabaseSizes(pg, Server(c)),
            ["get_index_usage"] = (c, pg, an) => DarlingMcpObjectStatsTools.GetIndexUsage(pg, Server(c)),
            ["get_object_locking"] = (c, pg, an) => DarlingMcpObjectStatsTools.GetObjectLocking(pg, Server(c)),
            ["get_table_index_sizes"] = (c, pg, an) => DarlingMcpObjectStatsTools.GetTableIndexSizes(pg, Server(c)),

            /* ── plan cache / scheduler ── */
            ["get_cpu_scheduler_pressure"] = (c, pg, an) => DarlingMcpPlanCacheSchedulerTools.GetCpuSchedulerPressure(pg, Server(c)),
            ["get_plan_cache_bloat"] = (c, pg, an) => DarlingMcpPlanCacheSchedulerTools.GetPlanCacheBloat(pg, Server(c), Hours(c, 24)),

            /* ── jobs ── */
            ["get_running_jobs"] = (c, pg, an) => DarlingMcpJobTools.GetRunningJobs(pg, Server(c)),

            /* ── stored plan XML (READ; the analyze_*_plan compute family stays excluded) ── */
            ["get_plan_xml"] = (c, pg, an) => RequireText(c, "query_hash", out var queryHash)
                ? DarlingMcpPlanTools.GetPlanXml(pg, queryHash, Server(c), Str(c, "database_name"))
                : MissingParam("query_hash"),

            /* ── default trace ── */
            ["get_default_trace_events"] = (c, pg, an) => DarlingMcpDefaultTraceTools.GetDefaultTraceEvents(pg, Server(c), Hours(c, 24), Rows(c, "limit", 100)),

            /* ── system_health parse-on-read family ── */
            ["get_health_parser_cpu_tasks"] = (c, pg, an) => DarlingMcpHealthParserTools.GetCPUTasks(pg, Server(c), Hours(c, 24), Rows(c, "limit", 50)),
            ["get_health_parser_io_issues"] = (c, pg, an) => DarlingMcpHealthParserTools.GetIOIssues(pg, Server(c), Hours(c, 24), Rows(c, "limit", 50)),
            ["get_health_parser_memory_broker"] = (c, pg, an) => DarlingMcpHealthParserTools.GetMemoryBroker(pg, Server(c), Hours(c, 24), Rows(c, "limit", 50)),
            ["get_health_parser_memory_conditions"] = (c, pg, an) => DarlingMcpHealthParserTools.GetMemoryConditions(pg, Server(c), Hours(c, 24), Rows(c, "limit", 50)),
            ["get_health_parser_memory_node_oom"] = (c, pg, an) => DarlingMcpHealthParserTools.GetMemoryNodeOOM(pg, Server(c), Hours(c, 24), Rows(c, "limit", 50)),
            ["get_health_parser_scheduler_issues"] = (c, pg, an) => DarlingMcpHealthParserTools.GetSchedulerIssues(pg, Server(c), Hours(c, 24), Rows(c, "limit", 50)),
            ["get_health_parser_severe_errors"] = (c, pg, an) => DarlingMcpHealthParserTools.GetSevereErrors(pg, Server(c), Hours(c, 24), Rows(c, "limit", 50)),
            ["get_health_parser_system_health"] = (c, pg, an) => DarlingMcpHealthParserTools.GetSystemHealth(pg, Server(c), Hours(c, 24), Rows(c, "limit", 50)),
        };
    }

    /* ─────────────────────────── response mapping ─────────────────────────── */

    /// <summary>How a tool's returned string maps to an HTTP outcome.</summary>
    internal enum ToolResponseKind
    {
        /// <summary>A serialized JSON object/array (data, or the {"status", ...} envelope) — 200 passthrough.</summary>
        JsonPassthrough,

        /// <summary>A bare "Error during ..." string (the tool caught an exception) — HTTP 500.</summary>
        ServerError,

        /// <summary>Any other bare string (validation / resolution) — a client-correctable HTTP 400.</summary>
        ClientError,
    }

    /// <summary>
    /// Classifies a tool's returned string. A leading <c>{</c> or <c>[</c> (after any whitespace) is a serialized
    /// object/array and passes through; a <c>"Error during ..."</c> string is the tool's caught-exception shape
    /// (HTTP 500); anything else is a bare validation / resolution message (HTTP 400). Pure so the whole mapping
    /// is unit-testable.
    /// </summary>
    internal static ToolResponseKind ClassifyToolResponse(string result)
    {
        var trimmed = result.AsSpan().TrimStart();
        if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
        {
            return ToolResponseKind.JsonPassthrough;
        }

        if (result.StartsWith("Error during ", StringComparison.Ordinal))
        {
            return ToolResponseKind.ServerError;
        }

        return ToolResponseKind.ClientError;
    }

    private static IResult ToHttpResult(string result) => ClassifyToolResponse(result) switch
    {
        ToolResponseKind.JsonPassthrough => Results.Text(result, "application/json"),
        ToolResponseKind.ServerError => Results.Json(new { error = result }, statusCode: StatusCodes.Status500InternalServerError),
        _ => Results.Json(new { error = result }, statusCode: StatusCodes.Status400BadRequest),
    };

    /* ─────────────────────────── query-string binding ─────────────────────────── */

    /// <summary>The server name from <c>?server=</c> (or the tool's own <c>?server_name=</c>); null when absent,
    /// which lets a tool auto-select a sole configured server exactly as the MCP surface does.</summary>
    private static string? Server(HttpContext context) => First(context, "server") ?? First(context, "server_name");

    /// <summary>The hours-back window from <c>?hours=</c> (or the tool's own <c>?hours_back=</c>), else the tool's default.</summary>
    private static int Hours(HttpContext context, int def) => QueryInt(context, "hours", "hours_back", def);

    /// <summary>An optional text parameter; null when absent or empty (so the tool sees its own default).</summary>
    private static string? Str(HttpContext context, string key) => First(context, key);

    /// <summary>A required text parameter — true with the value when present, false when absent/empty.</summary>
    private static bool RequireText(HttpContext context, string key, out string value)
    {
        value = First(context, key) ?? "";
        return value.Length > 0;
    }

    private static Task<string> MissingParam(string key) => Task.FromResult($"Missing required parameter '{key}'.");

    private static int QueryInt(HttpContext context, string key, string? aliasKey, int def) =>
        ParseInt(First(context, key) ?? (aliasKey is null ? null : First(context, aliasKey)), def);

    /// <summary>
    /// A row-count knob (<c>?limit=</c> / <c>?top=</c>), clamped to [1, <see cref="MaxRowLimit"/>] at the
    /// dispatch layer so an authenticated/loopback caller can't request an unbounded result set from a reader
    /// that binds the value straight into <c>LIMIT $N</c> (the /api/fleet window is clamped the same way).
    /// </summary>
    private static int Rows(HttpContext context, string key, int def) =>
        ClampRows(QueryInt(context, key, null, def));

    /// <summary>The dispatch-layer ceiling on any caller-supplied row count — generous for real use, bounded against abuse.</summary>
    internal const int MaxRowLimit = 1000;

    /// <summary>PURE row-count clamp to [1, <see cref="MaxRowLimit"/>] — the abuse bound the row-knob binding applies.</summary>
    internal static int ClampRows(int requested) => Math.Clamp(requested, 1, MaxRowLimit);

    private static bool QueryBool(HttpContext context, string key, bool def) => ParseBool(First(context, key), def);

    private static double QueryDouble(HttpContext context, string key, double def) => ParseDouble(First(context, key), def);

    /// <summary>The first non-empty value for a query key, or null.</summary>
    private static string? First(HttpContext context, string key)
    {
        var value = context.Request.Query[key].ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /* Pure parse helpers (invariant culture) — the binding logic the tests pin without an HttpContext. */

    internal static int ParseInt(string? raw, int def) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : def;

    internal static bool ParseBool(string? raw, bool def) =>
        bool.TryParse(raw, out var value) ? value : def;

    internal static double ParseDouble(string? raw, double def) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : def;
}

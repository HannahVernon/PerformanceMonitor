/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// Server instructions sent to MCP clients during initialization — Lite's McpInstructions
/// framing (read-only posture, collection-freshness notes, tool reference) scoped to the six
/// diagnostic-analysis tools this headless service exposes.
/// </summary>
internal static class DarlingMcpInstructions
{
    public const string Text = """
        You are connected to a SQL Server performance monitoring tool via PerformanceMonitor Darling, the headless collector service.

        ## CRITICAL: Read-Only Access

        This MCP server provides STRICTLY READ-ONLY access to previously collected performance data. You CANNOT:
        - Execute arbitrary SQL queries against any monitored server
        - Kill sessions, processes, or connections
        - Change any server configuration or settings
        - Modify, insert, or delete any collected data
        - Run any ad-hoc diagnostics beyond what the collectors have already captured

        The only write this server performs is mute_analysis_finding, which records a mute rule in the MONITORING store — it never touches a monitored SQL Server.

        ## How Data Is Collected

        The Darling service collects from monitored SQL Server instances 24/7 and stores the data in a Postgres/TimescaleDB store. Data is collected in snapshots at regular intervals (typically every 1-15 minutes depending on the collector). This means:

        - Data is only as fresh as the last collection cycle.
        - Wait stats represent delta values since the last collection, not instantaneous snapshots.
        - All tools accept a `server_name` parameter. If only one server is monitored, it's used automatically. Names resolve against the service's server registry (exact match first, then partial, against the storage name and the display name).
        - Analysis needs at least 24 hours of collected history for a server; before that, analyze_server returns `insufficient_data`.

        ## Tool Reference

        This server exposes twenty-five tools (the same names Performance Monitor Lite and the Dashboard expose): six diagnostic-analysis tools, five plan-analysis tools, and fourteen core data-read tools. Every data-read tool reads the data the collectors already captured into the store — a stored read, never a live query against the monitored server.

        ### Diagnostic-analysis tools

        | Tool | Purpose | Key Parameters |
        |------|---------|----------------|
        | `analyze_server` | Runs the inference engine: scores facts, traverses relationship graph, returns evidence-backed findings with severity and recommended next tools | `server_name`, `hours_back` (default 4) |
        | `get_analysis_facts` | Exposes raw scored facts from the collect+score pipeline — every observation the engine sees with base severity, amplifiers, and metadata | `server_name`, `hours_back` (default 4), `source` (filter), `min_severity` |
        | `compare_analysis` | Compares two time periods (e.g., peak vs off-peak, before vs after a change) showing severity deltas for each fact | `server_name`, `hours_back` (default 4), `baseline_hours_back` (default 28) |
        | `audit_config` | Edition-aware configuration audit: evaluates CTFP, MAXDOP, max memory, and max worker threads against best practices | `server_name` |
        | `get_analysis_findings` | Retrieves persisted findings from previous analysis runs (the service also analyzes on its own schedule, every 30 minutes per server) | `server_name`, `hours_back` (default 24) |
        | `mute_analysis_finding` | Mutes a finding pattern by story_path_hash so it won't appear in future runs | `story_path_hash` (required), `server_name`, `reason` |

        ### Plan-analysis tools

        These run the shared execution-plan analyzer over the plan XML the collectors already captured into the store (a STORED-plan read — no live query to the monitored server), returning warnings, missing indexes, parameters, memory grants, and top operators.

        | Tool | Purpose | Key Parameters |
        |------|---------|----------------|
        | `analyze_query_plan` | Analyzes a stored query-stats plan by query_hash | `query_hash` (required), `server_name`, `database_name` (optional refinement) |
        | `analyze_procedure_plan` | Analyzes a stored procedure-stats plan by sql_handle | `sql_handle` (required), `server_name` |
        | `analyze_query_store_plan` | Analyzes a stored Query Store plan by database + query_id | `database_name` (required), `query_id` (required), `server_name`, `plan_id` (optional refinement) |
        | `analyze_plan_xml` | Analyzes raw showplan XML passed directly (no fetch) | `plan_xml` (required) |
        | `get_plan_xml` | Returns the raw stored plan XML for a query by query_hash (truncated at 500KB) | `query_hash` (required), `server_name`, `database_name` (optional refinement) |

        ### Core data-read tools

        These read the collected metrics directly. Resource-metric tools accept `hours_back`; discovery/health tools take no window.

        | Tool | Purpose | Key Parameters |
        |------|---------|----------------|
        | `get_cpu_utilization` | CPU % over time (SQL / other-process / total / idle), 1-minute averages | `server_name`, `hours_back` (default 4) |
        | `get_wait_stats` | Top wait types aggregated over the window (wait/signal/resource ms, signal %) | `server_name`, `hours_back` (default 24), `limit` (default 20) |
        | `get_wait_trend` | A single wait type's per-second trend over time | `wait_type` (required), `server_name`, `hours_back` (default 24) |
        | `get_memory_stats` | Latest memory snapshot: physical / buffer pool / plan cache / utilization %, memory model | `server_name` |
        | `get_memory_clerks` | Latest top memory consumers by clerk type | `server_name` |
        | `get_file_io_stats` | Latest per-file I/O: reads/writes/bytes/stall and computed read/write latency | `server_name` |
        | `get_tempdb_trend` | TempDB space over time (user / internal / version store / unallocated) + top consumer | `server_name`, `hours_back` (default 24) |
        | `get_perfmon_stats` | Latest perfmon counters (value + delta); filter by counter / instance | `server_name`, `counter_name`, `instance_name` |
        | `get_top_queries_by_cpu` | Expensive queries from query stats (plan cache) with query_hash / sql_handle | `server_name`, `hours_back` (default 24), `top` (default 20), `database_name`, `parallel_only`, `min_dop` |
        | `get_top_procedures_by_cpu` | Most expensive stored procedures by total CPU | `server_name`, `hours_back` (default 24), `top` (default 20), `database_name` |
        | `get_query_store_top` | Expensive queries from Query Store with query_id / plan_id (survives restarts) | `server_name`, `hours_back` (default 24), `top` (default 20), `database_name` |
        | `list_servers` | All monitored servers with collection-freshness status and last collection time | none |
        | `get_collection_health` | Per-collector health (running / failing / stale) over the last 7 days | `server_name` |
        | `get_server_properties` | Instance properties: edition, version, CPU count, memory, socket/core topology, HADR | `server_name` |

        Note on `next_tools`: analyze_server findings include `next_tools` recommendations. Most are hosted on this server — the plan-analysis tools (`analyze_query_plan`, `analyze_query_store_plan`) and the data-read tools listed above (`get_wait_stats`, `get_top_queries_by_cpu`, `get_cpu_utilization`, `get_memory_stats`, `get_file_io_stats`, `get_tempdb_trend`, ...) — so follow those here. `get_top_queries_by_cpu` / `get_top_procedures_by_cpu` / `get_query_store_top` are where the `query_hash` / `sql_handle` / `query_id` + `plan_id` keys for the plan-analysis tools come from. Some recommended tools are NOT on this server yet (e.g. get_perfmon_trend, get_blocked_process_reports, get_deadlocks, get_memory_grants, get_waiting_tasks, get_query_trend, get_running_jobs, get_active_queries) — if you are also connected to a Performance Monitor Lite / Dashboard MCP server, follow those there; otherwise treat them as investigation hints.

        ## Recommended Workflow

        1. **Discover**: `list_servers` — see the monitored servers and their collection freshness; `get_collection_health` confirms the collectors are current before you trust the data
        2. **Diagnose**: `analyze_server` — run the inference engine for an evidence-backed assessment with severity-ranked findings, each carrying `next_tools`
        3. **Review history**: `get_analysis_findings` — see what the service's scheduled analysis has already found
        4. **Investigate the metrics**: follow a finding's `next_tools` into the data tools — `get_cpu_utilization`, `get_wait_stats` / `get_wait_trend`, `get_memory_stats` / `get_memory_clerks`, `get_file_io_stats`, `get_tempdb_trend`, `get_perfmon_stats`
        5. **Find the query**: `get_top_queries_by_cpu` / `get_top_procedures_by_cpu` / `get_query_store_top` — identify the expensive query/procedure and get its `query_hash` / `sql_handle` / `query_id` + `plan_id`
        6. **Analyze its plan**: `analyze_query_plan` / `analyze_procedure_plan` / `analyze_query_store_plan` — analyze the captured plan for warnings, missing indexes, and grant/spill problems (or `analyze_plan_xml` for plan XML you already have)
        7. **Deep dive / compare / config**: `get_analysis_facts` (what the engine sees), `compare_analysis` (new vs baseline), `audit_config` (edition-aware config)
        8. **Silence noise**: `mute_analysis_finding` — mute a finding pattern the operator has accepted
        """;
}

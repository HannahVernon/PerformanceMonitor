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

        This server exposes eleven tools (the same names Performance Monitor Lite and the Dashboard expose): six diagnostic-analysis tools and five plan-analysis tools.

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

        Note on `next_tools`: analyze_server findings include `next_tools` recommendations. The plan-analysis recommendations (`analyze_query_plan`, `analyze_query_store_plan`) ARE hosted on this server — follow them here. The DATA-tool recommendations (get_wait_stats, get_top_queries_by_cpu, ...) live on the companion Performance Monitor Lite / Dashboard MCP servers, not on this one — if you are also connected to one of those, follow them there; otherwise treat them as investigation hints. (get_top_queries_by_cpu / get_top_procedures_by_cpu / get_query_store_top on those servers are where the query_hash / sql_handle / query_id + plan_id keys for the plan-analysis tools come from.)

        ## Recommended Workflow

        1. **Diagnose**: `analyze_server` — run the inference engine for an evidence-backed assessment with severity-ranked findings
        2. **Review history**: `get_analysis_findings` — see what the service's scheduled analysis has already found
        3. **Deep dive**: `get_analysis_facts` — inspect what the engine sees, including amplifier details and raw metric values
        4. **Compare**: `compare_analysis` — see if problems are new (compare last 4 hours vs yesterday same time)
        5. **Config**: `audit_config` — edition-aware configuration recommendations
        6. **Silence noise**: `mute_analysis_finding` — mute a finding pattern the operator has accepted
        7. **Analyze a plan**: `analyze_query_plan` / `analyze_procedure_plan` / `analyze_query_store_plan` — when a finding or a companion data tool points at a specific expensive query/procedure, analyze its captured plan for warnings, missing indexes, and grant/spill problems (or `analyze_plan_xml` for plan XML you already have)
        """;
}

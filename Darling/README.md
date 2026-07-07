# Performance Monitor Darling — Headless Edition

Darling is the headless, centralized edition of Performance Monitor: a 24/7 Windows service that collects from your SQL Servers into a central PostgreSQL (optionally TimescaleDB) store, plus a detached desktop viewer that reads that store. No desktop app has to stay open for collection to happen, and every viewer seat reads the same central data.

It runs the **same monitoring brain as the Lite edition** — one shared codebase, two storage engines:

- `PerformanceMonitor.Collectors` owns all 32 collector definitions: the exact T-SQL sent to monitored servers, the result-row mappings, the delta rules, the default cadences and retention horizons, and the ignored-wait-types list. Lite writes those rows to DuckDB; Darling writes the same rows to PostgreSQL via binary COPY.
- `PerformanceMonitor.Alerting` owns the shared alert engine — the same thresholds, edge-trigger gates, cooldowns, and dedup fingerprints Lite uses.
- The analysis/recommendations pipeline (the same inference engine behind both apps' Recommendations tabs and the `analyze_server` MCP tool) runs on a schedule inside the service.

A collector, alert, or analysis change lands once in the shared libraries and both editions get it. A Darling install monitoring a server even derives the **same `server_id`** Lite would for that server, because the identity rule (`host[:database][:RO]`, hashed) is shared too.

> **Status: in development.** Darling builds and runs from source (it is wired into the solution and CI), but is not yet packaged into the signed release artifacts. Expect the surface documented here to grow.

---

## When to Choose Darling vs. Lite

| | **Lite** | **Darling** |
|---|---|---|
| Collection runs | While the desktop app is open (or in the tray) | 24/7 as a Windows service |
| Data lives | Locally per seat (DuckDB + Parquet) | Centrally (PostgreSQL / TimescaleDB) |
| Execution plans | Not stored (fetched live when you view a query) | Captured and stored, TOAST-compressed (`capturePlans`, default on) |
| Viewers | The app is the viewer | Any number of viewer seats read the central store |
| Setup | Download and run | Provision PostgreSQL, edit `darling.json`, install the service |
| Best for | Quick triage, consultants, a handful of servers | Always-on team monitoring, larger estates, one shared store |
| Configuration | Settings UI | One JSON file (no UI) |

Nothing is installed on the monitored SQL Servers by either edition beyond two lightweight Extended Events ring-buffer sessions and, when it is unset, a one-time `blocked process threshold` bootstrap (see [What the Service Does on Monitored Servers](#what-the-service-does-on-monitored-servers)).

---

## Quick Start

### Prerequisites

- **Windows** for the service host (Windows-service lifetime, DPAPI password protection) and for the viewer (WPF). Monitored servers can be SQL Server 2016–2025, Azure SQL Managed Instance, AWS RDS for SQL Server, or Azure SQL Database.
- **A PostgreSQL store — bundled or your own.** In managed mode (the shipped default, see [Managed Bundled PostgreSQL](#managed-bundled-postgresql)) the service runs its own bundled PostgreSQL 17 + TimescaleDB and no database provisioning is needed. To bring your own instead, PostgreSQL 16 or 17 is recommended (developed and validated against PostgreSQL 17) with a database and a login the service can create tables in.
- **TimescaleDB is optional and auto-adopted.** If the extension is installed (or pre-created by an administrator) in the store database, the service detects it at startup and automatically converts the collector tables to hypertables with compression; without it, the service runs in plain-PostgreSQL mode, which is fully supported. No configuration flag either way.
- **.NET 10** to build and run.

Build from the repository root:

```
dotnet build Darling/PerformanceMonitor.Darling.Service/PerformanceMonitor.Darling.Service.csproj -c Release
```

```
dotnet build Darling/PerformanceMonitor.Darling.Viewer/PerformanceMonitor.Darling.Viewer.csproj -c Release
```

### Configure darling.json

The service reads one JSON file. It resolves the path in this order:

1. An explicit path (when a component is handed one)
2. The `DARLING_CONFIG` environment variable
3. `darling.json` next to the service binary

Copy the shipped `darling.sample.json` (it lands next to the built binary) to `darling.json` and edit. Comments and trailing commas are allowed; property names are case-insensitive.

Minimal working example — one server, integrated auth, bring-your-own PostgreSQL. (With the bundled store instead, replace the `postgres` block with `"postgres": { "managed": true }` and skip provisioning entirely — see [Managed Bundled PostgreSQL](#managed-bundled-postgresql).)

```json
{
  "postgres": {
    "connectionString": "Host=localhost;Port=5432;Username=darling;Database=darling"
  },
  "servers": [
    {
      "name": "SQL2022",
      "host": "SQL2022",
      "auth": "integrated",
      "excludedDatabases": []
    }
  ]
}
```

**Integrated auth (recommended).** The service connects to monitored servers as the Windows account the service runs under. Grant that account the [permissions below](#permissions-on-monitored-servers).

**SQL auth.** Set `"auth": "sql"`, a `username`, and an `encryptedPassword` produced by the `--encrypt-password` verb:

```
PerformanceMonitor.Darling.Service.exe --encrypt-password
```

It prompts for the password on stdin (so the plaintext never lands in your shell history) and prints a base64 DPAPI blob. Paste that blob into the server's `"encryptedPassword"`. The blob is protected with **DPAPI LocalMachine scope**, so an administrator can encrypt it interactively and the service account can decrypt it later on the same machine — but it is machine-bound: run `--encrypt-password` **on the machine that will run the service**, and re-encrypt if you move `darling.json` to another machine. A plaintext `"password"` also works as a dev convenience, but the service logs a warning every time it is used.

**excludedDatabases** (per server) removes databases from collection: per-database collectors skip them and the exclusion is spliced into the collector queries — the same filter Lite applies. There is a second, separate `alerts.excludedDatabases` list that excludes databases from blocking/deadlock/long-running-query **alert evaluation** without affecting collection.

### Validate the Config (Pre-flight)

Before installing the service, check that `darling.json` is well-formed and that every monitored server is reachable with the configured credentials:

```
PerformanceMonitor.Darling.Service.exe --test-connection
```

(`--validate-config` is an alias.) It validates the file, then connects to and probes each server, printing a `[PASS]`/`[FAIL]` line per server (SQL major version, engine edition, and whether the account has msdb access for failed-job alerts). It exits `0` only when the file is valid **and** every server is reachable, so it doubles as a deployment gate. Add an explicit config path as a second argument if `darling.json` is not next to the exe and `DARLING_CONFIG` is not set. This is the same probe the Viewer's **Test Connection** button runs through the service.

### Run It — Console Mode

The same executable serves interactive debugging and service installation; the Windows-service lifetime is a no-op when run from a console.

```
Darling\PerformanceMonitor.Darling.Service\bin\Release\net10.0\PerformanceMonitor.Darling.Service.exe
```

Watch the log output: you should see the config load (`Loaded configuration from ...`), the store migrate (`Postgres store ready (schema v15, ...)`), the TimescaleDB detection result, per-server connects, and then per-collector run lines with row counts.

### Install as a Windows Service

Publish (or copy the build output) to a stable path, put `darling.json` next to the exe (or set `DARLING_CONFIG` as a machine environment variable), then register it:

```
dotnet publish Darling/PerformanceMonitor.Darling.Service/PerformanceMonitor.Darling.Service.csproj -c Release -o C:\PerformanceMonitorDarling
```

```
sc create "PerformanceMonitor Darling" binPath= "C:\PerformanceMonitorDarling\PerformanceMonitor.Darling.Service.exe" start= auto obj= "NT SERVICE\PerformanceMonitor Darling"
```

```
sc start "PerformanceMonitor Darling"
```

The `obj=` clause runs the service under a **virtual service account** (`NT SERVICE\<service name>` — password-less, per-service SID, unprivileged; the same convention SQL Server itself uses). That is the right account for SQL-auth monitoring, and with `postgres.managed = true` it is more than a preference: PostgreSQL refuses to execute with administrative privileges, so don't run the service as LocalSystem — a least-privilege account keeps the bundled store's initdb/start path on ground PostgreSQL supports. For integrated auth to monitored servers, set a domain account (or gMSA) holding the SQL-side grants below instead, via **Services.msc → Log On** or `sc config ... obj=`. Note the space after `binPath=`, `start=`, and `obj=` — `sc` requires it.

One managed-mode handoff gotcha: if you test-drove the service from a console first, the bundled store's data directory belongs to *your* account, and the service account may not be able to write it. Point the service at a fresh `postgres.dataDirectory` (or delete the test directory) rather than fighting ACLs.

### What the Service Does on Monitored Servers

On each successful connect, the service:

1. **Probes the server** — one query against `sys.dm_os_sys_info` / `SERVERPROPERTY()` for version, engine edition (box / Managed Instance / Azure SQL DB), AWS RDS detection, and msdb access. It is the same detection query Lite runs, so both editions classify a server identically.
2. **Ensures two Extended Events ring-buffer sessions** (created if missing, started if stopped; ~4 MB ring buffer each, no files written on the server):
   - `PerformanceMonitor_Deadlock` — `xml_deadlock_report`, server-scoped on on-prem/Managed Instance/RDS; `database_xml_deadlock_report`, database-scoped on Azure SQL Database.
   - `PerformanceMonitor_BlockedProcess` — `blocked_process_report`, server-scoped (database-scoped on Azure SQL Database).
3. **Bootstraps the blocked-process threshold** — if `blocked process threshold (s)` is `0`, the service sets it to `5` via `sp_configure`. On AWS RDS `sp_configure` is unavailable; the attempt is tolerated and logged, and you set the threshold through an RDS Parameter Group instead (Azure SQL Database has a fixed 20-second threshold).
4. **Runs the on-connect config snapshots once** (`server_config`, `database_config`, `database_scoped_config`, `trace_flags`, `server_properties`), then runs all scheduled collectors on the shared default cadences.

Every failure in steps 2–3 is tolerated and logged: the deadlock/blocked-process collectors simply read zero rows until the sessions exist (and blocked-process reports only start arriving once the threshold is set). Monitoring queries connect with a 15-second connect budget and an application name of `PerformanceMonitorDarling`; connection encryption fails closed to `Mandatory` when the configured mode is unrecognized.

### Permissions on Monitored Servers

```sql
USE [master];
CREATE LOGIN [DarlingMonitor] WITH PASSWORD = N'YourStrongPassword';
GRANT VIEW SERVER STATE TO [DarlingMonitor];
GRANT ALTER ANY EVENT SESSION TO [DarlingMonitor];

-- Optional: SQL Agent job monitoring + failed-job alerts
USE [msdb];
CREATE USER [DarlingMonitor] FOR LOGIN [DarlingMonitor];
ALTER ROLE [SQLAgentReaderRole] ADD MEMBER [DarlingMonitor];
```

| Grant | Why | If missing |
|---|---|---|
| `VIEW SERVER STATE` | All DMV collectors (wait stats, query stats, memory, CPU, file I/O, sessions, etc.) and the connect probe | Collection fails — this one is required |
| `ALTER ANY EVENT SESSION` | Create/start the two XE sessions | Logged; deadlock and blocked-process collectors read zero rows (an admin can pre-create the sessions instead) |
| `ALTER SETTINGS` | The `sp_configure` blocked-process-threshold bootstrap | Logged; set the threshold yourself (or via RDS Parameter Group) |
| `SQLAgentReaderRole` on msdb | `running_jobs` collector and the failed/long-running-job alerts | Skipped gracefully — logged as a permissions skip, alerts return no jobs |
| `DBCC TRACESTATUS` permission | `trace_flags` snapshot | Degrades to zero rows with a warning |

**Azure SQL Database:** connect to the one database you monitor (set the server entry's `"database"`), using a contained user with `VIEW DATABASE STATE`, matching the product's existing Azure guidance. The XE sessions are created database-scoped there (`ALTER ANY DATABASE EVENT SESSION`); SQL Agent collectors are skipped automatically.

Collectors that hit a permission error (SQL errors 229/297/300) log a `PERMISSIONS` row in `collection_log` and retry on their next scheduled run — one denied collector never stops the rest.

---

## Configuration Reference

All sections except `postgres` and `servers` are optional — omit a section (or any key) to get the defaults listed here. Defaults deliberately mirror a fresh Lite install.

### postgres

Two mutually exclusive modes — setting both `managed: true` and `connectionString` is a validation error:

| Key | Default | Notes |
|---|---|---|
| `managed` | `false` | `true` runs the bundled PostgreSQL + TimescaleDB (Windows only; see [Managed Bundled PostgreSQL](#managed-bundled-postgresql)). The connection string is derived, never configured. |
| `port` | `5641` | Managed mode only: the loopback port the bundled server listens on. Deliberately uncommon so it coexists with any PostgreSQL (5432) already on the machine. |
| `dataDirectory` | *(null)* | Managed mode only: the cluster's data directory. `null` means `%ProgramData%\PerformanceMonitorDarling\pg`. |
| `connectAs` | `"admin"` | Managed mode only: which least-privilege role the Viewer connects as — `"admin"` (reads everything + manages mute rules and dismisses alerts) or `"viewer"` (read-only; those write actions are hidden/disabled). See [Security & Least-Privilege Roles](#security--least-privilege-roles). Ignored in bring-your-own mode (the connection string picks the role). |
| `connectionString` | *(required unless managed)* | Npgsql connection string for a store you provision yourself, e.g. `Host=localhost;Port=5432;Username=darling;Password=...;Database=darling` |

### servers (array, at least one entry)

| Key | Default | Notes |
|---|---|---|
| `name` | `""` | Display name; falls back to `host` |
| `host` | *(required)* | Server/instance to monitor |
| `database` | *(none)* | Azure SQL Database only: the one database this entry monitors (also part of the server's storage identity) |
| `auth` | `"integrated"` | `"integrated"` or `"sql"` |
| `username` | *(none)* | Required for `"sql"` |
| `encryptedPassword` | *(none)* | DPAPI blob from `--encrypt-password` (preferred) |
| `password` | *(none)* | Plaintext fallback — dev only, warned on every use |
| `readOnlyIntent` | `false` | Route to a readable AG secondary (`ApplicationIntent=ReadOnly`) |
| `trustServerCertificate` | `false` | |
| `encryptMode` | `"Mandatory"` | `Mandatory` / `Strict` / `Optional`; unknown values fail closed to `Mandatory` |
| `multiSubnetFailover` | `false` | |
| `excludedDatabases` | `[]` | Databases excluded from collection |

### capturePlans (boolean, optional)

| Key | Default | Notes |
|---|---|---|
| `capturePlans` | `true` | Capture execution plans into `query_stats.query_plan_xml` and `query_store_stats.query_plan_text`. PostgreSQL TOAST compresses the plan text transparently (pglz) and TimescaleDB chunk compression squeezes it further, so plans are cheap to keep — unlike Lite, which stores to DuckDB/Parquet and deliberately never captures them. Set `false` to skip plan capture (e.g. to shave storage across a very large fleet). |

### alerts

The shared alert engine's switches and thresholds. Every default mirrors Lite's alert defaults exactly, so an empty section alerts like a fresh Lite install. `enabled: false` turns off all alert evaluation **and** scheduled-analysis finding notifications (the analysis itself still runs and persists findings).

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Master switch for alert evaluation + finding notifications |
| `cpuEnabled` | `true` | |
| `cpuThresholdPercent` | `80` | |
| `cpuMode` | `"total"` | `"total"` = SQL + other processes; `"sql"` = SQL process only |
| `blockingEnabled` | `true` | |
| `blockingCountThreshold` | `1` | Blocked-process count (rolling window) that trips the alert |
| `deadlockEnabled` | `true` | |
| `deadlockCountThreshold` | `1` | Deadlock count (rolling window) that trips the alert |
| `poisonWaitEnabled` | `true` | THREADPOOL / RESOURCE_SEMAPHORE / RESOURCE_SEMAPHORE_QUERY_COMPILE |
| `poisonWaitThresholdMs` | `500` | Average ms per wait |
| `longRunningQueryEnabled` | `true` | |
| `longRunningQueryThresholdMinutes` | `30` | |
| `tempDbSpaceEnabled` | `true` | |
| `tempDbSpaceThresholdPercent` | `80` | |
| `lowDiskEnabled` | `true` | Volume free space; graded CRITICAL when critically low |
| `lowDiskThresholdPercent` | `10` | Fire below X% free; `0` disables this dimension (clamped 0–100) |
| `lowDiskThresholdGb` | `5` | Fire below X GB free; `0` disables this dimension |
| `longRunningJobEnabled` | `true` | SQL Agent job running long vs. its history |
| `longRunningJobMultiplier` | `3` | Fires at 3x the job's historical average |
| `failedJobEnabled` | `true` | Live msdb check for recently failed jobs |
| `failedJobLookbackMinutes` | `60` | Clamped 1–1440 |
| `cooldownMinutes` | `5` | Minimum minutes between repeats of the same alert condition (clamped 1–120) |
| `excludedDatabases` | `[]` | Excluded from blocking/deadlock/long-running-query **alert evaluation** (collection unaffected) |

Not configurable (hardcoded to Lite's defaults until someone needs a knob): the long-running-query read shape (top 5 results; the five noise filters — sp_server_diagnostics, WAITFOR, backups, misc waits, CDC — all on) and the analysis-finding notification policy (notify at severity >= 1.5, 6-hour per-finding cooldown).

### smtp

Email delivery is enabled when `host`, `from`, and `to` are all set — there is no separate enable flag.

| Key | Default | Notes |
|---|---|---|
| `host` | `""` | |
| `port` | `587` | |
| `useSsl` | `true` | |
| `username` | *(none)* | For authenticated relays |
| `encryptedPassword` | *(none)* | Same `--encrypt-password` DPAPI pattern as SQL auth |
| `from` | `""` | |
| `to` | `""` | Comma-separated recipients |
| `emailCooldownMinutes` | `15` | Email/webhook channel cooldown (clamped 1–120) |

### webhooks

A channel is enabled by a non-empty URL.

| Key | Default | Notes |
|---|---|---|
| `teamsUrl` | `""` | Teams incoming webhook |
| `teamsProxy` | `""` | Optional proxy address |
| `slackUrl` | `""` | Slack incoming webhook |
| `slackProxy` | `""` | Optional proxy address |

### mcp

The embedded MCP server, over Streamable HTTP bound to `localhost` only. It exposes the same tool names Lite and the Dashboard expose:

- **Six diagnostic-analysis tools** — `analyze_server`, `get_analysis_facts`, `compare_analysis`, `audit_config`, `get_analysis_findings`, `mute_analysis_finding`.
- **Five plan-analysis tools** — `analyze_query_plan` (by `query_hash`), `analyze_procedure_plan` (by `sql_handle`), `analyze_query_store_plan` (by `database_name` + `query_id`), `analyze_plan_xml` (raw showplan XML, no fetch), and `get_plan_xml` (raw stored plan XML by `query_hash`). These run the shared execution-plan analyzer over the plan XML the collectors already captured into the store — a stored-plan read, never a live query against the monitored server. `analyze_query_plan`/`get_plan_xml` accept an optional `database_name`, and `analyze_query_store_plan` an optional `plan_id`, to pin the exact stored plan when the caller knows it.
- **Fourteen core data-read tools** — the diagnostic reads an assistant needs to investigate a server, each a stored read of the collected data (never a live query against the monitored server):
  - *Resource metrics* — `get_cpu_utilization`, `get_wait_stats`, `get_wait_trend`, `get_memory_stats`, `get_memory_clerks`, `get_file_io_stats`, `get_tempdb_trend`, `get_perfmon_stats`.
  - *Query performance* — `get_top_queries_by_cpu`, `get_top_procedures_by_cpu`, `get_query_store_top` (these hand back the `query_hash` / `sql_handle` / `query_id` + `plan_id` keys the plan-analysis tools consume).
  - *Discovery / health* — `list_servers` (with collection-freshness status), `get_collection_health`, `get_server_properties`.

  These are the tools the analysis findings' `next_tools` recommendations point at, so a client following a finding's advice resolves them on this same server. Result shapes match Lite's (the store is Lite's collector schema); where Lite and the Dashboard's shapes diverge, Darling follows Lite — the shape its collector-mirror store can serve faithfully.
- **Fifteen diagnostic-depth data-read tools** — deeper reads for a blocking / deadlock / session / configuration / storage investigation, each a stored read:
  - *Blocking / deadlocks* — `get_blocking` (blocked/blocking pairs from the blocked-process-report XE + the always-on DMV fallback), `get_deadlocks`, `get_deadlock_detail` (raw graph XML), `get_blocked_process_xml` (raw report XML).
  - *Sessions* — `get_session_stats` (latest per-application connection counts), `get_active_queries` (captured running-query snapshots), `get_waiting_tasks`.
  - *Config history* — `get_server_config_changes`, `get_database_config_changes`, `get_trace_flag_changes`, and `get_database_scoped_config` (latest snapshot).
  - *Index / object* — `get_table_index_sizes` (size + growth), `get_index_usage` (Unused / Write-only / Active), `get_object_locking` (lock/latch contention), `get_database_sizes`.

  The three config-change tools diff the store's config snapshots. This edition captures configuration **when the service connects** to a server (not on a fixed schedule), so a change is detected between two connect snapshots and at least two are needed — a stable, always-connected deployment may show no changes until the next connect. They emit only the values the collectors capture; the Dashboard's `requires_restart` / setting `description` / `setting_type` / generated change-narrative enrichment is not collected here and is omitted. The Dashboard's `get_blocking_deadlock_stats` aggregate is **not** hosted (Darling has no blocking/deadlock rollup table — use `get_blocking` / `get_deadlocks` for the raw events).

- **Seven resource-contention + jobs data-read tools** — deeper reads for an internal-contention / worker-thread / plan-cache / SQL Agent investigation, each a stored read of the latest collected snapshot:
  - *Latch / spinlock* — `get_latch_stats` (top latch classes by wait time, per-second rates), `get_spinlock_stats` (top spinlocks by collisions).
  - *Memory grants* — `get_resource_semaphore` (workspace-memory target / max-target ceiling vs granted / used), `get_memory_grants` (per-pool grant detail).
  - *Plan cache / scheduler* — `get_plan_cache_bloat` (single-use vs multi-use + bloat level), `get_cpu_scheduler_pressure` (runnable queue, worker utilization, pressure level).
  - *Jobs* — `get_running_jobs` (running SQL Agent jobs vs historical average / p95).

  The Dashboard's per-class latch `severity` / `description` / `recommendation`, spinlock `description`, plan-cache `bloat_level`, and CPU-scheduler `pressure_level` / `recommendation` are the Dashboard / reporting-view CASE derivations (not collected columns), reproduced service-side so the full result shape is served. Darling's delta collectors store no `sample_interval_seconds`, so per-second latch/spinlock rates are derived from the collection interval, and the Dashboard's `get_resource_semaphore` `sample_interval_seconds` is not emitted for the same reason (`max_target_memory_mb`, the workspace-memory ceiling, is added since the store carries it).

- **Five trend data-read tools** — windowed time-series siblings of the core reads, each a stored read of the collected series over the window (BOTH-sides, naive-UTC):
  - `get_memory_trend` (total / target server memory, buffer pool, plan cache over time), `get_perfmon_trend` (a single counter's value + delta, `counter_name` required), `get_file_io_trend` (per-database read/write latency, top-10 busiest files), `get_query_trend` (one query's per-collection history by `query_hash` + `database_name`), `get_query_duration_trend` (overall elapsed-ms/sec + executions/sec).

  Each mirrors the viewer's proven chart read (byte-identical Postgres SQL); the shape follows Lite where the SKUs diverge. `get_perfmon_trend` reproduces Lite's miss vocabulary (Page Life Expectancy is intentionally not collected; an unknown counter hands back the collected names). `get_memory_trend` carries a `total_granted_mb` field for field-for-field parity with Lite, where its memory_stats-only read leaves it 0 (the grant overlay is a separate chart series).

- **Eight system-health parse-on-read tools** — the Dashboard's `get_health_parser_*` family, over Darling's raw `system_health_events`:
  - `get_health_parser_system_health` (corruption + contention counters), `get_health_parser_severe_errors` (severity ≥ 19, with `database_id` resolved to a name), `get_health_parser_scheduler_issues`, `get_health_parser_memory_conditions`, `get_health_parser_memory_broker`, `get_health_parser_memory_node_oom`, `get_health_parser_cpu_tasks`, `get_health_parser_io_issues`.

  Where the Dashboard reads its server-side-parsed `collect.HealthParser_*` tables, these shred the raw extended-event XML **on read** with the shared `SystemHealthParser` (the same parser the viewer's System Events tab uses) and gate with the service-side twin of the viewer's `SystemEventSignificance` — returning the same SIGNIFICANT warning set the Dashboard surfaces (sp_HealthParser at `@warnings_only = 1`). `get_health_parser_system_health` is the one UNGATED category (its counter series plots every snapshot). Each row carries the full sp_HealthParser column set keyed on the event's `event_time`; the tools window on `event_time` (the event's real time), so "last 24 hours" means events that happened in the last 24 hours.

| Key | Default | Notes |
|---|---|---|
| `enabled` | `false` | **Off by default** — a headless service does not open a local port unless you ask |
| `port` | `5152` | Chosen so all three editions coexist on one machine (Dashboard 5150, Lite 5151) |

Register with Claude Code:

```
claude mcp add --transport http --scope user sql-monitor-darling http://localhost:5152/
```

If the port is already in use at startup, the MCP server logs an error and does not start; collection is unaffected.

### No Schedule Knobs, by Design

There are deliberately **no collection-schedule or retention settings** in `darling.json`. The service consumes the shared per-collector defaults (`CollectorScheduleDefaults`) — the same cadences and retention horizons a fresh Lite install uses, identity-pinned by tests so the two editions cannot drift. If a schedule knob is ever genuinely needed, it will be added then, not speculatively.

---

## Operations

### The Store

The service migrates the store itself at startup — plain versioned SQL scripts, each applied once inside its own transaction, tracked in `darling_schema_version`, safe under concurrent starters (advisory-locked). Current schema is **v15**:

| Version | Contents |
|---|---|
| **V1** — collector tables | One table per collector, all 32, generated from the shared collector definitions (column-for-column identical to Lite's DuckDB schema): `wait_stats`, `latch_stats`, `spinlock_stats`, `query_stats`, `procedure_stats`, `query_store_stats`, `query_snapshots`, `plan_cache_stats`, `cpu_utilization_stats`, `cpu_scheduler_stats`, `file_io_stats`, `memory_stats`, `memory_clerks`, `memory_pressure_events`, `tempdb_stats`, `perfmon_stats`, `deadlocks`, `blocked_process_reports`, `dmv_blocking_snapshots`, `memory_grant_stats`, `waiting_tasks`, `session_stats`, `session_summary_stats`, `running_jobs`, `database_size_stats`, `index_object_stats`, `server_properties`, `system_health_events`, and the four config snapshots (`server_config`, `database_config`, `database_scoped_config`, `trace_flags`) |
| **V2** — observability | `servers` (registry, upserted on every successful connect: identity, display name, engine edition, major version) and `collection_log` (one row per collector run: SUCCESS / PERMISSIONS / ERROR, row count, SQL-phase and storage-phase timings) |
| **V3** — alerting | `config_alert_log` (one history row per fired alert), `config_edge_trigger_watermarks` (restart-surviving edge-trigger and failed-job watermarks), `config_mute_rules` (alert mute rules; starts empty) |
| **V4** — analysis | `analysis_findings` (persisted findings incl. the stored remediation action), `analysis_muted` (muted finding patterns), and 17 `v_<table>` passthrough views so the shared analysis SQL runs verbatim against this store |
| **V5** — viewer passthrough views | The five remaining `v_*` passthrough views (`v_running_jobs`, `v_server_config`, `v_database_scoped_config`, `v_trace_flags`, `v_collection_log`) that complete the viewer's read layer |
| **V6** — memory passthrough views | `v_memory_clerks` and `v_memory_pressure_events`, the two views the Memory tab reads |
| **V7** — plan-capture columns | Nullable plan-XML columns for the viewer's View Plan surfaces: `procedure_stats.query_plan_xml`, `blocked_process_reports.blocked_query_plan_xml` / `blocking_query_plan_xml`, `deadlocks.victim_query_plan_xml` |
| **V8** — schema split (collect/config) | Moves the tables into the `collect` and `config` schemas (least-privilege security split); the shared SQL keeps using bare names, resolved via `search_path = collect, config, public` |
| **V9** — inventory + cost fields | `server_properties` inventory columns (`sqlserver_start_time`, `host_os_version`, `ag_replica_role`) and `servers.monthly_cost_usd` (the FinOps per-server budget) |
| **V10** — latch + spinlock collectors | `latch_stats` and `spinlock_stats` tables plus their `v_*` views |
| **V11** — CPU scheduler + plan cache collectors | `cpu_scheduler_stats` and `plan_cache_stats` tables plus their `v_*` views |
| **V12** — session summary collector | `session_summary_stats` (server-wide connection-leak / idle signal) table plus its `v_*` view |
| **V13** — system health events collector | `system_health_events` (raw `system_health` Extended Events capture) table plus its `v_*` view |
| **V14** — refresh passthrough views | `CREATE OR REPLACE` on every `v_*` view so a store upgraded across a column-adding migration picks up the new columns (Postgres freezes a view's `SELECT *` expansion at create time) |
| **V15** — index metadata columns | Per-index definition columns on `index_object_stats` (ordered key/included column lists, filter, uniqueness/constraint/FK flags, `is_disabled`, and the reconstruct-a-CREATE options — compression, fill factor, page/row locks, etc.) for monitor-side UNUSED/DUPLICATE index analysis, and refreshes `v_index_object_stats` |

All timestamps in the store are **naive-UTC** `timestamp` columns — the product-wide cross-store contract (Lite's DuckDB does the same).

### TimescaleDB (Optional, Auto-Adopted)

At startup, right after migration, the service attempts `CREATE EXTENSION IF NOT EXISTS timescaledb` and checks `pg_extension`:

- **Present** — every collector table is converted to a hypertable (partitioned on its own time column, existing rows migrated) and gets a compression policy: chunks older than **7 days** compress automatically (segmented by `server_id`). Compressed chunks stay fully queryable — this is Darling's archival tier, the centralized-store answer to Lite's Parquet archive. Everything is idempotent and re-converges on every service start; a table that fails conversion stays a plain table and keeps working.
- **Absent** — the service logs one Information line and runs in plain-PostgreSQL mode, which is a fully supported configuration, not a degraded one.

`IF NOT EXISTS` short-circuits before privilege checks, so a store whose administrator pre-created the extension works for a service login that could never create it.

### Retention

A purge runs on the first sweep after startup and then daily, driven by the same shared per-collector horizons Lite uses:

| Horizon | Tables |
|---|---|
| 7 days | `query_snapshots`, `waiting_tasks`, `running_jobs` |
| 30 days | Most collector tables (wait/query/procedure/Query Store stats, CPU, memory, file I/O, tempdb, perfmon, deadlocks, blocking, sessions, config snapshots), plus `collection_log` and `analysis_findings` |
| 90 days | `database_size_stats`, `index_object_stats` |
| 365 days | `server_properties` |

On plain PostgreSQL the purge is DELETE-based. With TimescaleDB it switches to `drop_chunks` — a metadata-only detach of whole expired chunks (rows inside a partially-expired chunk survive until the whole chunk ages out; up to ~7 days of grace at the default chunk width), with a per-table DELETE fallback for any table that is not a hypertable. Failure-isolated per table: one stuck purge is logged and retried the next day without stopping the sweep.

### Logs

The service logs through standard .NET hosting: console output when run interactively; when installed as a Windows service, lifecycle and log events go to the **Windows Application event log** (standard `AddWindowsService` behavior). Collection outcomes are also queryable in the store itself — `collection_log` records every collector run per server with status and timings, and the viewer's Collection Health tab renders exactly that.

### The Viewer

`PerformanceMonitor.Darling.Viewer.exe` is a WPF app that talks **only to the PostgreSQL store** — it never connects to your monitored SQL Servers. It reads the same `darling.json` the service uses, but only the `postgres` section, resolved in the same order (explicit path, then `DARLING_CONFIG`, then `darling.json` next to the binary) plus one viewer-only fallback: the parent directory, so the release zip's layout — viewer in a `viewer\` subfolder, `darling.json` beside the service exe — works with no setup. A viewer seat on another machine needs only a minimal `darling.json` containing the `postgres.connectionString`. If the file is missing it shows a hint instead of crashing.

The layout mirrors the Lite desktop app: a left sidebar lists the servers from the `servers` registry the service maintains, and the top tab strip holds three fixed **aggregate tabs** — Overview, Recommendations, and Alerts — alongside a closable **per-server tab** for each server you open. Overview (the all-servers server-cards grid) and Alerts (the all-servers alert history) span every server; Recommendations has its own server selector, independent of the sidebar. **Double-click a server** in the sidebar — or **double-click its Overview card** — to open (or focus) its tab, and close it with the × on the tab header; an empty-state panel is shown until the store has at least one server.

Each per-server tab has fourteen inner tabs:

| Inner tab | Contents |
|---|---|
| **Overview** | Five correlated, X-axis-synced timeline lanes over the last 24 hours — CPU % (SQL Server vs SQL+other Total), total wait ms/sec, blocking + deadlocking, buffer pool MB, and file-I/O latency — each with a ±2σ baseline band and anomaly markers, all sharing one crosshair so a spike in one lane lines up against the others |
| **Wait Stats** | A searchable wait-type picker (poison + usual-suspect + `PAGELATCH_` defaults, checked-to-top, a 30-type selection guide) beside a per-**type** trend chart for the checked types over the last 24 hours, with a Wait Time (ms/sec) ↔ Avg Wait Time (ms/wait) metric toggle — the per-type companion to the Overview's single total-wait lane |
| **Queries** | Six sub-tabs over the last 24 hours — **Performance Trends** (a 2×2 of per-second trend charts: query duration, procedure duration, Query Store duration, execution count), **Active Queries** (the ~26-column filterable snapshot grid of captured running queries with a time-range slicer, a **Latest Snapshot** button that re-reads the newest stored capture, and per-row Estimated / Actual plan buttons that open the stored plan in the Plan Viewer), **Top Queries by Duration** (the full query-stats grid with in-grid bar cells for executions/CPU/duration/reads and a CPU-by-database breakdown), **Top Procedures by Duration**, **Query Store by Duration**, and **Query Heatmap** (query counts per 5-minute bin × per-execution magnitude bucket, by a chosen metric; right-click a cell to drill into Active Queries for that window) — the three grids each carry a time-range slicer (drag to narrow the window) and a shared **Compare** control that overlays the current window against a baseline period (yesterday, last week, or same day last week), flagging new and vanished queries |
| **Plan Viewer** | Hosts execution plans as closable sub-tabs (the shared plan-viewer control, the same one Lite and the Dashboard use). Right-click a **Top Queries** or **Query Store** row and choose **View Plan** to open the plan the service captured for it (`query_stats.query_plan_xml` / `query_store_stats.query_plan_text`); Top Queries rows also carry a **Query Plan** column whose Download button saves the stored plan as a `.sqlplan` file (enabled only when a plan was captured). Top Procedures and the blocking / deadlock reports deliberately do **not** surface a plan here — procedure plans aren't stored, and blocked-process / deadlock rows carry only a `sql_handle` (not plan XML); resolving either to a plan needs a live SQL connection the viewer never makes. "Get Actual Plan" (a live re-execution) is likewise out |
| **CPU** | Raw per-sample CPU utilization (SQL Server vs other processes) over the last 24 hours — every ring-buffer sample, full-bleed as two series; the Overview's CPU lane plots the same raw samples compactly (SQL vs SQL+other Total) with a baseline |
| **Memory** | Four sub-tabs over the last 24 hours — **Overview** (a summary strip of physical / SQL Server / target / buffer pool / plan cache / page-file memory plus the system memory state and model, over a Total-vs-Target-vs-Buffer-Pool memory trend with a memory-grants overlay), **Memory Clerks** (a searchable clerk-type picker — top-5 default, checked-to-top, clear-only-the-filtered — beside a per-clerk memory trend for the checked clerks with a non-buffer-pool total and top-clerk summary), **Memory Grants** (per-resource-pool grant sizing — available / granted / used MB — and activity — grantees / waiters / timeouts / forced grants), and **Memory Pressure Events** (hour-bucketed stacked bars of `RING_BUFFER_RESOURCE_MONITOR` pressure, SQL Server vs OS, medium vs severe) |
| **File I/O** | Two sub-tabs over the last 24 hours — **Latency** (per-file read and write latency, with a dashed queued-I/O overlay) and **Throughput** (per-file read and write MB/s) — the top 10 files by activity |
| **tempdb** | Three stacked charts over the last 24 hours — space usage (user / internal objects / version store), total allocated size, and per-file I/O latency |
| **Blocking** | Four sub-tabs over the last 24 hours — **Trends** (lock-wait rate, blocking incidents, deadlocks), **Current Waits** (waiting-task duration by wait type, blocked sessions by database), **Blocked Process Reports** (the full ~25-column filterable grid — XE reports preferred with the always-on DMV blocking snapshot merged in as fallback, each row badged with its source, a time-range slicer, per-row report-XML save, and long-block highlighting; double-click or right-click **View Block Chain** to reconstruct and draw the blocking chain the row belongs to), and **Deadlocks** (one filterable row per process parsed from each deadlock graph, a slicer, per-row graph-XML save; double-click or right-click **View Deadlock Graph** to draw the deadlock graph) |
| **Perfmon** | A searchable counter picker with the shared counter packs (General Throughput, Memory Pressure, CPU / Compilation, I/O Pressure, TempDB Pressure, Lock / Blocking) beside a per-counter delta trend for the checked counters (up to 12) over the last 24 hours |
| **Running Jobs** | Latest snapshot of currently-running SQL Agent jobs — start time, current vs average vs p95 duration, % of average, and a highlighted row when a job is running past its p95 (a store-derived banner appears when the service's login lacks msdb access) |
| **Configuration** | Four column-filterable snapshot grids of the server's latest capture — server configuration (`sys.configurations`), database configuration (28 columns of `sys.databases`), database-scoped configuration, and trace flags |
| **Daily Summary** | A one-row roll-up of the selected day (default today, UTC, with a date picker) — total wait time, the top wait type, distinct query count, deadlock / blocking-event / high-CPU-sample counts, collector errors, and an overall health band |
| **Collection Health** | Three sub-tabs — **Health Summary** (a 7-day per-collector roll-up: run / success / error counts, failure rate, average duration, last success / run / error, and a health band of HEALTHY / WARNING / STALE / FAILING / NEVER_RUN / NO_PERMISSIONS — double-click a collector to open its full run history), **Collection Log** (the recent run log with per-run SQL and store-write timings and row counts), and **Duration Trends** (a per-collector success-duration scatter) |

The three aggregate tabs — **Overview** and **Alerts** span every server; **Recommendations** has its own server selector, independent of the sidebar:

| Tab | Contents |
|---|---|
| **Overview** | A card per registered server (all servers, not the sidebar selection): server name + status dot, CPU (total non-idle with the SQL-only number alongside), memory, blocking and deadlock counts over the last hour, and last-collection time, each colour-banded (CPU ≥ 80% red / ≥ 50% amber / green; blocking and deadlocks red-or-amber when present) with a red **Offline** overlay. Status is derived from **collection freshness** — the newest `collection_log` age — rather than a live ping (the viewer never connects to the monitored servers): fresh is Online, older than twice the fastest collector's one-minute cadence is a Warning, and no recent collection is Offline. **Double-click a card** to open that server's tab. Refreshes every 30 seconds |
| **Recommendations** | The latest analysis run's findings for the tab's **own selected server** — a server selector independent of the sidebar, a Refresh button, and a status line showing the last analysis time — re-skinned to Lite's advise-only **card** design: a scrollable list of collapsible **incident** sections, each holding severity-banded cards (a severity badge, the affected `[database]`, the title, and the advice). Every card offers **Ask AI** (copies an MCP investigation prompt referencing `analyze_server` / `get_analysis_findings`); a card whose stored remediation carries a copy-paste statement also offers **Copy fix** (copies the suggested T-SQL). Advise-only — the viewer never applies anything, and there is no mute affordance here (alert muting lives on the Alerts surface). There is no in-app "Generate now": the service runs analysis on its own 30-minute cadence, so the status line surfaces the last analysis time instead |
| **Alerts** | The full alert history from `config_alert_log` across **all servers** (newest first, selectable time range), with a Server column and a Server filter. Double-click a row (or **View Details**) for a modal detail window showing the alert's stored detail and structured advice / remediation / drill-down from its dedup-fingerprint context. **Dismiss Selected / Dismiss All** hide alerts from the view (a durable `dismissed` flag on `config_alert_log`); column filters, Copy Cell/Row/All, and Export to CSV match Lite's grid. Right-click to **Mute This Alert** or **Mute Similar** (metric-only), and a **Manage Mute Rules** button opens the mute-rule editor |

Only the visible tab loads (Lite's visible-only rule). The Alerts tab and the visible server tab's active inner tab refresh every 60 seconds; the Overview refreshes on its own faster 30-second timer (Lite's Overview cadence); and **Recommendations** refreshes on tab activation, its Refresh button, and its own server-selector change only, never on the timer — its findings change on the service's 30-minute analysis cadence, so a 60-second auto-refresh would be pointless churn (and would reset the incident expanders under the reader), matching Lite.

The viewer is read-only over collected data, but it does perform a small set of **user-initiated writes** — and those go straight to the PostgreSQL store, which is the coordination point (the service honors them on its next read; there is no viewer-to-service channel). From the Alerts tab, creating a mute rule from an alert (**Mute This Alert** / **Mute Similar**) or adding, editing, toggling, deleting, or purging one via **Manage Mute Rules** writes `config_mute_rules` (a rule scopes to a server by name, exactly as Lite's mute rules do); and **dismissing alerts** sets the `dismissed` flag on `config_alert_log` so they drop out of the Alert History view (a single atomic UPDATE — Darling has no parquet archive tier, so there is no dismissed-archive sidecar). The viewer never writes collector data.

### Restart Semantics

The service is built to restart cleanly, any time:

- **Delta continuity** — delta-based collectors (wait stats, file I/O, perfmon, memory grants) re-seed their baselines from the store at startup, so the first cycle after a restart produces real deltas instead of zeroes.
- **Alert no-re-fire** — edge-trigger watermarks and the failed-job watermark persist in `config_edge_trigger_watermarks`, and per-alert cooldowns re-seed from `config_alert_log`, so a restart does not replay alerts you already received.
- **Idempotent store setup** — migrations are versioned and skip what is already applied; TimescaleDB conversion and compression policies re-converge as no-ops.
- **Per-connect snapshots** — the on-connect config snapshot collectors run once per (re)connect, mirroring Lite's server-open behavior.
- Mute rules (`config_mute_rules`) load once at service startup — restart the service after adding rows.

A monitored server that is down is retried every 60 seconds forever; a collector that errors is logged and retried at its next scheduled time; a mid-cycle connection-level failure forces a clean reconnect and re-probe. The loop never dies for one bad cycle.

---

## Troubleshooting

**"Cannot load configuration"** (critical, service idles) — no `darling.json` was found at the resolved path. The message names the path it tried; copy `darling.sample.json` there or point `DARLING_CONFIG` at your file.

**"Configuration problem: ..."** (critical, service idles) — validation failed. The messages are literal and per-field, e.g. `postgres.connectionString is required.`, `servers must contain at least one entry.`, `server 'X': host is required.`, `server 'X': sql auth requires username.`, `server 'X': sql auth requires encryptedPassword (preferred; see --encrypt-password) or password.`, `server 'X': auth must be 'integrated' or 'sql'`. Fix the file and restart the service.

**"Cannot reach or migrate the Postgres store"** (critical, service idles) — the store connection string is wrong, PostgreSQL is down/unreachable, or the login cannot create tables. Collection does not start until this succeeds; fix and restart.

**"uses a plaintext password in darling.json"** (warning, every connect) — you set `"password"` instead of `"encryptedPassword"`. It works, but run `--encrypt-password` on the service machine and switch.

**DPAPI decrypt fails after moving darling.json** — `encryptedPassword` blobs are machine-bound (DPAPI LocalMachine). Re-run `--encrypt-password` on the new machine.

**"Failed to ensure XE sessions"** — the login lacks `ALTER ANY EVENT SESSION` (or the database-scoped equivalent on Azure SQL Database). Deadlock and blocked-process collection read zero rows until the sessions exist; grant the permission or have an administrator create/start `PerformanceMonitor_Deadlock` and `PerformanceMonitor_BlockedProcess`. "Already exists / already started" XE errors are logged as benign and mean the sessions are up.

**Blocked-process reports empty** — the blocked-process threshold may still be 0. On AWS RDS set `blocked process threshold (s)` via a Parameter Group (the `sp_configure` bootstrap cannot run there); on Azure SQL Database the threshold is fixed at 20 seconds. Blocking stays visible either way through the always-on DMV blocking snapshot.

**`PERMISSIONS` rows in `collection_log`** — that collector's reads were denied (SQL errors 229/297/300). Check the [permissions](#permissions-on-monitored-servers); the collector retries every cycle and recovers as soon as the grant lands.

**"Skipping recently-failed-job check"** (info) — the login has no msdb / `SQLAgentReaderRole` access, so failed-job alerts are skipped. Expected for minimal-privilege monitoring logins; grant the role if you want job alerts.

**"TimescaleDB setup failed — continuing in plain-PostgreSQL mode"** (warning) — the extension exists but conversion hit a problem. Everything still works (DELETE-based retention, plain tables); conversion is retried on the next service start.

**MCP client cannot connect** — `mcp.enabled` defaults to `false`; set it to `true` and restart. If the log says `Port 5152 is already in use — MCP server not started`, change `mcp.port`. The MCP server binds to `localhost` only and does not accept remote connections.

**Recommendations tab says no findings** — analysis runs every 30 minutes per server but only once the store holds at least 24 hours of collected data for that server; a fresh install simply has not earned findings yet.

---

## How It Runs (Reference)

Fixed cadences, hardcoded on purpose:

| What | Cadence |
|---|---|
| Collector sweep loop | Every 15 seconds (each collector runs when its own shared schedule is due — most every 1 minute, some every 5, sizes hourly, index stats daily) |
| Alert evaluation | Every 30 seconds per connected server (Lite's overview cadence) |
| Scheduled analysis | Every 30 minutes per server, 120-second budget, analyzing the last 4 hours; findings persist to `analysis_findings` and high-severity ones notify through the configured channels |
| Retention purge | First sweep after startup, then daily |
| Reconnect attempts | Every 60 seconds while a server is unreachable |

---

## Managed Bundled PostgreSQL

With `postgres.managed = true` (the sample's default), the service runs its own bundled PostgreSQL 17 + TimescaleDB and a from-zero install needs no database provisioning at all. Windows only, like every DPAPI surface here.

```json
{
  "postgres": {
    "managed": true,
    "port": 5641,
    "dataDirectory": null
  }
}
```

**What first run does.** The service looks for `pg-runtime\pgsql\` beside its binary, extracting it from `pg-runtime.zip` when only the zip is present (deleting the extracted directory is therefore always safe — it self-heals). If the data directory has no cluster, it generates a 32-character random password, protects it with DPAPI LocalMachine into `pg-credential.dpapi` beside the data directory (credential first, so a crash mid-initdb never strands a cluster nobody can log into), then runs `initdb` with `scram-sha-256` auth, data checksums, and UTF8/C locale. A marker-guarded block appended to `postgresql.conf` preloads TimescaleDB, sets the port, and restricts listening to `127.0.0.1`; a second versioned block sizes background workers up for the per-hypertable compression jobs (`timescaledb.max_background_workers = 28`, `max_worker_processes = 40` — PostgreSQL's default of 8 workers cannot launch them). Both appends are re-checked on every start, so a crash between initdb and the append heals itself instead of silently degrading — and clusters initialized before the worker sizing existed gain it on their next start (effective at the next PostgreSQL restart). Then `pg_ctl start`, `CREATE DATABASE darling`, and the normal startup path (migrations, TimescaleDB adoption — you should see `32/32 collector table(s) are hypertables`) continues exactly as in bring-your-own mode. The connection string is derived from the stored credential; the Viewer and the MCP host on the same machine derive it the same way, so nothing needs configuring there either.

**Why scram and not trust, even loopback-only.** Trust auth would hand superuser to any local code that can open a loopback socket — every other local user, and network-capable-but-not-filesystem-capable attack primitives like SSRF from a co-hosted app. With scram the credential travels on the wire, failed attempts are auditable, and access is confined to what can read the DPAPI-protected credential file. `listen_addresses = '127.0.0.1'` keeps the server unreachable off the machine on top.

**Lifecycle.** On shutdown the service stops the server (`pg_ctl stop -m fast`) **only when it started it**. A server that was already running — an operator's own `pg_ctl`, or a postmaster that survived a service crash — is adopted for connections but never stopped: you'll see `already running … will not stop it` in the log, and the service keeps collecting into it.

**The runtime zip.** `pg-runtime.zip` ships beside the service binary in packaged releases. Building from source, produce it once with `Darling\tools\fetch-pg-runtime.ps1` — it downloads the pinned EDB PostgreSQL 17 binaries and TimescaleDB, verifies their SHA256, prunes what the service doesn't need, and writes the zip to `Darling\artifacts\`; copy it next to the built service exe.

**Server log.** The bundled server's own log is `pg.log` beside the data directory — that's where PostgreSQL explains a refused start; bootstrap errors in the service log quote its tail.

## Security & Least-Privilege Roles

The store is split into two schemas so that no consumer connects with more privilege than it needs:

- **`collect`** — the 32 collector hypertables plus the service-written, user-read metadata (`servers`, `collection_log`, `analysis_findings`, the `v_*` views). Read-only to everyone but the service.
- **`config`** — exactly the tables a human operator changes through the Viewer or MCP: `config_mute_rules`, `config_alert_log` (alert dismissals), `config_edge_trigger_watermarks`, and `analysis_muted`.

Table names are unchanged — only their schema moved — and the shared SQL keeps using the bare, unqualified names, resolved through `search_path = collect, config, public` (set as the database default and carried on the managed connection strings). This is deliberate: Darling's SQL is byte-identical to Lite's DuckDB SQL, and re-qualifying it would fork that twin.

**Three roles.** The service still owns the store as the `darling` superuser (it does the DDL — migrations, hypertable conversion, retention). On top of that it provisions two least-privilege **login** roles the Viewer connects as instead:

| Role | Privileges | Used by |
|---|---|---|
| `darling` | superuser / owner | the service (collection, migration, provisioning) |
| `admin` | SELECT on both schemas + INSERT/UPDATE/DELETE on `config` only | the Viewer, by default (`connectAs: "admin"`) |
| `viewer` | SELECT on both schemas, no writes | a locked-down Viewer (`connectAs: "viewer"`) |

`admin` cannot `DROP`, alter schema, touch `collect` data, or create objects — it can only do what the Viewer's mute-rule / alert-dismiss surfaces need. `ALTER DEFAULT PRIVILEGES` means new collector tables auto-inherit SELECT, so the model never drifts as collectors are added.

**Managed mode** provisions all of this automatically on every start (idempotent and self-healing), generating a per-role DPAPI-LocalMachine credential — `pg-admin-credential.dpapi` and `pg-viewer-credential.dpapi` beside the data directory, same posture as the owner's `pg-credential.dpapi`. Nothing to configure beyond `connectAs`.

**Credential file protection.** DPAPI LocalMachine scope is deliberate (the service writes the credential, a *different* interactive user's Viewer reads it), which means the machine-bound blob is decryptable by anything that can *read* the file. So the credential files are locked down with an NTFS ACL that strips the inherited world-read `%ProgramData%` would give them:

| File(s) | Readable by |
|---|---|
| `pg-credential.dpapi` (superuser) + the transient init pwfile | SYSTEM, Administrators, the service account — **not** interactive users |
| `pg-admin-credential.dpapi`, `pg-viewer-credential.dpapi` | the above **+ `NT AUTHORITY\INTERACTIVE`** (the operator's Viewer) |

The principal model assumes the **single-operator VM** this edition targets: `INTERACTIVE` == the operator, so the admin/viewer credentials are readable by the Viewer with zero configuration, while non-interactive local code (other services, sandboxed/SSRF socket primitives, scheduled tasks) and the superuser credential are excluded outright. On a shared machine where untrusted users log on interactively, tighten those two files to the specific operator account by hand. The service also refuses to trust a credential file that isn't owned by SYSTEM/Administrators/itself (closing a pre-plant attack), and regenerates an untrusted role credential.

**A read-only (`viewer`) Viewer degrades gracefully.** It probes its own privileges on connect (`has_table_privilege`), so the mute-rule Add/Edit/Toggle/Delete/Purge buttons and the alert Dismiss / Dismiss All buttons are hidden or disabled, and any write that still slips through returns a clear "read-only connection" message instead of an error.

**Bring-your-own PostgreSQL.** The schema split runs everywhere (it's a migration — the service applies it on startup and best-effort sets the database `search_path`; if your collection login can't `ALTER DATABASE`, run that one statement yourself as the owner). Role provisioning is managed-only, so for BYO you create the two roles yourself, once, with the shipped script:

```
psql -h <host> -U <owner> -d darling -f Darling/tools/provision-roles.sql
```

Edit the two password placeholders (and the database/owner names if yours differ) first. Then point a read-only Viewer's `connectionString` at the `viewer` role.

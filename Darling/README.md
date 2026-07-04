# Performance Monitor Darling — Headless Edition

Darling is the headless, centralized edition of Performance Monitor: a 24/7 Windows service that collects from your SQL Servers into a central PostgreSQL (optionally TimescaleDB) store, plus a detached desktop viewer that reads that store. No desktop app has to stay open for collection to happen, and every viewer seat reads the same central data.

It runs the **same monitoring brain as the Lite edition** — one shared codebase, two storage engines:

- `PerformanceMonitor.Collectors` owns all 26 collector definitions: the exact T-SQL sent to monitored servers, the result-row mappings, the delta rules, the default cadences and retention horizons, and the ignored-wait-types list. Lite writes those rows to DuckDB; Darling writes the same rows to PostgreSQL via binary COPY.
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

### Run It — Console Mode

The same executable serves interactive debugging and service installation; the Windows-service lifetime is a no-op when run from a console.

```
Darling\PerformanceMonitor.Darling.Service\bin\Release\net10.0\PerformanceMonitor.Darling.Service.exe
```

Watch the log output: you should see the config load (`Loaded configuration from ...`), the store migrate (`Postgres store ready (schema v4, ...)`), the TimescaleDB detection result, per-server connects, and then per-collector run lines with row counts.

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

The embedded MCP server: the six diagnostic-analysis tools — `analyze_server`, `get_analysis_facts`, `compare_analysis`, `audit_config`, `get_analysis_findings`, `mute_analysis_finding` — the same analysis surface Lite and the Dashboard expose, over Streamable HTTP bound to `localhost` only.

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

The service migrates the store itself at startup — plain versioned SQL scripts, each applied once inside its own transaction, tracked in `darling_schema_version`, safe under concurrent starters (advisory-locked). Current schema is **v4**:

| Version | Contents |
|---|---|
| **V1** — collector tables | One table per collector, all 26, generated from the shared collector definitions (column-for-column identical to Lite's DuckDB schema): `wait_stats`, `query_stats`, `procedure_stats`, `query_store_stats`, `query_snapshots`, `cpu_utilization_stats`, `file_io_stats`, `memory_stats`, `memory_clerks`, `memory_pressure_events`, `tempdb_stats`, `perfmon_stats`, `deadlocks`, `blocked_process_reports`, `dmv_blocking_snapshots`, `memory_grant_stats`, `waiting_tasks`, `session_stats`, `running_jobs`, `database_size_stats`, `index_object_stats`, `server_properties`, and the four config snapshots (`server_config`, `database_config`, `database_scoped_config`, `trace_flags`) |
| **V2** — observability | `servers` (registry, upserted on every successful connect: identity, display name, engine edition, major version) and `collection_log` (one row per collector run: SUCCESS / PERMISSIONS / ERROR, row count, SQL-phase and storage-phase timings) |
| **V3** — alerting | `config_alert_log` (one history row per fired alert), `config_edge_trigger_watermarks` (restart-surviving edge-trigger and failed-job watermarks), `config_mute_rules` (alert mute rules; starts empty) |
| **V4** — analysis | `analysis_findings` (persisted findings incl. the stored remediation action), `analysis_muted` (muted finding patterns), and 17 `v_<table>` passthrough views so the shared analysis SQL runs verbatim against this store |

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

The layout mirrors the Lite desktop app: a left sidebar lists the servers from the `servers` registry the service maintains, and the top tab strip holds two fixed **aggregate tabs** — Recommendations and Alerts, which follow the sidebar's single-click selection — alongside a closable **per-server tab** for each server you open. **Double-click a server** in the sidebar to open (or focus) its tab, and close it with the × on the tab header; an empty-state panel is shown until the store has at least one server.

Each per-server tab has eleven inner tabs:

| Inner tab | Contents |
|---|---|
| **Overview** | Five correlated, X-axis-synced timeline lanes over the last 24 hours — CPU % (SQL Server vs SQL+other Total), total wait ms/sec, blocking + deadlocking, buffer pool MB, and file-I/O latency — each with a ±2σ baseline band and anomaly markers, all sharing one crosshair so a spike in one lane lines up against the others |
| **Wait Stats** | A searchable wait-type picker (poison + usual-suspect + `PAGELATCH_` defaults, checked-to-top, a 30-type selection guide) beside a per-**type** trend chart for the checked types over the last 24 hours, with a Wait Time (ms/sec) ↔ Avg Wait Time (ms/wait) metric toggle — the per-type companion to the Overview's single total-wait lane |
| **Queries** | Six sub-tabs over the last 24 hours — **Performance Trends** (a 2×2 of per-second trend charts: query duration, procedure duration, Query Store duration, execution count), **Active Queries** (the ~26-column filterable snapshot grid of captured running queries with a time-range slicer, a **Latest Snapshot** button that re-reads the newest stored capture, and per-row Estimated / Actual plan buttons that open the stored plan in the Plan Viewer), **Top Queries by Duration** (the full query-stats grid with in-grid bar cells for executions/CPU/duration/reads and a CPU-by-database breakdown), **Top Procedures by Duration**, **Query Store by Duration**, and **Query Heatmap** (query counts per 5-minute bin × per-execution magnitude bucket, by a chosen metric; right-click a cell to drill into Active Queries for that window) — the three grids each carry a time-range slicer (drag to narrow the window) and a shared **Compare** control that overlays the current window against a baseline period (yesterday, last week, or same day last week), flagging new and vanished queries |
| **CPU** | Raw per-sample CPU utilization (SQL Server vs other processes) over the last 24 hours — every ring-buffer sample, full-bleed as two series; the Overview's CPU lane plots the same raw samples compactly (SQL vs SQL+other Total) with a baseline |
| **File I/O** | Two sub-tabs over the last 24 hours — **Latency** (per-file read and write latency, with a dashed queued-I/O overlay) and **Throughput** (per-file read and write MB/s) — the top 10 files by activity |
| **tempdb** | Three stacked charts over the last 24 hours — space usage (user / internal objects / version store), total allocated size, and per-file I/O latency |
| **Blocking** | Four sub-tabs over the last 24 hours — **Trends** (lock-wait rate, blocking incidents, deadlocks), **Current Waits** (waiting-task duration by wait type, blocked sessions by database), **Blocked Process Reports** (the full ~25-column filterable grid — XE reports preferred with the always-on DMV blocking snapshot merged in as fallback, each row badged with its source, a time-range slicer, per-row report-XML save, and long-block highlighting; double-click or right-click **View Block Chain** to reconstruct and draw the blocking chain the row belongs to), and **Deadlocks** (one filterable row per process parsed from each deadlock graph, a slicer, per-row graph-XML save; double-click or right-click **View Deadlock Graph** to draw the deadlock graph) |
| **Perfmon** | A searchable counter picker with the shared counter packs (General Throughput, Memory Pressure, CPU / Compilation, I/O Pressure, TempDB Pressure, Lock / Blocking) beside a per-counter delta trend for the checked counters (up to 12) over the last 24 hours |
| **Running Jobs** | Latest snapshot of currently-running SQL Agent jobs — start time, current vs average vs p95 duration, % of average, and a highlighted row when a job is running past its p95 (a store-derived banner appears when the service's login lacks msdb access) |
| **Configuration** | Four column-filterable snapshot grids of the server's latest capture — server configuration (`sys.configurations`), database configuration (28 columns of `sys.databases`), database-scoped configuration, and trace flags |
| **Daily Summary** | A one-row roll-up of the selected day (default today, UTC, with a date picker) — total wait time, the top wait type, distinct query count, deadlock / blocking-event / high-CPU-sample counts, collector errors, and an overall health band |
| **Collection Health** | Three sub-tabs — **Health Summary** (a 7-day per-collector roll-up: run / success / error counts, failure rate, average duration, last success / run / error, and a health band of HEALTHY / WARNING / STALE / FAILING / NEVER_RUN / NO_PERMISSIONS — double-click a collector to open its full run history), **Collection Log** (the recent run log with per-run SQL and store-write timings and row counts), and **Duration Trends** (a per-collector success-duration scatter) |

The two aggregate tabs (server-scoped via the sidebar selection for now):

| Tab | Contents |
|---|---|
| **Recommendations** | The latest analysis run's findings, severity-banded, with a detail pane showing the finding's story, advice, and stored remediation script (read-only — the viewer never applies anything). Right-click a finding to **mute** or **unmute** its pattern; muted findings are flagged and drop out on the engine's next analysis run |
| **Alerts** | Recent alerts from `config_alert_log` for the selected server (newest first, selectable time range), with a detail pane showing each alert's stored detail and dedup fingerprint. A **Manage Mute Rules** button opens the mute-rule editor, and right-clicking an alert can seed a mute rule from it |

Only the visible tab loads, and it refreshes every 60 seconds — an aggregate tab for the sidebar-selected server, or the visible server tab's active inner tab (Lite's visible-only rule).

The viewer is read-only over collected data, but it does perform a small set of **user-initiated writes** — and those go straight to the PostgreSQL store, which is the coordination point (the service honors them on its next read; there is no viewer-to-service channel). Muting/unmuting a finding writes the `analysis_muted` registry; adding, editing, toggling, deleting, or purging a mute rule writes `config_mute_rules` (a rule scopes to a server by name, exactly as Lite's mute rules do). These two coordination tables are the **only** tables the viewer ever writes — it never writes collector data. Alert history is read-only (dismiss is deliberately not offered).

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

**What first run does.** The service looks for `pg-runtime\pgsql\` beside its binary, extracting it from `pg-runtime.zip` when only the zip is present (deleting the extracted directory is therefore always safe — it self-heals). If the data directory has no cluster, it generates a 32-character random password, protects it with DPAPI LocalMachine into `pg-credential.dpapi` beside the data directory (credential first, so a crash mid-initdb never strands a cluster nobody can log into), then runs `initdb` with `scram-sha-256` auth, data checksums, and UTF8/C locale. A marker-guarded block appended to `postgresql.conf` preloads TimescaleDB, sets the port, and restricts listening to `127.0.0.1`; a second versioned block sizes background workers for the 26 per-hypertable compression jobs (`timescaledb.max_background_workers = 28`, `max_worker_processes = 40` — PostgreSQL's default of 8 workers cannot launch them). Both appends are re-checked on every start, so a crash between initdb and the append heals itself instead of silently degrading — and clusters initialized before the worker sizing existed gain it on their next start (effective at the next PostgreSQL restart). Then `pg_ctl start`, `CREATE DATABASE darling`, and the normal startup path (migrations, TimescaleDB adoption — you should see `26/26 collector table(s) are hypertables`) continues exactly as in bring-your-own mode. The connection string is derived from the stored credential; the Viewer and the MCP host on the same machine derive it the same way, so nothing needs configuring there either.

**Why scram and not trust, even loopback-only.** Trust auth would hand superuser to any local code that can open a loopback socket — every other local user, and network-capable-but-not-filesystem-capable attack primitives like SSRF from a co-hosted app. With scram the credential travels on the wire, failed attempts are auditable, and access is confined to what can read the DPAPI-protected credential file. `listen_addresses = '127.0.0.1'` keeps the server unreachable off the machine on top.

**Lifecycle.** On shutdown the service stops the server (`pg_ctl stop -m fast`) **only when it started it**. A server that was already running — an operator's own `pg_ctl`, or a postmaster that survived a service crash — is adopted for connections but never stopped: you'll see `already running … will not stop it` in the log, and the service keeps collecting into it.

**The runtime zip.** `pg-runtime.zip` ships beside the service binary in packaged releases. Building from source, produce it once with `Darling\tools\fetch-pg-runtime.ps1` — it downloads the pinned EDB PostgreSQL 17 binaries and TimescaleDB, verifies their SHA256, prunes what the service doesn't need, and writes the zip to `Darling\artifacts\`; copy it next to the built service exe.

**Server log.** The bundled server's own log is `pg.log` beside the data directory — that's where PostgreSQL explains a refused start; bootstrap errors in the service log quote its tail.

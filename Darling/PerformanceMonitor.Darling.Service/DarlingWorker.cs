/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PerformanceMonitor.Alerting;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// The 24/7 collection loop (headless plan M2): load darling.json, migrate the Postgres store,
/// re-seed delta baselines from it (restart continuity — the Postgres twin of Lite's DuckDB
/// seeding, so a service restart doesn't zero the first cycle's deltas), connect and probe each
/// monitored server, ensure the XE sessions, run the on-load config
/// snapshots once, then run every scheduled collector on the shared
/// <see cref="CollectorScheduleDefaults"/> cadence through <see cref="DarlingCollectorRunner"/>.
/// A server that fails to connect is retried every sweep; a collector that errors is logged and
/// retried on its next due time — the loop never dies for one bad cycle. Dispatch mirrors Lite's:
/// the deadlock/blocked-process readers tolerate a missing XE session as zero rows, and
/// trace_flags tolerates denied DBCC as zero rows with a warning. Every successful connect
/// upserts the servers registry and every collector run writes a collection_log row — both
/// failure-isolated (<see cref="DarlingObservability"/>).
/// </summary>
public sealed class DarlingWorker : BackgroundService
{
    private static readonly TimeSpan s_sweepInterval = TimeSpan.FromSeconds(15);

    /* The alert engine's evaluation cadence — Lite's overview/alert sweep runs on its 30-second
       status timer (MainWindow.xaml.cs:144), so the headless twin evaluates each connected server
       every 30 seconds too (the collector sweep itself runs every 15). Cooldowns and the
       edge-trigger gates shape delivery on top of this. */
    private static readonly TimeSpan s_alertSweepInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<DarlingWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /* Set once by ExecuteAsync before the loop starts; the observability writes need it. */
    private NpgsqlDataSource? _postgres;

    /* MinValue = the first sweep after startup runs the retention purge, then daily. */
    private DateTime _nextPurgeUtc = DateTime.MinValue;

    public DarlingWorker(ILogger<DarlingWorker> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    private sealed class ServerLoopState
    {
        public required MonitoredServer Config { get; init; }
        public ServerRuntime? Runtime { get; set; }
        public Dictionary<string, DateTime> NextDue { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime NextConnectAttempt { get; set; } = DateTime.MinValue;

        /* MinValue = the first loop pass after connect evaluates alerts immediately. */
        public DateTime NextAlertSweep { get; set; } = DateTime.MinValue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DarlingConfig config;
        try
        {
            var configPath = DarlingConfig.ResolveConfigPath();
            config = DarlingConfig.Load();
            _logger.LogInformation("Loaded configuration from {Path}: {ServerCount} server(s)", configPath, config.Servers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogCritical("Cannot load configuration: {Message}", ex.Message);
            return;
        }

        var problems = config.Validate();
        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                _logger.LogCritical("Configuration problem: {Problem}", problem);
            }
            return;
        }

        await using var postgres = NpgsqlDataSource.Create(config.Postgres.ConnectionString);
        _postgres = postgres;
        try
        {
            await using var migrateConnection = await postgres.OpenConnectionAsync(stoppingToken);
            var applied = await PgMigrations.MigrateAsync(migrateConnection, stoppingToken);
            _logger.LogInformation("Postgres store ready (schema v{Version}, {Applied} migration(s) applied)",
                StorageVersion.SchemaVersion, applied);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogCritical("Cannot reach or migrate the Postgres store: {Message}", ex.Message);
            return;
        }

        /* Restart continuity: re-seed delta baselines from the store (the Postgres twin of Lite's
           DuckDB seeding) so the first cycle after a service restart produces real deltas instead
           of zeroes. A seed failure logs a warning and collection proceeds with first-cycle-zero. */
        var deltas = new DarlingDeltaCalculator();
        await deltas.SeedFromStoreAsync(postgres, _logger, stoppingToken);

        var runner = new DarlingCollectorRunner(postgres, deltas, _logger);
        var servers = new List<ServerLoopState>();
        foreach (var server in config.Servers)
        {
            servers.Add(new ServerLoopState { Config = server });
        }

        /* Phase-5 slice D: the shared alert engine, wired to the PG-backed stores (V3) and the
           shared email/webhook delivery. Constructed once — the engine holds the per-server
           edge-trigger state for the service's lifetime. */
        var engine = await BuildAlertEngineAsync(config, postgres, servers);

        _logger.LogInformation("PerformanceMonitor Darling collection loop started");

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var server in servers)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (server.Runtime is null)
                {
                    await TryConnectAsync(server, runner, stoppingToken);
                    continue;
                }

                await RunDueCollectorsAsync(server, runner, stoppingToken);

                /* After the server's collector sweep: evaluate alerts against the freshly
                   collected store — per-server sequential within this loop, on Lite's 30-second
                   overview cadence. */
                if (DateTime.UtcNow >= server.NextAlertSweep)
                {
                    server.NextAlertSweep = DateTime.UtcNow.Add(s_alertSweepInterval);
                    await EvaluateAlertsAsync(engine, server, stoppingToken);
                }
            }

            if (DateTime.UtcNow >= _nextPurgeUtc)
            {
                _nextPurgeUtc = DateTime.UtcNow.AddHours(24);
                await DarlingRetention.PurgeAsync(postgres, _logger, stoppingToken);
            }

            try
            {
                await Task.Delay(s_sweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("PerformanceMonitor Darling collection loop stopped");
    }

    /// <summary>
    /// Assembles the Phase-5 shared alert engine over Darling's third-party implementations:
    /// darling.json thresholds/SMTP/webhooks (<see cref="DarlingAlertSettings"/>), the Postgres
    /// collected feeds (<see cref="DarlingAlertReadAdapter"/>), the V3 PG watermark/history/mute
    /// stores, Lite-cadence delivery (<see cref="DarlingAlertDeliverer"/> → the shared
    /// EmailSendCore + WebhookAlertService), a live-msdb failed-jobs fetcher on the monitored
    /// server's own connection, and a resolution hook that logs recovered conditions (the
    /// headless stand-in for Lite's tray "Cleared" toasts).
    /// </summary>
    private async Task<AlertEngine> BuildAlertEngineAsync(
        DarlingConfig config, NpgsqlDataSource postgres, List<ServerLoopState> servers)
    {
        var alertSettings = new DarlingAlertSettings(config);
        var historyStore = new PgAlertHistoryStore(postgres, _logger);
        var stateStore = new PgAlertStateStore(postgres, _logger);

        /* Mute rules load once at startup, like Lite's — the headless store starts empty
           (nothing muted) until rows are added to config_mute_rules. */
        var muteRuleService = new MuteRuleService(
            new PgMuteRuleStore(postgres, _logger), _loggerFactory.CreateLogger<MuteRuleService>());
        await muteRuleService.LoadAsync();

        /* The webhook service is constructed first and injected into the deliverer's send core
           (Lite's MainWindow wiring); the shared history store seeds both channels' cooldowns
           across a service restart (#1145). */
        var webhookAlertService = new WebhookAlertService(
            alertSettings, DarlingAlertDeliverer.Branding,
            _loggerFactory.CreateLogger<WebhookAlertService>(), historyStore);
        var deliverer = new DarlingAlertDeliverer(alertSettings, historyStore, webhookAlertService, _logger);

        return new AlertEngine(
            alertSettings,
            new DarlingAlertReadAdapter(postgres),
            stateStore,
            deliverer,
            muteRuleService.IsAlertMuted,
            failedJobsFetcher: (serverKey, lookbackMinutes, ct) =>
                FetchFailedJobsAsync(servers, serverKey, lookbackMinutes, ct),
            resolutionCallback: (resolution, _) =>
            {
                _logger.LogInformation("[{Server}] {Title}: {Message}",
                    resolution.ServerName, resolution.Title, resolution.Message);
                return Task.CompletedTask;
            },
            logger: _logger);
    }

    /// <summary>
    /// Builds this sweep's <see cref="AlertServerSnapshot"/> and runs the engine for one
    /// connected server. The CPU pair mirrors what Lite's overview summary carries (the latest
    /// cpu_utilization_stats sample; total = SQL + other-process, null when no SQL sample);
    /// isOnline is true by definition here (a connected runtime) and suppression is always false
    /// (headless — suppression is an engine INPUT owned by interactive hosts). Failure-isolated:
    /// a failed sweep logs and retries on the next cadence tick, mirroring the collector loop.
    /// </summary>
    private async Task EvaluateAlertsAsync(AlertEngine engine, ServerLoopState server, CancellationToken cancellationToken)
    {
        var runtime = server.Runtime;
        if (runtime is null)
        {
            return;
        }

        try
        {
            var (sqlCpu, totalCpu) = await ReadLatestCpuAsync(runtime.ServerId, cancellationToken);
            var snapshot = new AlertServerSnapshot(
                runtime.ServerId.ToString(CultureInfo.InvariantCulture),
                runtime.Config.DisplayName,
                IsOnline: true,
                SqlCpuPercent: sqlCpu,
                TotalCpuPercent: totalCpu,
                IsAzureSqlDb: runtime.Target.IsAzureSqlDb,
                Suppressed: false);

            await engine.EvaluateServerAsync(snapshot, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("[{Server}] Alert sweep failed: {Message}", server.Config.DisplayName, ex.Message);
        }
    }

    /// <summary>
    /// The latest collected CPU sample for the snapshot — Lite's overview read
    /// (LocalDataService.Overview.cs:37-51) against the raw PG table, and the
    /// ServerSummaryItem.TotalCpuPercent derivation (:140-141): total = SQL + (other ?? 0),
    /// null when there is no SQL sample (Azure SQL DB stores other as 0; Linux stores NULL).
    /// </summary>
    private async Task<(double? SqlCpu, double? TotalCpu)> ReadLatestCpuAsync(int serverId, CancellationToken cancellationToken)
    {
        double? sqlCpu = null;
        double? otherCpu = null;

        await using var connection = await _postgres!.OpenConnectionAsync(cancellationToken);
        using var command = new NpgsqlCommand(@"
SELECT sqlserver_cpu_utilization, other_process_cpu_utilization
FROM cpu_utilization_stats
WHERE server_id = $1
ORDER BY sample_time DESC
LIMIT 1", connection);
        command.Parameters.AddWithValue(serverId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            sqlCpu = reader.IsDBNull(0) ? null : Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture);
            otherCpu = reader.IsDBNull(1) ? null : Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        double? totalCpu = sqlCpu.HasValue ? sqlCpu.Value + (otherCpu ?? 0) : null;
        return (sqlCpu, totalCpu);
    }

    /// <summary>
    /// The engine's live-msdb failed-jobs feed: runs the shared <see cref="FailedJobsQuery"/> on
    /// the monitored server's own connection. Gated !IsAzureSqlDb (the engine also gates on the
    /// snapshot; there is deliberately NO msdb-access probe — Phase-5 review F11) and degrades
    /// exactly like the Dashboard's caller: a login without msdb / SQLAgentReaderRole access
    /// raises SqlException 229/297/300/916 → Info + empty list; any other failure → Warning +
    /// empty list — a permission gap or transient error never fails the alert cycle.
    /// </summary>
    private async Task<List<FailedJobInfo>> FetchFailedJobsAsync(
        List<ServerLoopState> servers, string serverKey, int lookbackMinutes, CancellationToken cancellationToken)
    {
        var runtime = servers
            .Select(s => s.Runtime)
            .FirstOrDefault(r => r is not null
                && string.Equals(r.ServerId.ToString(CultureInfo.InvariantCulture), serverKey, StringComparison.Ordinal));
        if (runtime is null || runtime.Target.IsAzureSqlDb)
        {
            return new List<FailedJobInfo>();
        }

        try
        {
            using var connection = new SqlConnection(runtime.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = new SqlCommand(FailedJobsQuery.Sql, connection) { CommandTimeout = 10 };
            command.Parameters.Add(new SqlParameter(FailedJobsQuery.LookbackMinutesParameter, SqlDbType.Int) { Value = lookbackMinutes });
            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            return await FailedJobsQuery.ReadAsync(reader, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex) when (ex.Number is 229 or 297 or 300 or 916)
        {
            /* Expected for read-only monitoring accounts; hit every alert cycle, so Info. */
            _logger.LogInformation("[{Server}] Skipping recently-failed-job check (msdb/SQLAgentReaderRole access needed): {Message}",
                runtime.Config.DisplayName, ex.Message);
            return new List<FailedJobInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[{Server}] Recently-failed-job check errored: {Message}",
                runtime.Config.DisplayName, ex.Message);
            return new List<FailedJobInfo>();
        }
    }

    private async Task TryConnectAsync(ServerLoopState server, DarlingCollectorRunner runner, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow < server.NextConnectAttempt)
        {
            return;
        }

        try
        {
            server.Runtime = await DarlingServerConnector.ConnectAsync(server.Config, _logger, cancellationToken);
            _logger.LogInformation("[{Server}] Connected (major {Major}, edition {Edition}, server_id {ServerId})",
                server.Config.DisplayName,
                server.Runtime.Target.SqlMajorVersion,
                server.Runtime.Target.IsAzureSqlDb ? "AzureSqlDb" : server.Runtime.Target.IsAzureManagedInstance ? "ManagedInstance" : "Box",
                server.Runtime.ServerId);

            await DarlingObservability.UpsertServerAsync(_postgres!, server.Runtime, _logger, cancellationToken);

            await DarlingXeSessions.EnsureAllAsync(server.Runtime, _logger, cancellationToken);

            /* On-load config snapshots (FrequencyMinutes 0) run once per connect, then every
               scheduled collector becomes immediately due — mirrors Lite's server-open behavior. */
            var now = DateTime.UtcNow;
            foreach (var (name, schedule) in CollectorScheduleDefaults.All)
            {
                if (schedule.FrequencyMinutes == 0)
                {
                    await RunOneAsync(server, runner, name, cancellationToken);
                }
                else
                {
                    server.NextDue[name] = now;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            server.Runtime = null;
            server.NextConnectAttempt = DateTime.UtcNow.AddSeconds(60);
            _logger.LogWarning("[{Server}] Connect failed, retrying in 60s: {Message}", server.Config.DisplayName, ex.Message);
        }
    }

    private async Task RunDueCollectorsAsync(ServerLoopState server, DarlingCollectorRunner runner, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        foreach (var (name, schedule) in CollectorScheduleDefaults.All)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (schedule.FrequencyMinutes == 0
                || !server.NextDue.TryGetValue(name, out var due)
                || now < due)
            {
                continue;
            }

            server.NextDue[name] = now.AddMinutes(schedule.FrequencyMinutes);
            await RunOneAsync(server, runner, name, cancellationToken);
        }
    }

    private async Task RunOneAsync(ServerLoopState server, DarlingCollectorRunner runner, string collectorName, CancellationToken cancellationToken)
    {
        var runtime = server.Runtime;
        if (runtime is null || !s_dispatch.TryGetValue(collectorName, out var run))
        {
            return;
        }

        try
        {
            var result = await run(runner, runtime, cancellationToken);
            _logger.LogInformation("  [{Server}] {Collector} => {Rows} rows (sql:{SqlMs}ms, pg:{PgMs}ms)",
                server.Config.DisplayName, collectorName, result.Rows, result.SqlMs, result.StorageMs);

            await DarlingObservability.LogCollectionAsync(
                _postgres!, runtime, collectorName, "SUCCESS", result.Rows, result.SqlMs, result.StorageMs, null, _logger, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex) when (ex.Number is 229 or 297 or 300)
        {
            _logger.LogWarning("  [{Server}] {Collector} => insufficient permissions ({Number}): {Message}",
                server.Config.DisplayName, collectorName, ex.Number, ex.Message);

            await DarlingObservability.LogCollectionAsync(
                _postgres!, runtime, collectorName, "PERMISSIONS", 0, 0, 0, ex.Message, _logger, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("  [{Server}] {Collector} => ERROR: {Message}",
                server.Config.DisplayName, collectorName, ex.Message);

            /* A dead connection poisons every collector — force a reconnect + reprobe. */
            if (ex is SqlException sqlEx && (sqlEx.Class >= 20 || sqlEx.Number == -2))
            {
                server.Runtime = null;
                server.NextConnectAttempt = DateTime.UtcNow.AddSeconds(60);
                _logger.LogWarning("[{Server}] Connection-level failure — will reconnect", server.Config.DisplayName);
            }

            await DarlingObservability.LogCollectionAsync(
                _postgres!, runtime, collectorName, "ERROR", 0, 0, 0, ex.Message, _logger, cancellationToken);
        }
    }

    private delegate Task<CollectorRunResult> DispatchEntry(DarlingCollectorRunner runner, ServerRuntime server, CancellationToken cancellationToken);

    /// <summary>Test hook: the collector names the worker can dispatch (pinned against the catalog).</summary>
    internal static IReadOnlyCollection<string> DispatchedCollectorNames => s_dispatch.Keys.ToArray();

    /// <summary>
    /// Collector-name dispatch — the Darling twin of Lite's RunCollectorAsync switch, one typed
    /// entry per shared definition, with Lite's forwarder tolerances mirrored: the XE readers
    /// treat a missing/inaccessible session as zero rows, trace_flags treats denied DBCC as zero
    /// rows with a warning.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, DispatchEntry> s_dispatch = new Dictionary<string, DispatchEntry>(StringComparer.OrdinalIgnoreCase)
    {
        ["wait_stats"] = (r, s, ct) => r.RunAsync(WaitStatsCollector.Instance, s, ct),
        ["tempdb_stats"] = (r, s, ct) => r.RunAsync(TempDbStatsCollector.Instance, s, ct),
        ["memory_grant_stats"] = (r, s, ct) => r.RunAsync(MemoryGrantsCollector.Instance, s, ct),
        ["cpu_utilization"] = (r, s, ct) => r.RunAsync(CpuUtilizationCollector.Instance, s, ct),
        ["memory_stats"] = (r, s, ct) => r.RunAsync(MemoryStatsCollector.Instance, s, ct),
        ["memory_clerks"] = (r, s, ct) => r.RunAsync(MemoryClerksCollector.Instance, s, ct),
        ["memory_pressure_events"] = (r, s, ct) => r.RunAsync(MemoryPressureEventsCollector.Instance, s, ct),
        ["file_io_stats"] = (r, s, ct) => r.RunAsync(FileIoStatsCollector.Instance, s, ct),
        ["server_properties"] = (r, s, ct) => r.RunAsync(ServerPropertiesCollector.Instance, s, ct),
        ["server_config"] = (r, s, ct) => r.RunAsync(ServerConfigCollector.Instance, s, ct),
        ["database_config"] = (r, s, ct) => r.RunAsync(DatabaseConfigCollector.Instance, s, ct),
        ["trace_flags"] = RunTraceFlagsTolerantAsync,
        ["database_scoped_config"] = (r, s, ct) => r.RunAsync(DatabaseScopedConfigCollector.Instance, s, ct),
        ["session_stats"] = (r, s, ct) => r.RunAsync(SessionStatsCollector.Instance, s, ct),
        ["waiting_tasks"] = (r, s, ct) => r.RunAsync(WaitingTasksCollector.Instance, s, ct),
        ["procedure_stats"] = (r, s, ct) => r.RunAsync(ProcedureStatsCollector.Instance, s, ct),
        ["running_jobs"] = (r, s, ct) => r.RunAsync(RunningJobsCollector.Instance, s, ct),
        ["perfmon_stats"] = (r, s, ct) => r.RunAsync(PerfmonStatsCollector.Instance, s, ct),
        ["dmv_blocking_snapshot"] = (r, s, ct) => r.RunAsync(DmvBlockingSnapshotCollector.Instance, s, ct),
        ["database_size_stats"] = (r, s, ct) => r.RunAsync(DatabaseSizeStatsCollector.Instance, s, ct),
        ["index_object_stats"] = (r, s, ct) => r.RunAsync(IndexObjectStatsCollector.Instance, s, ct),
        ["query_stats"] = (r, s, ct) => r.RunAsync(QueryStatsCollector.Instance, s, ct),
        ["query_snapshots"] = (r, s, ct) => r.RunAsync(QuerySnapshotsCollector.Instance, s, ct),
        ["query_store"] = (r, s, ct) => r.RunAsync(QueryStoreCollector.Instance, s, ct),
        ["deadlocks"] = (r, s, ct) => RunXeTolerantAsync(DeadlocksCollector.Instance, r, s, ct),
        ["blocked_process_report"] = (r, s, ct) => RunXeTolerantAsync(BlockedProcessReportCollector.Instance, r, s, ct),
    };

    private static async Task<CollectorRunResult> RunXeTolerantAsync<TRow>(
        ICollectorDefinition<TRow> definition, DarlingCollectorRunner runner, ServerRuntime server, CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync(definition, server, cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == 297 || ex.Number == 15151 || ex.Message.Contains("XE session"))
        {
            /* XE session not found or not accessible — zero rows, mirrors Lite. */
            return new CollectorRunResult(0, 0, 0);
        }
    }

    private static async Task<CollectorRunResult> RunTraceFlagsTolerantAsync(
        DarlingCollectorRunner runner, ServerRuntime server, CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync(TraceFlagsCollector.Instance, server, cancellationToken);
        }
        catch (SqlException)
        {
            /* DBCC may be denied — degrade to zero rows, mirrors Lite's warning path. */
            return new CollectorRunResult(0, 0, 0);
        }
    }
}

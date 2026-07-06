/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Concurrent;
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
using PerformanceMonitor.Darling.Analysis;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// The 24/7 collection loop (headless plan M2): load darling.json, bootstrap the bundled
/// Postgres first when <c>postgres.managed</c> is true (<see cref="DarlingManagedPostgres"/> —
/// unpack/initdb/start before anything touches the store, stop-on-shutdown only if this
/// process started it), migrate the Postgres store,
/// detect optional TimescaleDB (hypertables + compression when present, plain PG otherwise —
/// see TimescaleSupport), re-seed delta baselines from it (restart continuity — the Postgres
/// twin of Lite's DuckDB
/// seeding, so a service restart doesn't zero the first cycle's deltas), connect and probe each
/// monitored server, ensure the XE sessions, run the on-load config
/// snapshots once, then run every scheduled collector on the shared
/// <see cref="CollectorScheduleDefaults"/> cadence through <see cref="DarlingCollectorRunner"/>.
/// A server that fails to connect is retried every sweep; a collector that errors is logged and
/// retried on its next due time — the loop never dies for one bad cycle. Dispatch mirrors Lite's:
/// the deadlock/blocked-process readers tolerate a missing XE session as zero rows, and
/// trace_flags tolerates denied DBCC as zero rows with a warning. Every successful connect
/// upserts the servers registry and every collector run writes a collection_log row — both
/// failure-isolated (<see cref="DarlingObservability"/>). On top of collection the loop runs
/// the shared alert engine per server every 30 seconds and, since AN3, the analysis pipeline
/// (<see cref="DarlingAnalysisService"/>) per server every 30 minutes with findings routed
/// through the shared <see cref="AnalysisNotificationService"/>.
/// </summary>
public sealed class DarlingWorker : BackgroundService
{
    private static readonly TimeSpan s_sweepInterval = TimeSpan.FromSeconds(15);

    /* The alert engine's evaluation cadence — Lite's overview/alert sweep runs on its 30-second
       status timer (MainWindow.xaml.cs:144), so the headless twin evaluates each connected server
       every 30 seconds too (the collector sweep itself runs every 15). Cooldowns and the
       edge-trigger gates shape delivery on top of this. */
    private static readonly TimeSpan s_alertSweepInterval = TimeSpan.FromSeconds(30);

    /* The analysis pipeline's cadence + per-run budget — Lite's App defaults hardcoded
       (AnalysisIntervalMinutes 30 / AnalysisTimeoutSeconds 120; defaults over speculative
       config). Each run analyzes the last 4 hours (Lite's hoursBack default). */
    private static readonly TimeSpan s_analysisInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan s_analysisTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Test hooks: the hardcoded analysis cadence/budget, pinned against Lite's defaults.</summary>
    internal static TimeSpan AnalysisInterval => s_analysisInterval;
    internal static TimeSpan AnalysisTimeout => s_analysisTimeout;

    private readonly ILogger<DarlingWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /* Set once by ExecuteAsync before the loop starts; the observability writes need it. */
    private NpgsqlDataSource? _postgres;

    /* Server IDs whose scheduled analysis is currently running — prevents relaunching
       analysis for a server whose previous (possibly hung) pass has not finished
       (Lite's CollectionBackgroundService in-flight guard). */
    private readonly ConcurrentDictionary<int, byte> _analysisInFlight = new();

    /* MinValue = the first sweep after startup runs the retention purge, then daily. */
    private DateTime _nextPurgeUtc = DateTime.MinValue;

    /* Set once at startup by the TimescaleSupport detection (cached per data source — the
       extension can't appear or vanish under a running service without a restart anyway);
       branches the retention purge onto drop_chunks. */
    private bool _timescaleAvailable;

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

        /* MinValue = the first loop pass after connect runs analysis immediately; the
           pipeline's own 24h data-span gate no-ops it until the store has enough history
           (Lite's GetTotalDataSpanHoursAsync gate), so a fresh server simply re-checks
           every interval while an already-populated store analyzes right away. */
        public DateTime NextAnalysisDue { get; set; } = DateTime.MinValue;
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

        /* Bundled-Postgres bootstrap (the shipped zero-admin default): in managed mode the
           service unpacks/initializes/starts its own Postgres BEFORE the store connection
           below, and the connection string is DERIVED (localhost + port + the generated
           DPAPI credential), never configured. Windows-only, like every DPAPI surface here.
           A bootstrap failure is the existing no-store behavior: LogCritical + clean exit. */
        DarlingManagedPostgres? managedPostgres = null;
        var storeConnectionString = config.Postgres.ConnectionString;
        if (config.Postgres.Managed)
        {
            if (!OperatingSystem.IsWindows())
            {
                _logger.LogCritical(
                    "postgres.managed = true requires Windows (the bundled runtime and the DPAPI-protected credential); " +
                    "set postgres.managed = false and point postgres.connectionString at your own PostgreSQL instead.");
                return;
            }

            managedPostgres = new DarlingManagedPostgres(config.Postgres, _logger);
            try
            {
                storeConnectionString = await managedPostgres.EnsureRunningAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogCritical("Managed Postgres bootstrap failed: {Message}", ex.Message);
                return;
            }
        }

        try
        {
            await RunCollectionLoopAsync(config, storeConnectionString, stoppingToken);
        }
        finally
        {
            /* Stop the bundled server ONLY if this process started it — never one the operator
               (or a surviving previous run) owns. Runs on every exit path, including a failed
               migration, AFTER the loop's data source is disposed. The IsWindows re-check is a
               CA1416 guard only — a non-null managedPostgres already implies Windows. */
            if (managedPostgres is not null && OperatingSystem.IsWindows())
            {
                await managedPostgres.StopIfStartedByThisProcessAsync();
            }
        }
    }

    /// <summary>
    /// Everything after the (optional) managed-Postgres bootstrap: store connection, migration,
    /// Timescale adoption, delta seeding, and the collection/alert/analysis loop. Split from
    /// <see cref="ExecuteAsync"/> so the bootstrap's finally can stop the bundled server after
    /// this method's data source is disposed.
    /// </summary>
    private async Task RunCollectionLoopAsync(DarlingConfig config, string storeConnectionString, CancellationToken stoppingToken)
    {
        /* Carry the collect/config search path on the store connection string BEFORE the data
           source (and its pool) is created, so every pooled physical connection resolves the
           shared SQL's bare table names to the V8 schemas from its very first use — deterministic
           and independent of the pool's connection-open timing relative to PgMigrations'
           best-effort ALTER DATABASE ... SET search_path. Without this a FRESH bring-your-own
           store silently collects nothing until the service is restarted; see
           EnsureStoreSearchPath for the pool-timing root cause. Managed mode already sets it, so
           this is a no-op there. */
        storeConnectionString = EnsureStoreSearchPath(storeConnectionString);
        await using var postgres = NpgsqlDataSource.Create(storeConnectionString);
        _postgres = postgres;
        try
        {
            await using var migrateConnection = await postgres.OpenConnectionAsync(stoppingToken);
            /* MigrateAsync (logger overload) also best-effort sets the database-default search_path to
               collect/config for every future connection (V8 security split); a least-privilege BYO
               login that cannot ALTER DATABASE is warned, not failed — the managed connection strings
               carry Search Path regardless. */
            var applied = await PgMigrations.MigrateAsync(migrateConnection, _logger, stoppingToken);
            _logger.LogInformation("Postgres store ready (schema v{Version}, {Applied} migration(s) applied)",
                StorageVersion.SchemaVersion, applied);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogCritical("Cannot reach or migrate the Postgres store: {Message}", ex.Message);
            return;
        }

        /* Least-privilege role provisioning (V8 security hardening), managed mode only: create /
           refresh the admin + viewer login roles and their per-role DPAPI credentials, and grant the
           collect/config privileges — idempotent and self-healing, the conf-append discipline applied
           to roles. Windows-only (DPAPI credential files); a failure degrades (the Viewer cannot
           connect as admin/viewer until a later start succeeds) but never kills collection, which
           connects as the owner. BYO stores provision roles out-of-band via tools/provision-roles.sql. */
        if (config.Postgres.Managed && OperatingSystem.IsWindows())
        {
            try
            {
                var dataDirectory = DarlingManagedPostgres.ResolveDataDirectory(config.Postgres);
                await DarlingManagedRoles.EnsureProvisionedAsync(postgres, dataDirectory, _logger, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    "Least-privilege role provisioning failed — the Viewer's admin/viewer roles may be stale " +
                    "until the next successful start: {Message}", ex.Message);
            }
        }

        /* Optional TimescaleDB adoption — runtime setup, deliberately NOT a versioned migration
           (the store must work with or without the extension; migrations stay engine-plain).
           Detected once at startup; when present, the collector tables become hypertables with
           a 7-day compression policy (Darling's archival tier) and the daily retention purge
           below switches to drop_chunks. All idempotent, so every restart re-converges. In its
           own try/catch OUTSIDE the critical migrate block: an optional feature failing must
           degrade to plain-PostgreSQL mode, never kill the service. */
        try
        {
            await using var timescaleConnection = await postgres.OpenConnectionAsync(stoppingToken);
            _timescaleAvailable = await TimescaleSupport.TryEnableAsync(timescaleConnection, _logger, stoppingToken);
            if (_timescaleAvailable)
            {
                await TimescaleSupport.ConvertToHypertablesAsync(timescaleConnection, _logger, stoppingToken);
                await TimescaleSupport.ApplyCompressionPolicyAsync(timescaleConnection, _logger, stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* A partially-converted store is fine: DELETE-based retention works on hypertables
               too, so falling back to plain-PG mode is always safe. */
            _timescaleAvailable = false;
            _logger.LogWarning("TimescaleDB setup failed — continuing in plain-PostgreSQL mode: {Message}", ex.Message);
        }

        /* Restart continuity: re-seed delta baselines from the store (the Postgres twin of Lite's
           DuckDB seeding) so the first cycle after a service restart produces real deltas instead
           of zeroes. A seed failure logs a warning and collection proceeds with first-cycle-zero. */
        var deltas = new DarlingDeltaCalculator();
        await deltas.SeedFromStoreAsync(postgres, _logger, stoppingToken);

        var runner = new DarlingCollectorRunner(postgres, deltas, _logger, config.CapturePlans);
        var servers = new List<ServerLoopState>();
        foreach (var server in config.Servers)
        {
            servers.Add(new ServerLoopState { Config = server });
        }

        /* Phase-5 slice D: the shared alert engine, wired to the PG-backed stores (V3) and the
           shared email/webhook delivery. Constructed once — the engine holds the per-server
           edge-trigger state for the service's lifetime. The settings/history/webhook pieces
           are hoisted here because the AN3 analysis-notification path below shares them. */
        var alertSettings = new DarlingAlertSettings(config);
        var historyStore = new PgAlertHistoryStore(postgres, _logger);
        var webhookAlertService = new WebhookAlertService(
            alertSettings, DarlingAlertDeliverer.Branding,
            _loggerFactory.CreateLogger<WebhookAlertService>(), historyStore);
        var engine = await BuildAlertEngineAsync(config, postgres, servers, alertSettings, historyStore, webhookAlertService);

        /* Phase-5 analysis slice AN3: the analysis pipeline's shared pieces, constructed once.
           The plan fetcher resolves a finding's serverId to the CONNECTED runtime's connection
           string (the PgPlanFetcher seam — null for an unknown/disconnected server degrades the
           fetch like Lite's ServerManager miss). The shared AnalysisNotificationService routes
           high-severity findings through DarlingFindingAlertSender (email + webhook + history,
           Lite's cadence); the serverId resolver is Lite's shape (the finding's int id as a
           string), no silencing predicate and no tray sink (headless). */
        var planFetcher = new PgPlanFetcher(
            serverId => servers
                .Select(s => s.Runtime)
                .FirstOrDefault(r => r is not null && r.ServerId == serverId)?.ConnectionString,
            _logger);
        var notificationService = new AnalysisNotificationService(
            new DarlingFindingAlertSender(alertSettings, historyStore, webhookAlertService, _logger),
            alertSettings,
            finding => finding.ServerId.ToString(CultureInfo.InvariantCulture),
            _loggerFactory.CreateLogger<AnalysisNotificationService>());

        /* Delivery gate: Lite gates finding delivery on its AnalysisNotificationsEnabled
           setting while analysis always runs + persists; Darling's headless twin of that
           gate is the master alerts.enabled switch — an operator who turned alerts off gets
           no analysis-finding notifications either, but findings still land in the store. */
        var notifyFindings = config.Alerts.Enabled;

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

                /* AN3: the scheduled analysis pipeline, per-server on Lite's 30-minute
                   cadence. The next-due stamp advances up front (Lite's scheduler shape), so
                   a timed-out pass is skipped, not retried immediately. */
                if (DateTime.UtcNow >= server.NextAnalysisDue)
                {
                    server.NextAnalysisDue = DateTime.UtcNow.Add(s_analysisInterval);
                    await RunScheduledAnalysisAsync(server, planFetcher, notificationService, notifyFindings, stoppingToken);
                }
            }

            if (DateTime.UtcNow >= _nextPurgeUtc)
            {
                _nextPurgeUtc = DateTime.UtcNow.AddHours(24);
                await DarlingRetention.PurgeAsync(postgres, _timescaleAvailable, _logger, stoppingToken);

                /* AN3: findings retention. Both apps' finding stores declare a 30-day cleanup
                   but neither app schedules it (Lite's DuckDB archive-reset bounds it
                   incidentally); a 24/7 service must actually invoke it or analysis_findings
                   grows unbounded. Rides the daily purge; never throws (logs + degrades). */
                await new PgFindingStore(postgres, _logger).CleanupOldFindingsAsync(retentionDays: 30);
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
    /// Ensures the store connection string carries the collect/config search path (the V8 schema
    /// split) so every pooled physical connection resolves the shared SQL's bare table names to the
    /// <c>collect</c>/<c>config</c> schemas from its FIRST use. Managed mode already sets it
    /// (<see cref="DarlingManagedPostgres.SearchPath"/>) — that string is returned unchanged, no
    /// double-set. A bring-your-own connection string usually omits it and would otherwise rely on
    /// the database-default <c>search_path</c> that
    /// <see cref="PgMigrations.MigrateAsync(NpgsqlConnection, ILogger, CancellationToken)"/>
    /// best-effort sets via <c>ALTER DATABASE ... SET search_path</c>.
    ///
    /// <para>That database default only governs the startup search_path of connections established
    /// AFTER the ALTER commits, but the Npgsql pool's physical connections opened around it (for the
    /// migration itself, the hypertable conversion, and the first collection sweep) keep their
    /// pre-ALTER session search_path for their entire lifetime. On a FRESH BYO store that means the
    /// whole first run fails — hypertable conversion, delta seeding, and every collector write hit
    /// <c>42P01: relation "wait_stats" does not exist</c> — and collection only starts working after
    /// a service restart hands out a fresh pool. Carrying the search path on the connection string
    /// itself is deterministic and pool-timing-independent; any login may <c>SET</c> its own
    /// search_path, so this is safe for least-privilege BYO logins too.</para>
    /// </summary>
    internal static string EnsureStoreSearchPath(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.SearchPath))
        {
            /* DarlingManagedPostgres is annotated [SupportedOSPlatform("windows")] for its DPAPI /
               bundled-cluster surface, but SearchPath is a platform-neutral compile-time constant
               with no runtime dependency, and BYO mode runs on any OS — so the CA1416 cross-platform
               reachability flag is spurious for this const reference. Suppressed narrowly rather than
               forking the constant, keeping managed and BYO byte-identical (a test pins them equal). */
#pragma warning disable CA1416
            builder.SearchPath = DarlingManagedPostgres.SearchPath;
#pragma warning restore CA1416
            return builder.ConnectionString;
        }

        return connectionString;
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
        DarlingConfig config, NpgsqlDataSource postgres, List<ServerLoopState> servers,
        DarlingAlertSettings alertSettings, PgAlertHistoryStore historyStore, WebhookAlertService webhookAlertService)
    {
        var stateStore = new PgAlertStateStore(postgres, _logger);

        /* Mute rules load once at startup, like Lite's — the headless store starts empty
           (nothing muted) until rows are added to config_mute_rules. */
        var muteRuleService = new MuteRuleService(
            new PgMuteRuleStore(postgres, _logger), _loggerFactory.CreateLogger<MuteRuleService>());
        await muteRuleService.LoadAsync();

        /* The webhook service was constructed first and injected into the deliverer's send core
           (Lite's MainWindow wiring); the shared history store seeds both channels' cooldowns
           across a service restart (#1145). */
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
    /// Runs the AN3 analysis pipeline for one connected server and routes the findings to the
    /// shared notification path — Lite's CollectionBackgroundService.RunAnalysisIfDueAsync
    /// per-server body transplanted: the in-flight guard skips a server whose previous
    /// (possibly hung) pass has not finished; a FRESH DarlingAnalysisService per run
    /// (IsAnalyzing is a single instance flag, so a shared instance whose task is abandoned on
    /// timeout would block analysis for every other server); the 120-second timeout moves the
    /// loop on without clearing the in-flight marker (the continuation clears it only when the
    /// task truly finishes, so a hung server is not relaunched); findings are persisted inside
    /// AnalyzeAsync and only routed to the notification channels when delivery is on. The
    /// finding identity is the STORAGE name + its hash id — the same identity the collectors
    /// stamp on every row (Lite's GetServerNameForStorage semantics), so findings join the
    /// collected data; the alert engine's DisplayName snapshot identity is deliberately not
    /// used here.
    /// </summary>
    private async Task RunScheduledAnalysisAsync(
        ServerLoopState server,
        PgPlanFetcher planFetcher,
        AnalysisNotificationService notificationService,
        bool notifyFindings,
        CancellationToken stoppingToken)
    {
        var runtime = server.Runtime;
        if (runtime is null)
        {
            return;
        }

        var serverId = runtime.ServerId;

        /* Skip a server whose previous analysis is still running — a hung
           connection that outlived its timeout would otherwise pile up tasks. */
        if (!_analysisInFlight.TryAdd(serverId, 0))
        {
            return;
        }

        try
        {
            var analysisService = new DarlingAnalysisService(_postgres!, planFetcher, _logger);
            var analyzeTask = analysisService.AnalyzeAsync(serverId, runtime.StorageName, hoursBack: 4);

            /* Clear the in-flight marker only when the task truly finishes — not
               when the timeout below moves us on — so a hung server is not relaunched. */
            _ = analyzeTask.ContinueWith(
                completed => _analysisInFlight.TryRemove(serverId, out _),
                TaskScheduler.Default);

            var finished = await Task.WhenAny(analyzeTask, Task.Delay(s_analysisTimeout, stoppingToken));

            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (finished != analyzeTask)
            {
                _logger.LogWarning(
                    "[{Server}] Scheduled analysis exceeded {Timeout}s — skipped this cycle",
                    server.Config.DisplayName, (int)s_analysisTimeout.TotalSeconds);
                return;
            }

            /* Analysis already persisted its findings inside AnalyzeAsync. Only route them
               to the notification channels when delivery is on (Lite's D0 split: production
               unconditional, delivery gated). */
            var findings = await analyzeTask;
            if (notifyFindings)
            {
                await notificationService.NotifyAsync(findings);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            /* Shutting down — the loop's own cancellation check ends the sweep. */
        }
        catch (Exception ex)
        {
            _logger.LogError("[{Server}] Scheduled analysis failed: {Message}",
                server.Config.DisplayName, ex.Message);
            /* If analyzeTask was never created (e.g. ctor threw), the continuation
               never ran — clear the marker defensively. */
            _analysisInFlight.TryRemove(serverId, out _);
        }
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
        ["latch_stats"] = (r, s, ct) => r.RunAsync(LatchStatsCollector.Instance, s, ct),
        ["spinlock_stats"] = (r, s, ct) => r.RunAsync(SpinlockStatsCollector.Instance, s, ct),
        ["cpu_scheduler_stats"] = (r, s, ct) => r.RunAsync(CpuSchedulerStatsCollector.Instance, s, ct),
        ["plan_cache_stats"] = (r, s, ct) => r.RunAsync(PlanCacheStatsCollector.Instance, s, ct),
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
        ["session_summary_stats"] = (r, s, ct) => r.RunAsync(SessionSummaryStatsCollector.Instance, s, ct),
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
        ["system_health_events"] = (r, s, ct) => r.RunAsync(SystemHealthEventsCollector.Instance, s, ct),
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

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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PerformanceMonitor.Alerting;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Common;
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

    /* The command plane's poll cadence (Stage 2): a tighter 5-second tick, run on its OWN loop
       independent of the 15-second collection sweep and the 30-second alert sweep, so an operator
       command (pause, test_connect, snapshot_now, ...) is picked up within ~5s and a slow command
       (a test_connect against an unreachable host can block for the connect timeout) never starves
       collection — the two loops share a cancellation token and the guarded server set only. */
    private static readonly TimeSpan s_commandPollInterval = TimeSpan.FromSeconds(5);

    /* The store disk-pressure self-alert's poll cadence (fleet-level, Stage 4). Disk fills slowly, and the
       check is one pg_database_size + one DriveInfo syscall, so 5 minutes is ample and cheap — no need to
       run it on the 30-second alert sweep. */
    private static readonly TimeSpan s_diskCheckInterval = TimeSpan.FromMinutes(5);

    /* The analysis pipeline's per-run budget — Lite's App default hardcoded (AnalysisTimeoutSeconds
       120; not a control-plane knob). The CADENCE (interval), the enabled gate, and the notify gate
       are now control-plane knobs read live from config.Analysis (config_alert_settings' analysis
       columns) — see the loop below. Each run analyzes the last 4 hours (Lite's hoursBack default). */
    private static readonly TimeSpan s_analysisTimeout = TimeSpan.FromSeconds(120);

    /* Clamp the store-driven analysis interval to Lite's accepted range (App clamps 5-360). */
    private const int MinAnalysisIntervalMinutes = 5;
    private const int MaxAnalysisIntervalMinutes = 360;

    /// <summary>Test hook: the hardcoded per-run analysis budget, pinned against Lite's default.</summary>
    internal static TimeSpan AnalysisTimeout => s_analysisTimeout;

    /// <summary>
    /// The Stage 2 pause gate: whether the collection sweep does work this tick. FALSE while the service is
    /// paused (<c>config_service.paused</c>, mirrored into <c>_paused</c> on reload) — the loop then skips all
    /// collection/alert/analysis/purge work but keeps polling the reload beacon and the command queue, so a
    /// resume un-pauses it on the next tick. Pure so the gate is unit-testable without driving the loop.
    /// </summary>
    internal static bool ShouldRunCollection(bool paused) => !paused;

    /// <summary>
    /// The network-endpoint startup warnings the worker emits AFTER <see cref="DarlingConfig.Validate"/>
    /// passes (darling-network-endpoints) — NEVER inside Validate(), which is all-fatal, so an optional,
    /// default-off endpoint note can never abort collection (D-BYO / D-validate):
    /// <list type="bullet">
    /// <item>BYO mode (<c>managed=false</c>) with any <c>postgres.network.*</c> or <c>mcp.network.*</c>
    /// set — the fields are IGNORED; the operator's own PostgreSQL governs exposure.</item>
    /// <item>Managed mode with an EXPOSED store whose <c>network.role</c> is <c>admin</c> — names the
    /// <c>config_command</c> / <c>config_monitored_servers</c> / <c>config_notification</c>
    /// service-credential pivot a remote admin connection can reach (D7 — the operator's informed opt-in).</item>
    /// </list>
    /// Pure so a unit test asserts the returned strings without driving the loop or a live logger.
    /// </summary>
    internal static IReadOnlyList<string> GetNetworkStartupWarnings(DarlingConfig config)
    {
        var warnings = new List<string>();

        if (!config.Postgres.Managed)
        {
            /* BYO: network.* is managed-mode only. Warn per section that is set (D-BYO). */
            if (config.Postgres.Network?.IsConfigured == true)
            {
                warnings.Add(
                    "postgres.network.* is set but postgres.managed is false — it is IGNORED in bring-your-own mode; your own PostgreSQL governs its network exposure (pg_hba / listen_addresses / TLS).");
            }

            if (config.Mcp.Network?.IsConfigured == true)
            {
                warnings.Add(
                    "mcp.network.* is set but postgres.managed is false — the MCP network endpoint is managed-mode only, so it is IGNORED; the MCP server stays loopback-only.");
            }

            return warnings;
        }

        /* Managed + the store is genuinely exposed + the network role resolves to admin (D7 pivot warning). */
        var network = config.Postgres.Network;
        if (network is not null
            && DarlingNetwork.IsExposedListenAddress(network.Listen)
            && string.Equals(DarlingNetwork.NormalizeNetworkRole(network.Role), "admin", StringComparison.Ordinal))
        {
            warnings.Add(
                "postgres.network.role is 'admin' — a REMOTE admin connection can write config_command (the test_connect service-credential pivot), config_monitored_servers, and config_notification (webhook exfil). This is an explicit opt-in; the secure default is 'viewer' (read-only). Only expose admin on a trusted network.");
        }

        return warnings;
    }

    private readonly ILogger<DarlingWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /* Set once by ExecuteAsync before the loop starts; the observability writes need it. */
    private NpgsqlDataSource? _postgres;

    /* The live control-plane state (Stage 1): the last-seen config_version reload beacon and the
       current sparse per-collector schedule overrides. Both updated on startup and on each reload;
       read by the schedule-resolution path (TryConnectAsync / RunDueCollectorsAsync) and the reload. */
    private long _lastConfigVersion = -1;
    private IReadOnlyList<ScheduleOverride> _scheduleOverrides = Array.Empty<ScheduleOverride>();

    /* The service-pause flag (Stage 2): read from config_service.paused on every reload and honored by
       the collection loop (skip collection/alert/analysis/purge while paused — Lite's IsPaused gate). Set
       ONLY by the main loop's reload (single writer); the command loop keeps running while paused so a
       resume command is processed. A pause/resume command writes the store, the bump trigger fires the
       reload beacon, and the reload sets this — the same path every other control-plane setting takes. */
    private bool _paused;

    /* Guards structural access to the monitored-server list because the command loop (a concurrent task)
       looks servers up by id while the main loop reconciles (adds/removes) them and the alert / analysis
       plan/failed-job fetchers enumerate them. Only ever held for the microsecond lookup/mutation — never
       across collection I/O — so a long-running command never blocks the collection loop on it. */
    private readonly object _serversLock = new();

    /* Server IDs whose scheduled analysis is currently running — prevents relaunching
       analysis for a server whose previous (possibly hung) pass has not finished
       (Lite's CollectionBackgroundService in-flight guard). */
    private readonly ConcurrentDictionary<int, byte> _analysisInFlight = new();

    /* MinValue = the first sweep after startup runs the retention purge, then daily. */
    private DateTime _nextPurgeUtc = DateTime.MinValue;

    /* MinValue = the first sweep after startup evaluates the store disk-pressure self-alert, then every
       s_diskCheckInterval. Fleet-level (one shared store), so it is a single field, not per-server. */
    private DateTime _nextDiskCheckUtc = DateTime.MinValue;

    /* Set once at startup by the TimescaleSupport detection (cached per data source — the
       extension can't appear or vanish under a running service without a restart anyway);
       branches the retention purge onto drop_chunks. */
    private bool _timescaleAvailable;

    /* Stage 4 service self-alerts (collection-stopped, connection lost/restored, capture-down). Built
       once in RunCollectionLoopAsync over the SAME deliverer/history the shared engine uses, so the
       self-alerts inherit its delivery/cooldown/restart-replay. Held as a field because the connection
       edge fires from TryConnectAsync and the reconcile drops per-server state through it. */
    private DarlingSelfAlertEvaluator? _selfAlerts;

    public DarlingWorker(ILogger<DarlingWorker> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    private sealed class ServerLoopState
    {
        /* Settable so the reconcile can replace a still-connected server's definition on a config
           change (host/auth/excluded-dbs/cost) — paired with dropping Runtime to force a reconnect. */
        public required MonitoredServer Config { get; set; }
        public ServerRuntime? Runtime { get; set; }
        public Dictionary<string, DateTime> NextDue { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime NextConnectAttempt { get; set; } = DateTime.MinValue;

        /* MinValue = the first loop pass after connect evaluates alerts immediately. */
        public DateTime NextAlertSweep { get; set; } = DateTime.MinValue;

        /* MinValue = the first loop pass evaluates the Stage 4 service self-alerts (collection-stopped,
           capture-down) immediately. Separate from NextAlertSweep because the self-alert sweep runs for
           a DISCONNECTED server too (collection-stopped is exactly the unreachable-server case), above
           the Runtime-null connect gate. */
        public DateTime NextSelfAlertSweep { get; set; } = DateTime.MinValue;

        /* MinValue = the first loop pass after connect runs analysis immediately; the
           pipeline's own 24h data-span gate no-ops it until the store has enough history
           (Lite's GetTotalDataSpanHoursAsync gate), so a fresh server simply re-checks
           every interval while an already-populated store analyzes right away. */
        public DateTime NextAnalysisDue { get; set; } = DateTime.MinValue;

        /* Serializes this server's collector batch so an on-demand snapshot_now (from the command loop)
           and the scheduled sweep (from the main loop) never run the same server's collectors at once —
           which would double-COPY overlapping rows and race the shared delta baselines. The main loop
           try-acquires with a zero timeout (skip this server this sweep if a snapshot holds it); the
           snapshot waits its turn. Binary (1,1). */
        public SemaphoreSlim CollectionGate { get; } = new(1, 1);
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

        /* Network-endpoint caller warnings (darling-network-endpoints, D-BYO / D7) — emitted AFTER
           Validate() passes and NEVER inside it (Validate is all-fatal; an optional-endpoint note must not
           abort startup). Covers BYO-mode network.* being ignored and the network.role=admin pivot risk. */
        foreach (var warning in GetNetworkStartupWarnings(config))
        {
            _logger.LogWarning("{Warning}", warning);
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

        /* Control-plane Stage 1: SEED the config store from darling.json once (idempotent; only empty
           sections), then read the store view and make it authoritative — the held DarlingConfig is
           mutated in place so the alert-settings/capture-plans/analysis seams below all reflect the
           store. Store-unreachable degrades to the darling.json-loaded config (never worse than before).
           The initial server set comes from config_monitored_servers WHERE is_enabled = TRUE (post-seed
           equivalent to darling.json, but store-authoritative); the config_version read here is the
           reload baseline, so the seed's own version bumps do not trigger a spurious first-sweep reload. */
        var configProvider = new StoreConfigProvider(postgres, _logger);
        await configProvider.SeedIfEmptyAsync(config, stoppingToken);
        var initialView = await configProvider.LoadViewAsync(config, stoppingToken);
        IReadOnlyList<MonitoredServer> initialServers = config.Servers;
        if (initialView is not null)
        {
            StoreConfigProvider.ApplyToConfig(config, initialView);
            _scheduleOverrides = initialView.ScheduleOverrides;
            _lastConfigVersion = initialView.ConfigVersion;
            /* Stage 2: honor config_service.paused from the very first sweep (a service that was paused
               before a restart comes back up paused). */
            _paused = initialView.Paused;
            /* Reconcile the observed collect.servers.is_enabled to the desired state up front, so a server
               disabled in the store while the service was down shows disabled even though it never connects
               (never upserts) this run. */
            await DarlingObservability.SyncServerEnabledStatesAsync(postgres, _logger, stoppingToken);
            /* Post-seed the store carries darling.json's servers; fall back to the file only if the store
               read is empty (a partially-seeded store), so the service never starts up monitoring nothing. */
            if (initialView.EnabledServers.Count > 0)
            {
                initialServers = initialView.EnabledServers;
            }
        }

        /* Capture-plans is read live (() => config.CapturePlans) so a store reload of
           config_service.capture_plans is honored on the next collector cycle without rebuilding. */
        var runner = new DarlingCollectorRunner(postgres, deltas, _logger, () => config.CapturePlans);
        var servers = new List<ServerLoopState>();
        foreach (var server in initialServers)
        {
            servers.Add(new ServerLoopState { Config = server });
        }

        /* Phase-5 slice D: the shared alert engine, wired to the PG-backed stores (V3) and the
           shared email/webhook delivery. Constructed once — the engine holds the per-server
           edge-trigger state for the service's lifetime. The settings/history/webhook pieces
           are hoisted here because the AN3 analysis-notification path below shares them. The mute
           service is hoisted too so a reload can re-LoadAsync() it (closes F16 — the engine holds
           its IsAlertMuted delegate, so refreshing the same instance's cache mutes the next sweep). */
        var alertSettings = new DarlingAlertSettings(config);
        var historyStore = new PgAlertHistoryStore(postgres, _logger);
        var webhookAlertService = new WebhookAlertService(
            alertSettings, DarlingAlertDeliverer.Branding,
            _loggerFactory.CreateLogger<WebhookAlertService>(), historyStore);
        var muteRuleService = new MuteRuleService(
            new PgMuteRuleStore(postgres, _logger), _loggerFactory.CreateLogger<MuteRuleService>());
        await muteRuleService.LoadAsync();

        /* The shared record-and-send deliverer — hoisted so BOTH the shared alert engine and the Stage 4
           service self-alerts (DarlingSelfAlertEvaluator) fire through the SAME instance: identical
           email/webhook delivery, per-fingerprint delivery cooldown, and history-seeded restart replay. */
        var deliverer = new DarlingAlertDeliverer(
            alertSettings, historyStore, webhookAlertService, _logger,
            /* #1236: map a fired alert's ServerKey (the storage-name hash the engine keys on) back to the live
               server's per-server delivery override, under the servers lock (mirrors FetchFailedJobsAsync). A
               store reload swaps ServerLoopState.Config, so this always reads the current override. */
            resolveServerOverride: serverKey =>
            {
                if (!int.TryParse(serverKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    return null;
                }

                lock (_serversLock)
                {
                    return servers
                        .Find(s => ServerIdHelper.GetDeterministicHashCode(s.Config.StorageName) == id)
                        ?.Config.AlertDeliveryModeOverride;
                }
            });
        var engine = BuildAlertEngine(config, servers, alertSettings, historyStore, muteRuleService, deliverer);

        /* Stage 4: the service self-alerts, over the SAME deliverer + history + mute check the engine uses.
           collection-stopped / capture-down are polled from collection_log on the alert cadence below;
           connection lost/restored fire on the connect edges in TryConnectAsync. */
        _selfAlerts = new DarlingSelfAlertEvaluator(
            alertSettings, deliverer, historyStore, muteRuleService.IsAlertMuted, _logger,
            /* V20: the connect-edge Server-Unreachable/Restored delivery honors the notify toggle, read live
               through the same by-reference alertSettings seam a store reload hot-swaps. */
            notifyConnectionChanges: () => alertSettings.NotifyConnectionChanges);

        /* Phase-5 analysis slice AN3: the analysis pipeline's shared pieces, constructed once.
           The plan fetcher resolves a finding's serverId to the CONNECTED runtime's connection
           string (the PgPlanFetcher seam — null for an unknown/disconnected server degrades the
           fetch like Lite's ServerManager miss). The shared AnalysisNotificationService routes
           high-severity findings through DarlingFindingAlertSender (email + webhook + history,
           Lite's cadence); the serverId resolver is Lite's shape (the finding's int id as a
           string), no silencing predicate and no tray sink (headless). */
        var planFetcher = new PgPlanFetcher(
            /* Enumerated from the command loop (analyze_now) as well as the main loop, so guard the read
               against a concurrent reconcile add/remove. Held only for the lookup, never across the fetch. */
            serverId =>
            {
                lock (_serversLock)
                {
                    return servers
                        .Select(s => s.Runtime)
                        .FirstOrDefault(r => r is not null && r.ServerId == serverId)?.ConnectionString;
                }
            },
            _logger);
        var notificationService = new AnalysisNotificationService(
            new DarlingFindingAlertSender(alertSettings, historyStore, webhookAlertService, _logger),
            alertSettings,
            finding => finding.ServerId.ToString(CultureInfo.InvariantCulture),
            _loggerFactory.CreateLogger<AnalysisNotificationService>());

        /* Command plane (Stage 2): the executor claims/executes/reports config_command rows on its OWN
           5-second loop, concurrent with the collection sweep, so a slow command never stalls collection.
           The host lets snapshot_now/analyze_now reach the LIVE loop (the running server set + runner +
           analysis pieces) without the executor touching that mutable state directly; every other command
           only writes the config.* tables and rides the reload beacon. Launched here and awaited after the
           collection loop stops so both drain cleanly on shutdown. */
        var commandHost = new WorkerCommandHost(this, servers, runner, planFetcher, notificationService, config);
        var serviceInstance = $"{Environment.MachineName}:{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}";
        var commandExecutor = new DarlingCommandExecutor(postgres, commandHost, serviceInstance, _logger);
        var commandLoop = RunCommandLoopAsync(commandExecutor, stoppingToken);

        _logger.LogInformation("PerformanceMonitor Darling collection loop started");

        while (!stoppingToken.IsCancellationRequested)
        {
            /* Control-plane reload beacon: poll config_version at a SAFE point (top of the sweep, never
               mid-collection). On change, re-read the store and hot-swap the live config: the alert /
               SMTP / webhook / capture / analysis settings (via the by-reference DarlingAlertSettings
               seam + the runner's capture provider), the monitored-server set (add/remove/replace
               ServerLoopState), the per-collector schedule overrides + NextDue, and the mute-rule cache. */
            var configVersion = await configProvider.ReadConfigVersionAsync(stoppingToken);
            if (configVersion.HasValue && configVersion.Value != _lastConfigVersion)
            {
                _lastConfigVersion = configVersion.Value;
                await ReloadFromStoreAsync(configProvider, config, servers, muteRuleService, stoppingToken);
            }

            /* Stage 2 pause gate (Lite's IsPaused): while paused, skip ALL collection/alert/analysis/purge
               work but keep looping — the reload beacon above still runs (so a resume, applied via the same
               reload, un-pauses on the very next tick) and the command loop keeps draining commands. A
               resume that reloaded THIS iteration already flipped _paused to false above, so it takes effect
               immediately, not a sweep later. */
            if (!ShouldRunCollection(_paused))
            {
                try
                {
                    await Task.Delay(s_sweepInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            foreach (var server in servers)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                /* Stage 4 service self-alerts (store-polled): collection-stopped is evaluated for EVERY
                   server — connected or not — because an unreachable server has stopped collecting, which
                   is exactly the case a headless service must page on. Capture-down is evaluated only for a
                   connected server. Own 30s cadence; the master alerts gate + edge-trigger live inside the
                   evaluator. Runs ABOVE the Runtime-null connect gate so a disconnected server is still
                   checked. Connection lost/restored fire on the connect edges in TryConnectAsync. */
                if (DateTime.UtcNow >= server.NextSelfAlertSweep)
                {
                    server.NextSelfAlertSweep = DateTime.UtcNow.Add(s_alertSweepInterval);
                    await _selfAlerts!.EvaluateStoreAlertsAsync(
                        postgres,
                        ServerIdHelper.GetDeterministicHashCode(server.Config.StorageName),
                        server.Config.DisplayName,
                        connected: server.Runtime is not null,
                        stoppingToken);
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

                /* AN3: the scheduled analysis pipeline, per-server. The cadence, the enabled gate, and
                   the notify gate are now control-plane knobs read LIVE from config.Analysis (a reload
                   takes effect on the next tick). When analysis is disabled the pass is skipped and
                   NextAnalysisDue is left in the past, so re-enabling runs immediately. The next-due
                   stamp advances up front (Lite's scheduler shape), so a timed-out pass is skipped, not
                   retried immediately. Delivery is gated on analysis_notifications_enabled (Lite's D0
                   split: production unconditional, delivery gated) — replacing the old alerts.enabled
                   gate; the interval is clamped to Lite's 5-360 range. */
                if (config.Analysis.Enabled && DateTime.UtcNow >= server.NextAnalysisDue)
                {
                    var intervalMinutes = Math.Clamp(config.Analysis.IntervalMinutes, MinAnalysisIntervalMinutes, MaxAnalysisIntervalMinutes);
                    server.NextAnalysisDue = DateTime.UtcNow.AddMinutes(intervalMinutes);
                    await RunScheduledAnalysisAsync(
                        server, planFetcher, notificationService, config.Analysis.NotificationsEnabled, stoppingToken);
                }
            }

            if (DateTime.UtcNow >= _nextPurgeUtc)
            {
                _nextPurgeUtc = DateTime.UtcNow.AddHours(24);
                /* Honor fleet-wide retention overrides (config_collector_schedules, server_id NULL) layered
                   on CollectorScheduleDefaults; a per-server override can't apply to a shared-table purge.
                   Empty overrides (Stage 1 seeds none) resolve to the defaults — identical behavior. */
                var overrides = _scheduleOverrides;
                await DarlingRetention.PurgeAsync(
                    postgres, _timescaleAvailable, _logger, stoppingToken,
                    name => StoreConfigProvider.ResolveFleetRetentionDays(name, overrides));

                /* AN3: findings retention. Both apps' finding stores declare a 30-day cleanup
                   but neither app schedules it (Lite's DuckDB archive-reset bounds it
                   incidentally); a 24/7 service must actually invoke it or analysis_findings
                   grows unbounded. Rides the daily purge; never throws (logs + degrades). */
                await new PgFindingStore(postgres, _logger).CleanupOldFindingsAsync(retentionDays: 30);
            }

            /* Stage 4 fleet-level self-alert: the store disk-pressure backstop. The daily purge is the ONLY
               other maintenance cadence and it is purely time-based (no disk-free check), so on its own the
               store can still fill between purges — this edge-fired condition is the flagship-appropriate
               backstop. Own slow cadence; the master alerts gate + edge-trigger live inside the evaluator. */
            if (DateTime.UtcNow >= _nextDiskCheckUtc)
            {
                _nextDiskCheckUtc = DateTime.UtcNow.Add(s_diskCheckInterval);
                await EvaluateStoreDiskPressureAsync(config, stoppingToken);
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

        /* Drain the concurrent command loop on shutdown (it observes the same token). */
        try
        {
            await commandLoop;
        }
        catch (OperationCanceledException)
        {
            /* Expected on shutdown. */
        }

        _logger.LogInformation("PerformanceMonitor Darling collection loop stopped");
    }

    /// <summary>
    /// The command plane's poll loop (Stage 2), run concurrently with the collection sweep on its own
    /// ~5-second tick. Each tick DRAINS every currently-pending command (claim one at a time until the
    /// queue is empty), so a burst of viewer commands is not throttled to one per 5 seconds. Never throws
    /// out — a per-command failure is reported on the row and swallowed by the executor — so the loop lives
    /// for the service's lifetime. Cancellation ends it cleanly.
    /// </summary>
    private async Task RunCommandLoopAsync(DarlingCommandExecutor executor, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                /* Robustness (Stage 2 follow-up): before draining the queue, reclaim any command left
                   in_progress by a crashed/restarted service instance so a Viewer polling it is not stranded
                   forever. Cheap (an indexed UPDATE matching nothing in the normal case) and safe to run
                   every tick — the 5-minute staleness margin can never catch a merely-slow live command. A
                   reclaimed row is marked terminal 'failed' (not re-queued), so the drain below never re-runs
                   a non-idempotent command; see DarlingCommandExecutor.ReclaimStaleCommandsSql. */
                await executor.ReclaimStaleCommandsAsync(stoppingToken);

                while (await executor.PollOnceAsync(stoppingToken))
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                /* Belt-and-suspenders: PollOnceAsync already swallows its own failures, but a truly
                   unexpected throw must not kill the loop. */
                _logger.LogWarning("Command loop tick failed: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(s_commandPollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Applies a control-plane reload at a SAFE point (top of a sweep, never mid-collection): re-reads the
    /// store, hot-swaps the held config's alert/SMTP/webhook/capture/analysis settings IN PLACE (the
    /// by-reference DarlingAlertSettings seam + the runner's capture provider reflect it immediately),
    /// reconciles the monitored-server set, recomputes each connected server's NextDue from the fresh
    /// schedule overrides, and reloads the mute-rule cache (F16). Store-unreachable is a no-op — the current
    /// live config stands, never worse than before.
    /// </summary>
    private async Task ReloadFromStoreAsync(
        StoreConfigProvider provider, DarlingConfig config, List<ServerLoopState> servers,
        MuteRuleService muteRuleService, CancellationToken cancellationToken)
    {
        var view = await provider.LoadViewAsync(config, cancellationToken);
        if (view is null)
        {
            return;
        }

        StoreConfigProvider.ApplyToConfig(config, view);
        _scheduleOverrides = view.ScheduleOverrides;
        /* Stage 2: honor a pause/resume issued through the store (config_service.paused) — the collection
           loop reads this on its next tick. Single writer (this reload), so no interlock needed. */
        _paused = view.Paused;

        /* Structural reconcile mutates the server list; the command loop reads it concurrently, so hold
           the lock across the add/remove. NextDue recompute mutates only per-server state (safe against a
           concurrent id lookup) so it stays outside the lock. */
        lock (_serversLock)
        {
            ReconcileServers(servers, view.EnabledServers);
        }

        RecomputeNextDue(servers);

        /* Mirror the desired enable state onto the observed collect.servers registry so a disable_server
           flips its observed row FALSE even though the disabled server drops out of the loop (stops
           upserting), and an enable_server flips it back. */
        await DarlingObservability.SyncServerEnabledStatesAsync(_postgres!, _logger, cancellationToken);

        await muteRuleService.LoadAsync();

        _logger.LogInformation(
            "Control-plane reload applied (config_version {Version}, {Servers} monitored server(s), paused: {Paused})",
            _lastConfigVersion, servers.Count, _paused);
    }

    /// <summary>
    /// Reconciles the live <see cref="ServerLoopState"/> set to the store's desired enabled set, keyed by the
    /// shared server_id. ADD: a new enabled server gets a disconnected state that connects on its next tick
    /// exactly like a startup server (via <see cref="TryConnectAsync"/> — no novel connection logic). REMOVE:
    /// a server no longer enabled/present is dropped; its runtime holds no persistent connection (the
    /// collectors open per run), so dropping the state is a clean disconnect. STAY: the held config is always
    /// refreshed to the desired definition (so a non-connection edit — cost, the #1236 alert-delivery override
    /// — goes live without churn), but the connection + NextDue are preserved unless a connection/collection
    /// field changed (per <see cref="ServerDefinitionEquals"/>), in which case the runtime is dropped so it
    /// reconnects with the new definition through the same startup path.
    /// </summary>
    private void ReconcileServers(List<ServerLoopState> servers, IReadOnlyList<MonitoredServer> desired)
    {
        var desiredById = new Dictionary<int, MonitoredServer>();
        foreach (var d in desired)
        {
            desiredById[ServerIdHelper.GetDeterministicHashCode(d.StorageName)] = d;
        }

        for (int i = servers.Count - 1; i >= 0; i--)
        {
            var state = servers[i];
            var id = ServerIdHelper.GetDeterministicHashCode(state.Config.StorageName);
            if (!desiredById.TryGetValue(id, out var desiredServer))
            {
                _logger.LogInformation(
                    "[{Server}] Removed from the monitored set (disabled/deleted) — stopping collection",
                    state.Config.DisplayName);
                state.Runtime = null;
                /* Drop the Stage 4 self-alert edge state so a later re-add starts from the Unknown baseline
                   (no stale "was online" / "was stopped" flag carried across a remove+re-add). */
                _selfAlerts?.Forget(id);
                servers.RemoveAt(i);
                continue;
            }

            /* Always refresh the held definition so a NON-connection edit — the FinOps cost, or the #1236
               alert-delivery override the deliverer's resolver reads live off state.Config — takes effect on
               this reload without connection churn. Only a connection/collection-affecting change (per
               ServerDefinitionEquals) drops the runtime to reconnect through the startup path. */
            var connectionChanged = !ServerDefinitionEquals(state.Config, desiredServer);
            state.Config = desiredServer;
            if (connectionChanged)
            {
                _logger.LogInformation(
                    "[{Server}] Definition changed — reconnecting with the new configuration", desiredServer.DisplayName);
                state.Runtime = null;
                state.NextConnectAttempt = DateTime.MinValue;
                state.NextDue.Clear();
            }

            desiredById.Remove(id);
        }

        foreach (var addition in desiredById.Values)
        {
            _logger.LogInformation(
                "[{Server}] Added to the monitored set — will connect on the next sweep", addition.DisplayName);
            servers.Add(new ServerLoopState { Config = addition });
        }
    }

    /// <summary>
    /// Recomputes each CONNECTED server's per-collector NextDue from the current schedule overrides after a
    /// reload: a disabled or on-load-only (freq 0) collector is dropped from the schedule; a newly-enabled one
    /// becomes due now; an existing entry is pulled in to at most now + the (possibly shortened) effective
    /// interval so a frequency change takes effect promptly without over-firing. A server still connecting has
    /// no NextDue yet — <see cref="TryConnectAsync"/> seeds it from the fresh overrides when it connects.
    /// </summary>
    private void RecomputeNextDue(List<ServerLoopState> servers)
    {
        var now = DateTime.UtcNow;
        foreach (var server in servers)
        {
            var runtime = server.Runtime;
            if (runtime is null)
            {
                continue;
            }

            foreach (var name in CollectorScheduleDefaults.All.Keys)
            {
                var effective = StoreConfigProvider.ResolveSchedule(name, runtime.ServerId, _scheduleOverrides);
                if (!effective.Enabled || effective.FrequencyMinutes == 0)
                {
                    server.NextDue.Remove(name);
                    continue;
                }

                var capped = now.AddMinutes(effective.FrequencyMinutes);
                server.NextDue[name] = server.NextDue.TryGetValue(name, out var existing) && existing < capped
                    ? existing
                    : capped;
            }
        }
    }

    /// <summary>
    /// Whether two server definitions are identical for the collection loop — the connection-relevant fields
    /// plus the collection-affecting excluded databases. A difference triggers a reconnect on reconcile so the
    /// new definition takes effect. <c>MonthlyCostUsd</c> is deliberately NOT compared: it does not affect
    /// collection at all, and the reload's <see cref="DarlingObservability.SyncServerEnabledStatesAsync"/>
    /// mirrors a cost change straight onto <c>collect.servers</c> (which the FinOps display reads) with no
    /// connection churn — so a cost-only edit must NOT trigger a disconnect+reconnect. Internal so a unit test
    /// can pin exactly that.
    /// </summary>
    internal static bool ServerDefinitionEquals(MonitoredServer a, MonitoredServer b)
        => string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Database, b.Database, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Auth, b.Auth, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Username, b.Username, StringComparison.Ordinal)
        && string.Equals(a.EncryptedPassword, b.EncryptedPassword, StringComparison.Ordinal)
        && string.Equals(a.Password, b.Password, StringComparison.Ordinal)
        && string.Equals(a.EncryptMode, b.EncryptMode, StringComparison.OrdinalIgnoreCase)
        && a.TrustServerCertificate == b.TrustServerCertificate
        && a.ReadOnlyIntent == b.ReadOnlyIntent
        && a.MultiSubnetFailover == b.MultiSubnetFailover
        && a.ExcludedDatabases.SequenceEqual(b.ExcludedDatabases, StringComparer.OrdinalIgnoreCase);

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
    private AlertEngine BuildAlertEngine(
        DarlingConfig config, List<ServerLoopState> servers,
        DarlingAlertSettings alertSettings, PgAlertHistoryStore historyStore,
        MuteRuleService muteRuleService, DarlingAlertDeliverer deliverer)
    {
        var postgres = _postgres!;
        var stateStore = new PgAlertStateStore(postgres, _logger);

        /* The mute service is loaded + owned by the caller (RunCollectionLoopAsync) so a control-plane
           reload can re-LoadAsync() the SAME instance and mute the very next sweep (F16). The engine
           binds its IsAlertMuted delegate, which reads the refreshed cache. The deliverer is hoisted by
           the caller and shared with the Stage 4 self-alerts (same delivery/cooldown/restart-replay). */

        return new AlertEngine(
            alertSettings,
            new DarlingAlertReadAdapter(postgres),
            stateStore,
            deliverer,
            muteRuleService.IsAlertMuted,
            failedJobsFetcher: (serverKey, lookbackMinutes, ct) =>
                FetchFailedJobsAsync(servers, serverKey, lookbackMinutes, ct),
            resolutionCallback: async (resolution, _) =>
            {
                _logger.LogInformation("[{Server}] {Title}: {Message}",
                    resolution.ServerName, resolution.Title, resolution.Message);
                /* Stage 4 parity-gap fix: record a resolved-flavored history row so an operator reviewing
                   alert history sees the paired "Detected" then "Cleared/Resolved" entries (the Dashboard
                   records these explicitly; Darling previously only logged them). A resolution has no send
                   channel, so this never emails/webhooks; RecordAlertAsync is failure-isolated so it can
                   never break the sweep. */
                await historyStore.RecordAlertAsync(DarlingSelfAlertEvaluator.BuildResolutionRecord(resolution));
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
    /// Gathers the store disk-pressure sample and hands it to the Stage 4 evaluator (fleet-level). The store
    /// size (<c>pg_database_size</c>, context only) is always readable; the store volume's free/total space is
    /// resolved from the MANAGED data directory's drive — the bundled store this service owns and must protect.
    /// In bring-your-own mode the store can be a remote Postgres whose disk the service cannot see, so
    /// free/total stay null and the evaluator no-ops (never a false alarm — the operator owns their own
    /// PostgreSQL's disk monitoring, consistent with the BYO posture elsewhere). Failure-isolated: a bad
    /// sample logs at Debug and skips this tick, never breaking the loop.
    /// </summary>
    private async Task EvaluateStoreDiskPressureAsync(DarlingConfig config, CancellationToken cancellationToken)
    {
        long? storeSizeBytes = await ReadStoreSizeBytesAsync(cancellationToken);

        long? freeBytes = null;
        long? totalBytes = null;
        if (config.Postgres.Managed && OperatingSystem.IsWindows())
        {
            try
            {
                var dataDirectory = DarlingManagedPostgres.ResolveDataDirectory(config.Postgres);
                var root = Path.GetPathRoot(Path.GetFullPath(dataDirectory));
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady)
                    {
                        freeBytes = drive.TotalFreeSpace;
                        totalBytes = drive.TotalSize;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                /* Best-effort: an unreadable drive just means no disk signal this tick. */
                _logger.LogDebug("Store disk-pressure check: could not read the store volume free space: {Message}", ex.Message);
            }
        }

        /* EvaluateDiskPressureAsync (not ApplyDiskPressureAsync) so a throwing seam — e.g. a mute rule's
           Matches() in the pre-deliver mute check — is isolated inside the evaluator, exactly like the
           per-server EvaluateStoreAlertsAsync. This sweep-loop body has no catch-all of its own, so an
           un-isolated throw here would stop collection for the whole fleet. */
        await _selfAlerts!.EvaluateDiskPressureAsync(freeBytes, totalBytes, storeSizeBytes, cancellationToken);
    }

    /// <summary>
    /// The store database's on-disk size in bytes (<c>pg_database_size</c>) — context for the disk-pressure
    /// alert text, the same read the Viewer's status bar uses. Failure-isolated to null (Debug) so a transient
    /// store hiccup never breaks the disk-pressure check.
    /// </summary>
    private async Task<long?> ReadStoreSizeBytesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _postgres!.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand("SELECT pg_database_size(current_database())", connection);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is null || result == DBNull.Value ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Store disk-pressure check: could not read pg_database_size: {Message}", ex.Message);
            return null;
        }
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

        /* The scheduled caller discards the outcome — the analyze_now command maps it to a result. */
        await RunAnalysisPassAsync(
            runtime.ServerId, runtime.StorageName, server.Config.DisplayName,
            planFetcher, notificationService, notifyFindings, stoppingToken);
    }

    /// <summary>Terminal states of one analysis pass — surfaced to the analyze_now command result.</summary>
    private enum AnalysisPassStatus { Ran, Skipped, TimedOut, InsufficientData, Error }

    private sealed record AnalysisPassResult(AnalysisPassStatus Status, int FindingCount, string? Message);

    /// <summary>
    /// One analysis pass for a server — the shared core of the scheduled sweep and the <c>analyze_now</c>
    /// command. Lite's per-server body: the in-flight guard skips a server whose previous (possibly hung)
    /// pass has not finished; a FRESH <see cref="DarlingAnalysisService"/> per run (IsAnalyzing is a single
    /// instance flag); the 120-second timeout moves on without clearing the in-flight marker (the
    /// continuation clears it only when the task truly finishes, so a hung server is not relaunched);
    /// findings persist inside AnalyzeAsync and route to the notification channels only when delivery is on
    /// (Lite's D0 split). Returns the terminal state so the command path can report it; the scheduled caller
    /// ignores the return. Analyzes the STORAGE identity (Lite's GetServerNameForStorage), so a disconnected
    /// but previously-collected server can still be analyzed on demand (its stored data drives the pass).
    /// </summary>
    private async Task<AnalysisPassResult> RunAnalysisPassAsync(
        int serverId,
        string storageName,
        string displayName,
        PgPlanFetcher planFetcher,
        AnalysisNotificationService notificationService,
        bool notifyFindings,
        CancellationToken stoppingToken)
    {
        if (!_analysisInFlight.TryAdd(serverId, 0))
        {
            return new AnalysisPassResult(AnalysisPassStatus.Skipped, 0, "analysis is already running for this server");
        }

        try
        {
            var analysisService = new DarlingAnalysisService(_postgres!, planFetcher, _logger);
            var analyzeTask = analysisService.AnalyzeAsync(serverId, storageName, hoursBack: 4);

            /* Clear the in-flight marker only when the task truly finishes — not
               when the timeout below moves us on — so a hung server is not relaunched. */
            _ = analyzeTask.ContinueWith(
                completed => _analysisInFlight.TryRemove(serverId, out _),
                TaskScheduler.Default);

            var finished = await Task.WhenAny(analyzeTask, Task.Delay(s_analysisTimeout, stoppingToken));

            if (stoppingToken.IsCancellationRequested)
            {
                return new AnalysisPassResult(AnalysisPassStatus.Skipped, 0, "service is stopping");
            }

            if (finished != analyzeTask)
            {
                _logger.LogWarning(
                    "[{Server}] Analysis exceeded {Timeout}s — skipped this cycle",
                    displayName, (int)s_analysisTimeout.TotalSeconds);
                return new AnalysisPassResult(AnalysisPassStatus.TimedOut, 0, $"analysis exceeded {(int)s_analysisTimeout.TotalSeconds}s");
            }

            /* Analysis already persisted its findings inside AnalyzeAsync. Only route them
               to the notification channels when delivery is on (Lite's D0 split: production
               unconditional, delivery gated). */
            var findings = await analyzeTask;
            if (notifyFindings)
            {
                await notificationService.NotifyAsync(findings);
            }

            /* Persist the pass's insufficient-data determination (V19 marker) so the Viewer's
               Recommendations tab shows "still collecting" instead of a false all-clear on a young
               deployment: true + the engine's message when the pass hit the 24h data-span gate, cleared
               (false) when a real pass completed on enough data. Failure-isolated like the other
               observability writes. Only the two REAL terminal states write it — a Skipped/TimedOut/Error
               pass (handled above / in the catch) leaves the last known marker untouched. */
            if (analysisService.InsufficientDataMessage is string insufficient)
            {
                await DarlingObservability.WriteAnalysisStateAsync(
                    _postgres!, serverId, insufficientData: true, insufficient, _logger, stoppingToken);
                return new AnalysisPassResult(AnalysisPassStatus.InsufficientData, 0, insufficient);
            }

            await DarlingObservability.WriteAnalysisStateAsync(
                _postgres!, serverId, insufficientData: false, null, _logger, stoppingToken);
            return new AnalysisPassResult(AnalysisPassStatus.Ran, findings.Count, null);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            /* Shutting down — the loop's own cancellation check ends the sweep. */
            return new AnalysisPassResult(AnalysisPassStatus.Skipped, 0, "service is stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError("[{Server}] Analysis failed: {Message}", displayName, ex.Message);
            /* If analyzeTask was never created (e.g. ctor threw), the continuation
               never ran — clear the marker defensively. */
            _analysisInFlight.TryRemove(serverId, out _);
            return new AnalysisPassResult(AnalysisPassStatus.Error, 0, ex.Message);
        }
    }

    /// <summary>
    /// The <c>analyze_now</c> command handler (Recommendations "Generate now", control-plane form): forces
    /// an immediate analysis pass for one monitored server, bypassing its NextAnalysisDue wait, and maps the
    /// terminal outcome to a command result. Shares the in-flight guard with the scheduled sweep, so it
    /// no-ops (reported "already running") rather than racing a pass in flight for the same server.
    /// </summary>
    private async Task<CommandOutcome> RunAnalyzeNowAsync(
        List<ServerLoopState> servers,
        PgPlanFetcher planFetcher,
        AnalysisNotificationService notificationService,
        DarlingConfig config,
        int serverId,
        CancellationToken cancellationToken)
    {
        ServerLoopState? server;
        lock (_serversLock)
        {
            server = servers.Find(s => ServerIdHelper.GetDeterministicHashCode(s.Config.StorageName) == serverId);
        }

        if (server is null)
        {
            return new CommandOutcome(false, "server not monitored", JsonError($"no monitored server with server_id {serverId}"));
        }

        var result = await RunAnalysisPassAsync(
            serverId, server.Config.StorageName, server.Config.DisplayName,
            planFetcher, notificationService, config.Analysis.NotificationsEnabled, cancellationToken);

        return result.Status switch
        {
            AnalysisPassStatus.Ran => new CommandOutcome(true, "analysis complete",
                JsonSerializer.Serialize(new { success = true, server = server.Config.DisplayName, findings = result.FindingCount })),
            AnalysisPassStatus.InsufficientData => new CommandOutcome(true, "insufficient data",
                JsonSerializer.Serialize(new { success = true, server = server.Config.DisplayName, message = result.Message })),
            AnalysisPassStatus.Skipped => new CommandOutcome(false, "analysis already running", JsonError(result.Message ?? "skipped")),
            AnalysisPassStatus.TimedOut => new CommandOutcome(false, "analysis timed out", JsonError(result.Message ?? "timed out")),
            _ => new CommandOutcome(false, "analysis failed", JsonError(result.Message ?? "error")),
        };
    }

    /// <summary>A failure result_json body: <c>{ "success": false, "error": ... }</c>.</summary>
    private static string JsonError(string error) => JsonSerializer.Serialize(new { success = false, error });

    /// <summary>
    /// The <c>purge_now</c> command handler (the daily retention purge on demand): runs
    /// <see cref="DarlingRetention.PurgeAsync"/> over the shared store immediately and reports the tables +
    /// rows purged. Fleet-wide over the SHARED tables (no target server). When
    /// <paramref name="customRetentionDays"/> is set it purges every collector to that horizon (a
    /// <c>_ =&gt; customDays</c> resolver — PurgeAsync clamps a sub-1-day horizon at its destructive sink, so a
    /// bad custom-N can never wipe a table); otherwise it uses the SAME fleet resolver the scheduled daily
    /// purge uses (<see cref="StoreConfigProvider.ResolveFleetRetentionDays"/> over the live overrides). Takes
    /// NO collection gate: unlike snapshot_now a purge writes no collector state and races no delta baseline,
    /// and PurgeAsync is idempotent + failure-isolated per table, so it may safely overlap the daily sweep.
    /// </summary>
    private async Task<CommandOutcome> RunPurgeNowAsync(int? customRetentionDays, CancellationToken cancellationToken)
    {
        /* Reference read of the live overrides, matching the daily purge caller (never held under a lock —
           the reload swaps the whole list atomically). */
        var overrides = _scheduleOverrides;
        Func<string, int> resolver = customRetentionDays is int days
            ? _ => days
            : name => StoreConfigProvider.ResolveFleetRetentionDays(name, overrides);

        var summary = await DarlingRetention.PurgeAsync(
            _postgres!, _timescaleAvailable, _logger, cancellationToken, resolver);

        _logger.LogInformation(
            "purge_now purged {Tables} table(s), {Rows} row(s)/chunk(s){Custom}",
            summary.TablesPurged, summary.TotalPurged,
            customRetentionDays is int cd ? $" (custom retention {cd}d)" : string.Empty);

        var json = JsonSerializer.Serialize(new
        {
            success = true,
            tablesPurged = summary.TablesPurged,
            rowsPurged = summary.TotalPurged,
            rowsDeleted = summary.RowsDeleted,
            chunksDropped = summary.ChunksDropped,
            customRetentionDays,
        });
        return new CommandOutcome(true, "purge complete", json);
    }

    /// <summary>
    /// The worker's <see cref="IDarlingCommandHost"/> adapter (Stage 2): lets the command executor reach the
    /// two imperative commands that need the LIVE loop (snapshot_now / analyze_now) without the executor
    /// holding the worker's mutable loop state. Captures the running server set + collector runner + analysis
    /// pieces created in <see cref="RunCollectionLoopAsync"/>; the worker methods it calls take the
    /// <see cref="_serversLock"/> for the server lookup.
    /// </summary>
    private sealed class WorkerCommandHost : IDarlingCommandHost
    {
        private readonly DarlingWorker _worker;
        private readonly List<ServerLoopState> _servers;
        private readonly DarlingCollectorRunner _runner;
        private readonly PgPlanFetcher _planFetcher;
        private readonly AnalysisNotificationService _notificationService;
        private readonly DarlingConfig _config;

        public WorkerCommandHost(
            DarlingWorker worker, List<ServerLoopState> servers, DarlingCollectorRunner runner,
            PgPlanFetcher planFetcher, AnalysisNotificationService notificationService, DarlingConfig config)
        {
            _worker = worker;
            _servers = servers;
            _runner = runner;
            _planFetcher = planFetcher;
            _notificationService = notificationService;
            _config = config;
        }

        public Task<CommandOutcome> SnapshotNowAsync(int serverId, CancellationToken cancellationToken)
            => _worker.RunSnapshotAsync(_servers, _runner, serverId, cancellationToken);

        public Task<CommandOutcome> AnalyzeNowAsync(int serverId, CancellationToken cancellationToken)
            => _worker.RunAnalyzeNowAsync(_servers, _planFetcher, _notificationService, _config, serverId, cancellationToken);

        public Task<CommandOutcome> PurgeNowAsync(int? customRetentionDays, CancellationToken cancellationToken)
            => _worker.RunPurgeNowAsync(customRetentionDays, cancellationToken);

        public Task<CommandOutcome> FetchPlanAsync(int serverId, PlanFetchRequest request, CancellationToken cancellationToken)
            => _worker.RunFetchPlanAsync(_servers, _planFetcher, serverId, request, cancellationToken);
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
        /* Lookup only — held briefly under the lock (the command loop reconciles the list concurrently),
           then the connection I/O runs outside it. */
        ServerRuntime? runtime;
        lock (_serversLock)
        {
            runtime = servers
                .Select(s => s.Runtime)
                .FirstOrDefault(r => r is not null
                    && string.Equals(r.ServerId.ToString(CultureInfo.InvariantCulture), serverKey, StringComparison.Ordinal));
        }

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
            var runtime = await DarlingServerConnector.ConnectAsync(server.Config, _logger, cancellationToken);
            server.Runtime = runtime;
            /* Capture the id once, while the connection is freshly established and non-null: an on-load
               RunOneAsync below can drop server.Runtime on a mid-collection connection-level failure, so any
               later read of server.Runtime.ServerId (the schedule resolve, the connection edge) would NRE. */
            var serverId = runtime.ServerId;
            _logger.LogInformation("[{Server}] Connected (major {Major}, edition {Edition}, server_id {ServerId})",
                server.Config.DisplayName,
                runtime.Target.SqlMajorVersion,
                runtime.Target.IsAzureSqlDb ? "AzureSqlDb" : runtime.Target.IsAzureManagedInstance ? "ManagedInstance" : "Box",
                serverId);

            /* Stage 4: the offline->online connection edge (Server Restored) — fired HERE, right after the
               connection is established and BEFORE the on-load collectors run, using the captured serverId.
               The connect succeeded regardless of what the on-load collectors do next, so this is the correct
               point to record "online" (and it can't NRE on a Runtime the on-load loop might drop). Fires only
               if the server was previously seen offline; the first-ever connect is a silent baseline (the
               state machine mirrors the Dashboard's skip-first-check). */
            await _selfAlerts!.ApplyConnectionOutcomeAsync(
                serverId, server.Config.DisplayName, online: true, error: null, cancellationToken);

            await DarlingObservability.UpsertServerAsync(_postgres!, runtime, _logger, cancellationToken);

            await DarlingXeSessions.EnsureAllAsync(runtime, _logger, cancellationToken);

            /* On-load config snapshots (effective FrequencyMinutes 0) run once per connect, then every
               scheduled collector becomes immediately due — mirrors Lite's server-open behavior. The
               effective schedule layers config_collector_schedules overrides on CollectorScheduleDefaults;
               a collector disabled by an override is neither run on-load nor scheduled.

               These on-load runs are NOT under the per-server CollectionGate (unlike the scheduled sweep and
               snapshot_now). Safe today: this path only runs while Runtime is null, and a snapshot_now needs
               a non-null Runtime, so the two never overlap for one server. If TryConnect's timing ever changes
               so a connect can race a snapshot, gate this loop too. */
            var now = DateTime.UtcNow;
            foreach (var name in CollectorScheduleDefaults.All.Keys)
            {
                /* Captured serverId, not server.Runtime.ServerId: an earlier on-load RunOneAsync in this loop
                   can null server.Runtime on a connection-level failure, which would otherwise NRE here. */
                var effective = StoreConfigProvider.ResolveSchedule(name, serverId, _scheduleOverrides);
                if (!effective.Enabled)
                {
                    continue;
                }

                if (effective.FrequencyMinutes == 0)
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

            /* Stage 4: the online->offline connection edge (Server Unreachable) — fires once when a
               previously-connected server can no longer be reached; a repeated failed reconnect does NOT
               re-fire (the state machine dedups). server_id is derived from the config since Runtime is null. */
            await _selfAlerts!.ApplyConnectionOutcomeAsync(
                ServerIdHelper.GetDeterministicHashCode(server.Config.StorageName),
                server.Config.DisplayName, online: false, error: ex.Message, cancellationToken);
        }
    }

    private async Task RunDueCollectorsAsync(ServerLoopState server, DarlingCollectorRunner runner, CancellationToken cancellationToken)
    {
        var runtime = server.Runtime;
        if (runtime is null)
        {
            return;
        }

        /* Non-blocking: if an on-demand snapshot_now holds this server's gate, skip its scheduled sweep
           this pass (the due collectors run next sweep) — the main loop NEVER blocks on the gate, so a
           long snapshot cannot starve collection of the OTHER servers. */
        if (!await server.CollectionGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            foreach (var name in CollectorScheduleDefaults.All.Keys)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                /* Effective schedule = config_collector_schedules override layered on the code default.
                   A disabled or on-load-only (freq 0) collector is skipped; the frequency the NextDue stamp
                   advances by is the EFFECTIVE one, so an override takes effect immediately. */
                var effective = StoreConfigProvider.ResolveSchedule(name, runtime.ServerId, _scheduleOverrides);
                if (!effective.Enabled
                    || effective.FrequencyMinutes == 0
                    || !server.NextDue.TryGetValue(name, out var due)
                    || now < due)
                {
                    continue;
                }

                server.NextDue[name] = now.AddMinutes(effective.FrequencyMinutes);
                await RunOneAsync(server, runner, name, cancellationToken);
            }
        }
        finally
        {
            server.CollectionGate.Release();
        }
    }

    /// <summary>
    /// The <c>snapshot_now</c> command handler (Lite's Live Snapshot, control-plane form): runs EVERY
    /// enabled collector for one connected server immediately, bypassing the schedule, and reports the
    /// collectors run + total rows. Serialized against the scheduled sweep by the per-server
    /// <see cref="ServerLoopState.CollectionGate"/> so the two never double-collect. Waits its turn for the
    /// gate (unlike the main loop, which skips) because an explicit operator snapshot should not be dropped.
    /// </summary>
    private async Task<CommandOutcome> RunSnapshotAsync(
        List<ServerLoopState> servers, DarlingCollectorRunner runner, int serverId, CancellationToken cancellationToken)
    {
        ServerLoopState? server;
        lock (_serversLock)
        {
            server = servers.Find(s => ServerIdHelper.GetDeterministicHashCode(s.Config.StorageName) == serverId);
        }

        if (server is null)
        {
            return new CommandOutcome(false, "server not monitored", JsonError($"no monitored server with server_id {serverId}"));
        }

        if (server.Runtime is null)
        {
            return new CommandOutcome(false, "server not connected",
                JsonError($"server '{server.Config.DisplayName}' is not currently connected — snapshot skipped"));
        }

        await server.CollectionGate.WaitAsync(cancellationToken);
        try
        {
            /* Runtime can be dropped by a concurrent connection failure between the check above and here;
               re-read under the gate. */
            var runtime = server.Runtime;
            if (runtime is null)
            {
                return new CommandOutcome(false, "server not connected",
                    JsonError($"server '{server.Config.DisplayName}' disconnected before the snapshot ran"));
            }

            var collectorsRun = 0;
            var totalRows = 0;
            foreach (var name in CollectorScheduleDefaults.All.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();

                /* Honor a disabled-by-override collector (mirrors the on-load/scheduled gates); run every
                   enabled one NOW regardless of frequency or NextDue — that is what "snapshot" means. */
                var effective = StoreConfigProvider.ResolveSchedule(name, runtime.ServerId, _scheduleOverrides);
                if (!effective.Enabled)
                {
                    continue;
                }

                totalRows += await RunOneAsync(server, runner, name, cancellationToken);
                collectorsRun++;
            }

            _logger.LogInformation("[{Server}] snapshot_now ran {Collectors} collector(s), {Rows} row(s)",
                server.Config.DisplayName, collectorsRun, totalRows);
            var json = JsonSerializer.Serialize(new
            {
                success = true,
                server = server.Config.DisplayName,
                collectorsRun,
                rows = totalRows,
            });
            return new CommandOutcome(true, "snapshot complete", json);
        }
        finally
        {
            server.CollectionGate.Release();
        }
    }

    /// <summary>
    /// The <c>fetch_plan</c> command handler (headless-plan live-plan wave): reads an execution plan from one
    /// monitored server's LIVE plan cache and returns the plan XML — the mechanism the viewer uses to fetch a
    /// plan for ANY process in a deadlock graph / blocked-process report (by its sql_handle) or the
    /// currently-cached plan for a query-grid row (by its plan_handle), neither of which the store holds. The
    /// actual cache read is delegated to the SAME <see cref="PgPlanFetcher"/> the analysis pipeline already uses
    /// (it resolves the serverId to the connected runtime's connection string) — the plan_handle path is its
    /// existing <see cref="PgPlanFetcher.FetchPlanXmlAsync"/>, the sql_handle path its
    /// <see cref="PgPlanFetcher.FetchPlanBySqlHandleAsync"/>. Unlike snapshot_now it takes NO collection gate: a
    /// DMV plan read touches no collector state and writes nothing, so it can run concurrently with a scheduled
    /// sweep. The up-front lookup gives a precise "not monitored" / "not connected" outcome (mirroring
    /// RunSnapshotAsync); a plan that has aged out of the cache — or any fetch error the fetcher swallows to null
    /// per its analysis-safe contract — is reported as a clean "not in cache" (the command itself succeeded).
    /// </summary>
    private async Task<CommandOutcome> RunFetchPlanAsync(
        List<ServerLoopState> servers, PgPlanFetcher planFetcher, int serverId,
        PlanFetchRequest request, CancellationToken cancellationToken)
    {
        /* Lookup under the lock (the command loop reconciles the list concurrently); the fetcher re-resolves the
           serverId to the runtime connection string itself, so this only gates the precise not-monitored /
           not-connected outcomes. Held only for the microsecond lookup. */
        ServerLoopState? server;
        bool connected;
        string displayName;
        lock (_serversLock)
        {
            server = servers.Find(s => ServerIdHelper.GetDeterministicHashCode(s.Config.StorageName) == serverId);
            connected = server?.Runtime is not null;
            displayName = server?.Config.DisplayName ?? serverId.ToString(CultureInfo.InvariantCulture);
        }

        if (server is null)
        {
            return new CommandOutcome(false, "server not monitored", JsonError($"no monitored server with server_id {serverId}"));
        }

        if (!connected)
        {
            return new CommandOutcome(false, "server not connected",
                JsonError($"server '{displayName}' is not currently connected — the live plan cache can only be read from a connected server"));
        }

        var planXml = request.UsePlanHandle
            ? await planFetcher.FetchPlanXmlAsync(serverId, request.PlanHandle!)
            : await planFetcher.FetchPlanBySqlHandleAsync(
                serverId, request.DatabaseName, request.SqlHandle!,
                request.StatementStartOffset, request.StatementEndOffset, cancellationToken);

        if (string.IsNullOrEmpty(planXml))
        {
            _logger.LogInformation("[{Server}] fetch_plan: the requested plan is not in the cache", displayName);
            /* Succeeded (the fetch ran) but the plan is gone — the viewer shows a "not in cache" info, distinct
               from a failure. planXml is null so the viewer's parse hits the not-in-cache branch. */
            return new CommandOutcome(true, "not in cache",
                JsonSerializer.Serialize(new { success = true, planXml = (string?)null }));
        }

        _logger.LogInformation("[{Server}] fetch_plan returned a {Length}-char plan", displayName, planXml.Length);
        return new CommandOutcome(true, "plan fetched",
            JsonSerializer.Serialize(new { success = true, planXml }));
    }

    /// <summary>
    /// Runs one collector for a server and logs its outcome to collection_log. Returns the rows written
    /// (0 on skip/permissions/error) so an on-demand snapshot can tally them; the scheduled/on-load callers
    /// simply discard the count.
    /// </summary>
    private async Task<int> RunOneAsync(ServerLoopState server, DarlingCollectorRunner runner, string collectorName, CancellationToken cancellationToken)
    {
        var runtime = server.Runtime;
        if (runtime is null || !s_dispatch.TryGetValue(collectorName, out var run))
        {
            return 0;
        }

        try
        {
            var result = await run(runner, runtime, cancellationToken);
            _logger.LogInformation("  [{Server}] {Collector} => {Rows} rows (sql:{SqlMs}ms, pg:{PgMs}ms)",
                server.Config.DisplayName, collectorName, result.Rows, result.SqlMs, result.StorageMs);

            await DarlingObservability.LogCollectionAsync(
                _postgres!, runtime, collectorName, "SUCCESS", result.Rows, result.SqlMs, result.StorageMs, null, _logger, cancellationToken);
            return result.Rows;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DarlingXeSessionMissingException ex)
        {
            /* The blocking/deadlock XE session is missing and couldn't be created — log a distinct
               SESSION_MISSING status the Stage 4 Capture Down self-alert reads. Non-fatal, like a
               permissions skip: log it and continue the rest of the sweep. RunXeTolerantAsync already
               classified this (wrapping an XE 297/15151/'XE session' into the distinct type), so this
               catch and the SqlException permissions filter below match disjoint types — their relative
               order is not load-bearing; it only needs to precede the general Exception catch (which would
               otherwise mislabel it ERROR). A non-XE 297 arrives as a plain SqlException and correctly
               hits the permissions filter. */
            _logger.LogWarning("  [{Server}] {Collector} => XE session missing (capture down): {Message}",
                server.Config.DisplayName, collectorName, ex.Message);

            await DarlingObservability.LogCollectionAsync(
                _postgres!, runtime, collectorName, "SESSION_MISSING", 0, 0, 0, ex.Message, _logger, cancellationToken);
            return 0;
        }
        catch (SqlException ex) when (ex.Number is 229 or 297 or 300)
        {
            _logger.LogWarning("  [{Server}] {Collector} => insufficient permissions ({Number}): {Message}",
                server.Config.DisplayName, collectorName, ex.Number, ex.Message);

            await DarlingObservability.LogCollectionAsync(
                _postgres!, runtime, collectorName, "PERMISSIONS", 0, 0, 0, ex.Message, _logger, cancellationToken);
            return 0;
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
            return 0;
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
        ["default_trace_events"] = (r, s, ct) => r.RunAsync(DefaultTraceEventsCollector.Instance, s, ct),
    };

    /// <summary>
    /// Signals that a blocking/deadlock XE session is missing or inaccessible so the reader returned no
    /// events. <see cref="RunXeTolerantAsync"/> throws it, <see cref="RunOneAsync"/> catches it and logs a
    /// distinct <c>SESSION_MISSING</c> collection_log status. Before Stage 4 this case swallowed to zero
    /// rows and logged SUCCESS — indistinguishable from a genuinely idle session, so the Capture Down
    /// self-alert had no signal to read.
    /// </summary>
    private sealed class DarlingXeSessionMissingException : Exception
    {
        public DarlingXeSessionMissingException(string message, Exception inner) : base(message, inner) { }
    }

    private static async Task<CollectorRunResult> RunXeTolerantAsync<TRow>(
        ICollectorDefinition<TRow> definition, DarlingCollectorRunner runner, ServerRuntime server, CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync(definition, server, cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == 297 || ex.Number == 15151 || ex.Message.Contains("XE session"))
        {
            /* XE session not found or not accessible. Lite swallows this to zero rows, but a headless
               service (like the Dashboard) must surface it: raise it as a distinct SESSION_MISSING
               collection_log status so the Stage 4 Capture Down self-alert can detect that blocking/
               deadlock capture is non-functional. RunOneAsync catches this and continues the sweep, so
               collection is still tolerant — only the logged status differs from a real zero-row success. */
            throw new DarlingXeSessionMissingException(ex.Message, ex);
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

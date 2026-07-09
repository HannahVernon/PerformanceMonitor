/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Analysis;

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// Optional hosted service exposing Darling's full MCP tool surface over Streamable HTTP — the analysis
/// class (6 tools) plus the plan-analysis tools and the ~60 STORED data-read tools (resource metrics,
/// query performance, blocking/deadlocks, sessions, config history + current-config snapshots, index/object,
/// latch/spinlock/memory-grant/plan-cache/scheduler/jobs, windowed trends, the system_health parse-on-read
/// family, and the fleet-triage alerts + health-overview reads)
/// — the same names Lite and the Dashboard expose, all reading Darling's Postgres store (no live
/// monitored-server hit except <c>analyze_server</c>'s plan fetch). Same transport/hosting model as
/// Lite's <c>McpHostService</c> (ModelContextProtocol.AspNetCore, Kestrel, stateless HTTP,
/// Gemini-compatible tool registration for #1074); both reasons for HTTP-over-stdio (the server outlives
/// any one client and serves concurrent clients) apply MORE to a 24/7 headless service.
///
/// <para><b>Network exposure — off by default, secure by default (darling-network-endpoints, D3):</b>
/// with no <c>mcp.network</c> block the server binds loopback only and is TOKENLESS — byte-for-byte
/// today's local MCP, so existing local clients are unaffected. An opt-in <c>mcp.network</c> block
/// (MANAGED MODE ONLY) binds the specified LAN interface (plus both loopback families) behind two
/// middlewares installed FIRST in the pipeline, before any MCP handler/handshake: an in-app CIDR check on
/// <c>RemoteIpAddress</c> (loopback always allowed, Round-4 #2) and an unconditional constant-time bearer
/// token (NO loopback exemption — the loopback guard). The effective bind is decided by the pure
/// <see cref="ResolveMcpBind"/>; the caller maps its reason to a severity — LogCritical on a missing
/// precondition (token / valid allowFrom CIDR) and LogWarning in BYO mode — and degrades to loopback-only
/// either way. Fail-closed, enforced HERE (the MCP host), NEVER in the all-fatal
/// <see cref="DarlingConfig.Validate"/> (the worker's abort would not stop this host). No TLS on MCP (a
/// self-signed cert breaks real clients; the named MITM control is a TLS reverse proxy in front of the
/// endpoint) — the token travels cleartext on-segment, so own that residual with the reverse proxy. A
/// best-effort, scoped, idempotent firewall rule is added when exposed and removed when not
/// (defense-in-depth; the token + CIDR are the boundary, not the firewall).</para>
///
/// <para>Gated by darling.json's <c>mcp.enabled</c> (default OFF — a headless service should not open a
/// port unless the operator asks); when disabled or when the config cannot load (the worker already logs
/// that as critical), this service stands down without affecting collection. Registered always in
/// Program.cs and self-gating here, because config loading/validation is the worker's job and Program.cs
/// stays config-free.</para>
///
/// <para>The MCP surface gets its OWN <see cref="NpgsqlDataSource"/> over the store, connecting as the
/// dedicated least-privilege <c>mcp</c> role (D3-role) — NOT the superuser owner — so a token-holder (or a
/// future/buggy tool) reaches only the viewer read surface plus the <c>analysis_findings</c> /
/// <c>analysis_muted</c> INSERTs the tools persist, never the <c>config_command</c> service-credential
/// pivot or the carved secret columns. It also gets its own <see cref="DarlingAnalysisService"/>. Store
/// migration + role provisioning are the WORKER's job; the <c>mcp</c>-role credential is written AFTER
/// migration (later than the owner's), so the first-boot poll budget tolerates the delay. The plan fetcher
/// resolves a finding's serverId to a live connection string built from darling.json (DPAPI resolution
/// lazy per fetch; any resolution/connection failure degrades the fetch to null inside
/// <see cref="PgPlanFetcher"/>). On a brand-new store, tool calls before the first migration/connect
/// simply return their error/miss envelopes.</para>
/// </summary>
public sealed class DarlingMcpHostService : BackgroundService
{
    private readonly ILogger<DarlingMcpHostService> _logger;
    private WebApplication? _app;

    public DarlingMcpHostService(ILogger<DarlingMcpHostService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DarlingConfig config;
        try
        {
            config = DarlingConfig.Load();
        }
        catch (Exception ex)
        {
            /* The worker logs the missing/broken config as critical; the MCP host just stands down. */
            _logger.LogDebug("MCP server not started (configuration unavailable): {Message}", ex.Message);
            return;
        }

        if (!config.Mcp.Enabled)
        {
            _logger.LogDebug("MCP server disabled (mcp.enabled = false)");
            return;
        }

        /* Decide the effective bind PURELY, then map the reason -> severity here (Round-4 #7: the caller,
           not the pure fn, chooses LogCritical vs LogWarning; tests assert (Mode, Reason) without a logger). */
        var bind = ResolveMcpBind(config.Mcp, config.Postgres.Managed);
        LogBindReason(config.Mcp, bind.Reason);

        try
        {
            var networkMode = bind.Mode == McpBindMode.NetworkAndLoopback;

            /* In network mode ResolveMcpBind has already validated the listen IP, the allowFrom CIDR, AND their
               address-family agreement, so these two parses cannot throw; only resolving the token can still
               fail (a corrupt DPAPI blob), which fail-closes to loopback-only rather than exposing tokenless. */
            IPAddress? networkListenIp = null;
            IPNetwork allowedCidr = default;
            string bearerToken = "";
            if (networkMode)
            {
                networkListenIp = IPAddress.Parse(config.Mcp.Network!.Listen!.Trim());
                allowedCidr = IPNetwork.Parse(config.Mcp.Network.AllowFrom!.Trim());

                try
                {
                    var token = config.Mcp.Network.ResolveToken(out var usedPlaintext);
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        _logger.LogCritical(
                            "MCP network token resolved to empty after decryption — refusing to expose; binding loopback-only.");
                        networkMode = false;
                    }
                    else
                    {
                        bearerToken = token;
                        if (usedPlaintext)
                        {
                            _logger.LogWarning(
                                "mcp.network.token is set in plaintext (dev convenience) — prefer mcp.network.encryptedToken " +
                                "(produced by --encrypt-password). This token gates ALL MCP network access.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(
                        "MCP network token could not be decrypted ({Message}) — refusing to expose; binding loopback-only.",
                        ex.Message);
                    networkMode = false;
                }
            }

            /* The REAL primary bind address (network IP when exposed, else loopback): both the port precheck
               and the Kestrel bind use it, so the precheck probes the actual address, not always loopback. */
            var primaryBind = networkMode ? networkListenIp! : IPAddress.Loopback;

            /* Port-in-use pre-check — Lite's StartMcpServerAsync guard, via the shared utility, against the
               REAL bind address (D3-e: not always IPAddress.Loopback). Done before the firewall reconcile so a
               bail here leaves the firewall untouched. */
            if (await PortUtilityService.IsTcpPortListeningAsync(config.Mcp.Port, primaryBind, stoppingToken))
            {
                _logger.LogError("Port {Port} is already in use — MCP server not started", config.Mcp.Port);
                return;
            }

            /* Firewall reconcile (managed mode only; best-effort, never fatal). Symmetric like the store's D1
               reconcile: ensure the scoped rule when exposed, remove it when not (so disabling the config
               closes the box). The token + in-app CIDR are the boundary; the firewall is defense-in-depth. */
            if (config.Postgres.Managed && OperatingSystem.IsWindows())
            {
                await ReconcileMcpFirewallAsync(
                    config.Mcp.Port, networkMode, networkMode ? allowedCidr.ToString() : null, stoppingToken);
            }

            /* Managed mode: the WORKER owns the bundled server's lifecycle; the MCP host only derives the
               least-privilege mcp-role connection string from the stored DPAPI credential (D3-role). */
            string? storeConnectionString;
            if (config.Postgres.Managed)
            {
                if (!OperatingSystem.IsWindows())
                {
                    _logger.LogError("MCP server not started: postgres.managed = true requires Windows");
                    return;
                }

                storeConnectionString = await WaitForManagedConnectionStringAsync(config.Postgres, stoppingToken);
                if (storeConnectionString is null)
                {
                    return;
                }
            }
            else
            {
                storeConnectionString = config.Postgres.ConnectionString;
            }

            await using var postgres = NpgsqlDataSource.Create(storeConnectionString);

            /* serverId → connection string from config (first entry wins on a duplicate storage
               name, mirroring the worker's FirstOrDefault over runtimes). Resolution is lazy so
               DPAPI decrypt runs only when a plan fetch actually needs the connection. */
            var serversById = new Dictionary<int, MonitoredServer>();
            foreach (var server in config.Servers)
            {
                serversById.TryAdd(ServerIdHelper.GetDeterministicHashCode(server.StorageName), server);
            }

            var planFetcher = new PgPlanFetcher(
                serverId => serversById.TryGetValue(serverId, out var server)
                    ? DarlingServerConnector.ResolveConnectionString(server, _logger)
                    : null,
                _logger);

            var builder = WebApplication.CreateBuilder();

            builder.WebHost.ConfigureKestrel(options =>
            {
                if (networkMode)
                {
                    /* Bind the specific family (not ListenAnyIP), then ALSO both loopback families so a local
                       client resolving "localhost" -> ::1 still works — skipping the loopback Listen(s) when the
                       listen value is itself loopback or a wildcard (0.0.0.0/::), which would collide on the port. */
                    options.Listen(primaryBind, config.Mcp.Port);
                    if (ShouldAddLoopbackListeners(primaryBind))
                    {
                        options.Listen(IPAddress.Loopback, config.Mcp.Port);
                        options.Listen(IPAddress.IPv6Loopback, config.Mcp.Port);
                    }
                }
                else
                {
                    /* The default/degraded loopback-only server — byte-for-byte today's bind (both families). */
                    options.ListenLocalhost(config.Mcp.Port);
                }
            });

            /* Suppress ASP.NET Core console logging — the service's own logger reports lifecycle. */
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            /* Register services that MCP tools need via dependency injection. */
            builder.Services.AddSingleton<NpgsqlDataSource>(postgres);
            builder.Services.AddSingleton(new DarlingAnalysisService(postgres, planFetcher, _logger));

            /* Register MCP server with the analysis tool class. */
            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new()
                    {
                        Name = "PerformanceMonitorDarling",
                        Version = "1.0.0"
                    };
                    options.ServerInstructions = DarlingMcpInstructions.Text;
                })
                /* Stateless mode: each request is self-contained (no Mcp-Session-Id round-trip).
                   Required for clients like Google Antigravity that don't echo the session id,
                   which otherwise connect but list zero tools (issue #1074). */
                .WithHttpTransport(options => options.Stateless = true)
                /* WithGeminiCompatibleTools (not the SDK's WithTools) rewrites parameter schemas into
                   the subset Gemini/Antigravity accepts — collapsing nullable type unions and
                   dropping the default keyword. The companion to stateless transport for issue #1074. */
                .WithGeminiCompatibleTools<DarlingMcpTools>()
                /* The five plan-analysis tools (analyze_query_plan / analyze_procedure_plan /
                   analyze_query_store_plan / analyze_plan_xml / get_plan_xml) — the same names the
                   Dashboard and Lite expose, fetching the collectors' STORED plan XML from Postgres
                   (no live monitored-server hit) and running the SHARED PlanAnalysis engine. */
                .WithGeminiCompatibleTools<DarlingMcpPlanTools>()
                /* The core data-read tools (resource metrics, query performance, discovery/health —
                   get_cpu_utilization / get_wait_stats / get_wait_trend / get_memory_stats /
                   get_memory_clerks / get_file_io_stats / get_tempdb_trend / get_perfmon_stats /
                   get_top_queries_by_cpu / get_top_procedures_by_cpu / get_query_store_top /
                   list_servers / get_collection_health / get_server_properties), the same names Lite
                   and the Dashboard expose, over Darling's Postgres store (STORED reads, no live hit).
                   These are the tools the analysis findings' next_tools recommendations point at. */
                .WithGeminiCompatibleTools<DarlingMcpDataTools>()
                /* The diagnostic-depth data-read tools (blocking/deadlocks, sessions, config-history,
                   index/object) — get_blocking / get_deadlocks / get_deadlock_detail /
                   get_blocked_process_xml, get_session_stats / get_active_queries / get_waiting_tasks,
                   get_server_config_changes / get_database_config_changes / get_trace_flag_changes /
                   get_database_scoped_config, get_table_index_sizes / get_index_usage / get_object_locking /
                   get_database_sizes — the same names Lite and the Dashboard expose, over Darling's Postgres
                   store (STORED reads, no live hit). Result shapes follow Lite where the two SKUs diverge. */
                .WithGeminiCompatibleTools<DarlingMcpBlockingTools>()
                .WithGeminiCompatibleTools<DarlingMcpSessionTools>()
                .WithGeminiCompatibleTools<DarlingMcpConfigHistoryTools>()
                .WithGeminiCompatibleTools<DarlingMcpObjectStatsTools>()
                /* The resource-contention + jobs data-read tools — get_latch_stats / get_spinlock_stats,
                   get_resource_semaphore / get_memory_grants, get_plan_cache_bloat / get_cpu_scheduler_pressure,
                   get_running_jobs — the same names Lite and the Dashboard expose, over Darling's Postgres store
                   (STORED reads of the collected latch/spinlock/memory-grant/plan-cache/cpu-scheduler/running-job
                   snapshots, no live hit). The Dashboard-only CASE enrichment (latch severity/description/
                   recommendation, spinlock description) and the #1410 client-side classifications (plan-cache
                   bloat_level, cpu-scheduler pressure_level) are reproduced service-side so the full result shape
                   is served; Darling's delta collectors store no sample_interval_seconds, so per-second rates are
                   derived from the LAG interval. */
                .WithGeminiCompatibleTools<DarlingMcpLatchSpinlockTools>()
                .WithGeminiCompatibleTools<DarlingMcpMemoryGrantTools>()
                .WithGeminiCompatibleTools<DarlingMcpPlanCacheSchedulerTools>()
                .WithGeminiCompatibleTools<DarlingMcpJobTools>()
                /* The windowed-trend siblings of the core data-read tools — get_memory_trend /
                   get_perfmon_trend / get_file_io_trend / get_query_trend / get_query_duration_trend — the
                   same names Lite and the Dashboard expose, over Darling's Postgres store (STORED reads of
                   the collected memory / perfmon / file-io / query-stats series, no live hit). Each mirrors
                   the viewer's proven chart read; the shape follows Lite where the SKUs diverge. */
                .WithGeminiCompatibleTools<DarlingMcpTrendTools>()
                /* The fleet-triage quick-win reads the fleet edition previously lacked — the alerts family
                   (get_alert_history over config_alert_log, get_alert_settings over config_alert_settings,
                   get_mute_rules via the service-side PgMuteRuleStore), the CURRENT-config snapshot trio
                   (get_server_config / get_database_config / get_trace_flags — latest capture, the companion to
                   the *_changes diff tools), and the health overview (get_server_summary + the daily rollup
                   get_daily_summary, folded through the shared DailyHealthBandCalculator). Same names Lite and
                   the Dashboard expose, all STORED reads over Darling's Postgres store (no live hit). The
                   blocking-trend / deadlock-trend, memory-pressure-event, and wait-type siblings ride along on
                   the existing blocking / memory-grant / core data-read classes above. */
                .WithGeminiCompatibleTools<DarlingMcpAlertTools>()
                .WithGeminiCompatibleTools<DarlingMcpConfigTools>()
                .WithGeminiCompatibleTools<DarlingMcpHealthTools>()
                /* The system_health parse-on-read family — get_health_parser_cpu_tasks / _io_issues /
                   _memory_broker / _memory_conditions / _memory_node_oom / _scheduler_issues /
                   _severe_errors / _system_health — the same names the Dashboard exposes. Where the Dashboard
                   reads its server-side-parsed collect.HealthParser_* tables, these shred the raw
                   system_health_events on read via the shared SystemHealthParser (Common) and gate with the
                   service-side twin of the viewer's SystemEventSignificance, exactly as the viewer's System
                   Events tab does — the same SIGNIFICANT warning set, no live hit. */
                .WithGeminiCompatibleTools<DarlingMcpHealthParserTools>()
                /* The Default Trace tool — get_default_trace_events — the same name the Dashboard exposes.
                   Reads Darling's collected default_trace_events (the base table, no v_* view — like
                   server_properties) and returns the SIGNIFICANT set via the shared
                   DefaultTraceEventSignificance, the same significant-set gate the viewer's System Events
                   surface uses; config-change events are excluded (the config-snapshot diff tools own them). */
                .WithGeminiCompatibleTools<DarlingMcpDefaultTraceTools>();

            _app = builder.Build();

            /* Access-control middleware — installed ONLY in network mode (Round-4 #6). The default/degraded
               loopback-only server stays byte-for-byte today's tokenless local MCP, so existing local clients
               keep working. Both run BEFORE MapMcp (D3-b: "first ... before any handler/handshake"): the
               unconditional constant-time bearer token FIRST (NO loopback exemption — in exposed mode even a
               local client must present the token; that IS the loopback guard against SSRF/sandboxed sockets),
               then the in-app CIDR check (loopback-exempt so the loopback bind's local clients are not 403'd,
               Round-4 #2 — it bounds WHO can route to the port, independent of the best-effort firewall). */
            if (networkMode)
            {
                var cidr = allowedCidr;
                var token = bearerToken;

                _app.Use(async (context, next) =>
                {
                    if (!IsBearerTokenAuthorized(context.Request.Headers.Authorization.ToString(), token))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        return;
                    }

                    await next(context);
                });

                _app.Use(async (context, next) =>
                {
                    if (!IsRemoteAddressAllowed(context.Connection.RemoteIpAddress, cidr))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }

                    await next(context);
                });
            }

            _app.MapMcp();

            if (networkMode)
            {
                _logger.LogInformation(
                    "Starting MCP server on http://{Listen}:{Port} (LAN-exposed to {Cidr} behind a bearer token + in-app CIDR; loopback also bound)",
                    primaryBind, config.Mcp.Port, allowedCidr);
            }
            else
            {
                _logger.LogInformation("Starting MCP server on http://localhost:{Port} (loopback only)", config.Mcp.Port);
            }

            await _app.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            /* Normal shutdown */
        }
        catch (Exception ex)
        {
            _logger.LogError("MCP server failed: {Message}", ex.Message);
        }
    }

    /* ---------------------------------------------------------------------------------------------------
       Pure decision functions (darling-network-endpoints, D3). Factored out of the Kestrel/middleware
       wiring so they are unit-testable without a running server or a logger; the caller maps the reason to
       a log severity and installs the middleware only in network mode.
       --------------------------------------------------------------------------------------------------- */

    /// <summary>The effective MCP bind. <see cref="McpBindMode.LoopbackOnly"/> is the secure default;
    /// <see cref="McpBindMode.NetworkAndLoopback"/> binds the LAN interface behind the token + CIDR.</summary>
    internal enum McpBindMode
    {
        LoopbackOnly,
        NetworkAndLoopback,
    }

    /// <summary>WHY the bind resolved as it did — the caller maps this to a severity (Round-4 #7):
    /// <see cref="LoopbackByDefault"/>/<see cref="NetworkExposed"/> are non-degrade (no critical log),
    /// <see cref="TokenMissing"/>/<see cref="AllowFromInvalid"/> are fail-closed degrades (LogCritical),
    /// and <see cref="ManagedModeRequired"/> is the BYO "ignored" notice (LogWarning, D-BYO).</summary>
    internal enum McpBindReason
    {
        /// <summary>No network block, or a loopback/absent listen — the byte-for-byte-today loopback server.</summary>
        LoopbackByDefault,

        /// <summary>All preconditions met: non-loopback listen + managed + token present + valid allowFrom CIDR.</summary>
        NetworkExposed,

        /// <summary>network.* is set but postgres.managed = false — network exposure is managed-mode only (D-BYO warning).</summary>
        ManagedModeRequired,

        /// <summary>Exposed + managed but the listen value is not a parseable IP (localhost/hostname/"*") — fail-closed to loopback (LogCritical).</summary>
        ListenInvalid,

        /// <summary>Exposed + managed but no bearer token — fail-closed to loopback (LogCritical).</summary>
        TokenMissing,

        /// <summary>Exposed + managed + token but allowFrom is missing/not a valid CIDR or its family does not match the listen — fail-closed to loopback (LogCritical).</summary>
        AllowFromInvalid,
    }

    /// <summary>The (mode, reason) pair returned by <see cref="ResolveMcpBind"/>.</summary>
    internal readonly record struct McpBindDecision(McpBindMode Mode, McpBindReason Reason);

    /// <summary>
    /// PURE resolution of the effective MCP bind (D3-a). Returns <see cref="McpBindMode.NetworkAndLoopback"/>
    /// ONLY when the listen value is a genuine network (non-loopback) address (via the Phase-1 classifier —
    /// so <c>127.0.0.1</c> resolves to loopback, never a network bind/collision) that ALSO parses as an IP, AND
    /// <paramref name="managed"/> is true, AND a bearer token is present (encryptedToken or token — presence
    /// only; the host decrypts later), AND allowFrom is a valid CIDR of the SAME address family as the listen.
    /// Otherwise loopback-only with the specific reason: BYO (<paramref name="managed"/> = false) with any
    /// network.* set -&gt; <see cref="McpBindReason.ManagedModeRequired"/> (the network path never runs in BYO,
    /// D-BYO); exposed + managed but a non-IP listen -&gt; <see cref="McpBindReason.ListenInvalid"/>; exposed +
    /// managed + IP but no token -&gt; <see cref="McpBindReason.TokenMissing"/>; exposed + managed + token but an
    /// invalid/family-mismatched allowFrom -&gt; <see cref="McpBindReason.AllowFromInvalid"/>; anything else (not
    /// exposed) -&gt; <see cref="McpBindReason.LoopbackByDefault"/>. Never throws; never consults a logger.
    /// </summary>
    internal static McpBindDecision ResolveMcpBind(McpConfig mcp, bool managed)
    {
        var network = mcp.Network;
        var exposed = network is not null && DarlingNetwork.IsExposedListenAddress(network.Listen);

        if (!exposed)
        {
            /* Not exposed = the secure default. The one exception worth a word: a BYO store with a network
               block set at all (even a loopback/partial one) is ignored -> the D-BYO warning. */
            if (!managed && network is { IsConfigured: true })
            {
                return new McpBindDecision(McpBindMode.LoopbackOnly, McpBindReason.ManagedModeRequired);
            }

            return new McpBindDecision(McpBindMode.LoopbackOnly, McpBindReason.LoopbackByDefault);
        }

        /* Exposed. Managed-mode only: BYO never binds the network path (D3-a / D-BYO), and this dominates a
           missing/invalid listen/token/allowFrom so the operator sees the actionable "managed only" notice first. */
        if (!managed)
        {
            return new McpBindDecision(McpBindMode.LoopbackOnly, McpBindReason.ManagedModeRequired);
        }

        /* The listen must be a parseable IP. The classifier treats a non-IP value (localhost, a hostname, "*")
           as "exposed" so it is never silently bound; here it degrades to loopback rather than throwing when the
           host later does IPAddress.Parse (D-validate — the store degrades on the same input, the host must too). */
        if (!IPAddress.TryParse(network!.Listen!.Trim(), out var listenIp))
        {
            return new McpBindDecision(McpBindMode.LoopbackOnly, McpBindReason.ListenInvalid);
        }

        /* Token presence only (no decryption here — that is an effectful, Windows-only step the host does). */
        if (string.IsNullOrWhiteSpace(network.EncryptedToken) && string.IsNullOrWhiteSpace(network.Token))
        {
            return new McpBindDecision(McpBindMode.LoopbackOnly, McpBindReason.TokenMissing);
        }

        /* allowFrom must be a valid CIDR (host bits zeroed, the same IPNetwork rule the store side uses) whose
           address family matches the listen (the store's D4 check): a mismatched family would bind one family
           while the in-app CIDR check rejects the other, 403-ing every network client — fail-closed but silently
           non-functional, so degrade with a clear reason instead. */
        if (string.IsNullOrWhiteSpace(network.AllowFrom) || !IPNetwork.TryParse(network.AllowFrom.Trim(), out var cidr))
        {
            return new McpBindDecision(McpBindMode.LoopbackOnly, McpBindReason.AllowFromInvalid);
        }

        if (cidr.BaseAddress.AddressFamily != listenIp.AddressFamily)
        {
            return new McpBindDecision(McpBindMode.LoopbackOnly, McpBindReason.AllowFromInvalid);
        }

        return new McpBindDecision(McpBindMode.NetworkAndLoopback, McpBindReason.NetworkExposed);
    }

    /// <summary>
    /// Whether to ALSO bind the two loopback families beside the network listener (D3-e). Skipped when the
    /// listen value is itself a loopback address (already covered) or a wildcard (<c>0.0.0.0</c> covers IPv4
    /// loopback, <c>::</c> the IPv6) — binding an explicit loopback on the same port then would collide
    /// (WSAEADDRINUSE). For a specific LAN IP the loopback binds are added so a local client resolving
    /// "localhost" still reaches the server (which, in network mode, now also requires the token).
    /// </summary>
    internal static bool ShouldAddLoopbackListeners(IPAddress listenIp)
        => !(IPAddress.IsLoopback(listenIp)
             || listenIp.Equals(IPAddress.Any)
             || listenIp.Equals(IPAddress.IPv6Any));

    /// <summary>
    /// PURE in-app CIDR check (D3-c, Round-4 #2): is <paramref name="remoteIp"/> allowed? Loopback
    /// (<c>127.0.0.0/8</c> or <c>::1</c>, incl. an IPv4-mapped-IPv6 form) is ALWAYS allowed — it is not in
    /// <paramref name="allowedCidr"/>, so otherwise the loopback bind's local clients would get 403. Everything
    /// else must fall inside the CIDR. A null remote (unverifiable origin) fails closed.
    /// </summary>
    internal static bool IsRemoteAddressAllowed(IPAddress? remoteIp, IPNetwork allowedCidr)
    {
        if (remoteIp is null)
        {
            return false;
        }

        var ip = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
        return IPAddress.IsLoopback(ip) || allowedCidr.Contains(ip);
    }

    /// <summary>
    /// PURE bearer-token check (D3-b): true only when <paramref name="authorizationHeaderValue"/> carries a
    /// <c>Bearer</c> token that matches <paramref name="expectedToken"/>. The compare is constant-time over
    /// SHA-256 digests, so it leaks neither the token nor its length; empty/missing/mismatch all return false,
    /// and an empty <paramref name="expectedToken"/> never authorizes. Has NO notion of the remote address —
    /// so there is structurally no loopback exemption (the loopback guard).
    /// </summary>
    internal static bool IsBearerTokenAuthorized(string? authorizationHeaderValue, string expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken))
        {
            return false;
        }

        var presented = ExtractBearerToken(authorizationHeaderValue);
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        /* Hash both to a fixed 32 bytes so FixedTimeEquals is constant-time regardless of token length. */
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken));
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(expectedHash, presentedHash);
    }

    /// <summary>Extracts the token from a <c>Bearer &lt;token&gt;</c> Authorization header (scheme
    /// case-insensitive); null when absent, malformed, or the token part is blank. PURE.</summary>
    internal static string? ExtractBearerToken(string? authorizationHeaderValue)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeaderValue))
        {
            return null;
        }

        const string prefix = "Bearer ";
        var value = authorizationHeaderValue.Trim();
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = value.Substring(prefix.Length).Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>
    /// PURE severity map for a <see cref="ResolveMcpBind"/> reason (Round-4 #7): the fail-closed degrades
    /// (<see cref="McpBindReason.ListenInvalid"/>/<see cref="McpBindReason.TokenMissing"/>/
    /// <see cref="McpBindReason.AllowFromInvalid"/>) are <see cref="LogLevel.Critical"/>, the BYO "ignored"
    /// notice (<see cref="McpBindReason.ManagedModeRequired"/>) is <see cref="LogLevel.Warning"/>, and the
    /// non-degrade reasons (<see cref="McpBindReason.NetworkExposed"/>/<see cref="McpBindReason.LoopbackByDefault"/>)
    /// are silent (null). <see cref="LogBindReason"/> drives its emit level off this, so the level and the
    /// message can never diverge.
    /// </summary>
    internal static LogLevel? MapBindReasonSeverity(McpBindReason reason) => reason switch
    {
        McpBindReason.ListenInvalid => LogLevel.Critical,
        McpBindReason.TokenMissing => LogLevel.Critical,
        McpBindReason.AllowFromInvalid => LogLevel.Critical,
        McpBindReason.ManagedModeRequired => LogLevel.Warning,
        _ => null,
    };

    /// <summary>Emits the <see cref="ResolveMcpBind"/> reason at its mapped severity (Round-4 #7). Silent for
    /// the non-degrade reasons (the network-exposed line is logged at start with the real bind).</summary>
    private void LogBindReason(McpConfig mcp, McpBindReason reason)
    {
        var level = MapBindReasonSeverity(reason);
        if (level is null)
        {
            /* NetworkExposed is announced at start with the real address; LoopbackByDefault is the silent,
               byte-for-byte-today path. */
            return;
        }

        switch (reason)
        {
            case McpBindReason.ListenInvalid:
                _logger.Log(level.Value,
                    "MCP network exposure requested but mcp.network.listen '{Listen}' is not a valid IP address — " +
                    "refusing to expose; binding loopback-only. Use a specific IP (e.g. 192.168.1.205), or 0.0.0.0 for all interfaces.",
                    mcp.Network?.Listen);
                break;

            case McpBindReason.TokenMissing:
                _logger.Log(level.Value,
                    "MCP network exposure requested (mcp.network.listen is non-loopback) but no bearer token is set — " +
                    "refusing to expose; binding loopback-only. Set mcp.network.encryptedToken (via --encrypt-password) or mcp.network.token.");
                break;

            case McpBindReason.AllowFromInvalid:
                _logger.Log(level.Value,
                    "MCP network exposure requested but mcp.network.allowFrom '{AllowFrom}' is not a valid CIDR or its " +
                    "address family does not match mcp.network.listen — refusing to expose; binding loopback-only. " +
                    "Use e.g. 192.168.1.0/24 (host bits zeroed, same family as listen).",
                    mcp.Network?.AllowFrom);
                break;

            case McpBindReason.ManagedModeRequired:
                _logger.Log(level.Value,
                    "mcp.network.* is set but postgres.managed = false — MCP network exposure is managed-mode only and is " +
                    "ignored; your own PostgreSQL/reverse proxy governs BYO exposure. Binding loopback-only.");
                break;

            default:
                break;
        }
    }

    /* ---------------------------------------------------------------------------------------------------
       Best-effort MCP firewall reconcile (D1, defense-in-depth). Reuses the store's tested, pure command
       builders (DarlingManagedPostgres.BuildFirewall*Command) for the exact scoped rule shape AND its shared
       PowerShell runner (RunPowerShellAsync) — no duplication, no timeout divergence. Never fatal.
       --------------------------------------------------------------------------------------------------- */

    /// <summary>The scoped MCP firewall rule name (idempotent by DisplayName), port-specific and distinct
    /// from the store's rule so the two endpoints reconcile independently.</summary>
    private static string McpFirewallRuleName(int port) => $"PerformanceMonitor Darling MCP (port {port})";

    [SupportedOSPlatform("windows")]
    private async Task ReconcileMcpFirewallAsync(int port, bool enable, string? cidr, CancellationToken cancellationToken)
    {
        var ruleName = McpFirewallRuleName(port);
        var command = enable
            ? DarlingManagedPostgres.BuildFirewallEnableCommand(ruleName, port, cidr!)
            : DarlingManagedPostgres.BuildFirewallDisableCommand(ruleName);

        try
        {
            var (exitCode, output) = await DarlingManagedPostgres.RunPowerShellAsync(command, cancellationToken);
            if (exitCode == 0)
            {
                _logger.LogInformation("MCP firewall rule '{Rule}' {Verb}.", ruleName, enable ? "ensured" : "removed");
            }
            else if (enable)
            {
                _logger.LogWarning(
                    "Could not configure the MCP firewall automatically (exit {ExitCode}: {Output}). Run this in an elevated PowerShell:\n{Command}",
                    exitCode, output, command);
            }
            else
            {
                _logger.LogWarning("Could not remove the MCP firewall rule automatically (exit {ExitCode}: {Output}).", exitCode, output);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (enable)
            {
                _logger.LogWarning(
                    "Could not configure the MCP firewall automatically ({Message}). Run this in an elevated PowerShell:\n{Command}",
                    ex.Message, command);
            }
            else
            {
                _logger.LogWarning("Could not remove the MCP firewall rule automatically ({Message}).", ex.Message);
            }
        }
    }

    /// <summary>
    /// Managed mode's first-boot race, handled: the dedicated <c>mcp</c>-role credential appears only after
    /// the worker's initdb + migration + role provisioning finish (LATER than the owner credential), so poll
    /// up to ~5 minutes (60 × 5s) instead of racing it, then stand down with a pointer at the worker log —
    /// fail-closed (MCP just does not start; it self-heals on the next restart once the credential exists).
    /// The 5-minute budget tolerates a cold first boot (unpack + initdb + start + migrate + provision) that
    /// can exceed the owner credential's shorter window (Round-4 #8).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task<string?> WaitForManagedConnectionStringAsync(PostgresConfig config, CancellationToken stoppingToken)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var connectionString = DarlingManagedPostgres.TryBuildMcpConnectionStringFromStoredCredential(config);
            if (connectionString is not null)
            {
                return connectionString;
            }

            if (attempt == 0)
            {
                _logger.LogInformation("Waiting for the managed Postgres mcp-role credential (first-run initialization) before starting the MCP server");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        _logger.LogError("MCP server not started: the managed Postgres mcp-role credential never appeared — see the worker log for the bootstrap failure");
        return null;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app != null)
        {
            _logger.LogInformation("Stopping MCP server");
            await _app.StopAsync(cancellationToken);
            await _app.DisposeAsync();
            _app = null;
        }

        await base.StopAsync(cancellationToken);
    }
}

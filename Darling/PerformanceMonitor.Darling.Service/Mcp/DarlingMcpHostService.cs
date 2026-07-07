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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Analysis;

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// Optional hosted service exposing the six analysis MCP tools over Streamable HTTP — the
/// SAME transport/hosting model as Lite's <c>McpHostService</c> (ModelContextProtocol.AspNetCore,
/// Kestrel on localhost:{port}, stateless HTTP, Gemini-compatible tool registration for #1074).
/// Lite hosts HTTP rather than stdio because the server outlives any one client process and
/// serves concurrent clients; both reasons apply MORE to a 24/7 headless service, so the
/// mechanism carries over unchanged. Gated by darling.json's <c>mcp.enabled</c> (default OFF —
/// a headless service should not open a local port unless the operator asks); when disabled or
/// when the config cannot load (the worker already logs that as critical), this service stands
/// down without affecting collection. Registered always in Program.cs and self-gating here,
/// because config loading/validation is the worker's job and Program.cs stays config-free.
///
/// <para>
/// The MCP surface gets its OWN <see cref="NpgsqlDataSource"/> (a second pool over the same
/// store; the worker's is method-scoped) and its own <see cref="DarlingAnalysisService"/> —
/// Lite's host constructs a dedicated AnalysisService for MCP the same way. The plan fetcher
/// resolves a finding's serverId to a connection string built from darling.json (the config
/// twin of the worker's runtime-list resolver): DPAPI resolution happens lazily per fetch, and
/// any resolution/connection failure degrades the fetch to null inside
/// <see cref="PgPlanFetcher"/>. Store migration is the WORKER's job — on a brand-new store,
/// tool calls before the first migration/connect simply return their error/miss envelopes.
/// </para>
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

        try
        {
            /* Port-in-use pre-check — Lite's StartMcpServerAsync guard, via the shared utility. */
            if (await PortUtilityService.IsTcpPortListeningAsync(config.Mcp.Port, IPAddress.Loopback, stoppingToken))
            {
                _logger.LogError("Port {Port} is already in use — MCP server not started", config.Mcp.Port);
                return;
            }

            /* Managed mode: the WORKER owns the bundled server's lifecycle; the MCP host only
               derives the same connection string from the stored DPAPI credential. */
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
                options.ListenLocalhost(config.Mcp.Port);
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
                .WithGeminiCompatibleTools<DarlingMcpPlanTools>();

            _app = builder.Build();
            _app.MapMcp();

            _logger.LogInformation("Starting MCP server on http://localhost:{Port}", config.Mcp.Port);

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

    /// <summary>
    /// Managed mode's first-boot race, handled: the credential file appears only after the
    /// worker's initdb finishes, so poll briefly (2 minutes) instead of racing it, then stand
    /// down with a pointer at the worker log — the worker will already have logged any
    /// bootstrap failure as critical.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task<string?> WaitForManagedConnectionStringAsync(PostgresConfig config, CancellationToken stoppingToken)
    {
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var connectionString = DarlingManagedPostgres.TryBuildConnectionStringFromStoredCredential(config);
            if (connectionString is not null)
            {
                return connectionString;
            }

            if (attempt == 0)
            {
                _logger.LogInformation("Waiting for the managed Postgres credential (first-run initialization) before starting the MCP server");
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

        _logger.LogError("MCP server not started: the managed Postgres credential never appeared — see the worker log for the bootstrap failure");
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

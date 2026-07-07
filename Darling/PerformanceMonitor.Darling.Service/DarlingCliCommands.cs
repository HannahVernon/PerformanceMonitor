/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// One-shot CLI verbs the service exe supports alongside the Windows-service host — currently the
/// <c>--test-connection</c> / <c>--validate-config</c> pre-flight (Stage 2). It loads darling.json,
/// validates its shape, and probes EVERY configured server for reachability + permissions, reusing the SAME
/// <see cref="DarlingServerConnector.ProbeAsync"/> path the <c>test_connect</c> command runs — so a config
/// that validates from the CLI connects identically under the running service. Pure output formatting
/// (<see cref="FormatProbeLine"/>) is split out so it is unit-testable without live SQL.
/// </summary>
public static class DarlingCliCommands
{
    /// <summary>The verb aliases handled by <see cref="TryGetValidateConfigVerb"/>.</summary>
    public static bool IsValidateConfigVerb(string arg) =>
        string.Equals(arg, "--test-connection", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--validate-config", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads + validates darling.json, then probes every server. Prints one PASS/FAIL line per server and a
    /// summary. Returns 0 only when the config is valid AND every server is reachable; 1 otherwise (so it is
    /// usable as a deployment gate). Store/collection are never touched — this is a pure config pre-flight.
    /// </summary>
    public static async Task<int> ValidateConfigAsync(
        string? configPath, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        DarlingConfig config;
        try
        {
            config = DarlingConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not load configuration: {ex.Message}");
            return 1;
        }

        var problems = config.Validate();
        if (problems.Count > 0)
        {
            error.WriteLine("Configuration is invalid:");
            foreach (var problem in problems)
            {
                error.WriteLine("  - " + problem);
            }

            return 1;
        }

        output.WriteLine($"Validating connectivity to {config.Servers.Count} server(s)...");

        var allReachable = true;
        foreach (var server in config.Servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await DarlingServerConnector.ProbeAsync(server, null, cancellationToken);
            output.WriteLine(FormatProbeLine(server.DisplayName, probe));
            if (!probe.Success)
            {
                allReachable = false;
            }
        }

        output.WriteLine(allReachable
            ? "All servers reachable."
            : "One or more servers failed the connection pre-flight (see above).");
        return allReachable ? 0 : 1;
    }

    /// <summary>Formats one server's probe outcome as a PASS/FAIL line (pure — unit-testable).</summary>
    public static string FormatProbeLine(string serverName, ConnectionProbeResult probe)
    {
        if (!probe.Success)
        {
            return $"  [FAIL] {serverName}: {probe.Error}";
        }

        var edition = string.IsNullOrEmpty(probe.EngineEditionDescription)
            ? DarlingServerConnector.DescribeEngineEdition(probe.EngineEdition)
            : probe.EngineEditionDescription;
        var msdb = probe.HasMsdbAccess ? "msdb access: yes" : "msdb access: NO (failed-job alerts unavailable)";
        return $"  [PASS] {serverName}: SQL major version {probe.MajorVersion}, {edition}, {msdb}";
    }
}

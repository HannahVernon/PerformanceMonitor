/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
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

    /// <summary>The verb <see cref="PrintViewerConnectionAsync"/> handles (darling-network-endpoints D8).</summary>
    public static bool IsPrintViewerConnectionVerb(string arg) =>
        string.Equals(arg, "--print-viewer-connection", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Prints a paste-ready remote-viewer connection string and the server TLS certificate for the opt-in store
    /// network endpoint (darling-network-endpoints D8). It DPAPI-decrypts the credential of the role
    /// <c>postgres.network.role</c> names (default <c>viewer</c>, read-only) and reads the generated
    /// <c>server.crt</c>, so it must run ON the managed store's host under an account that can decrypt them —
    /// hence Windows-only (the caller is <c>OperatingSystem.IsWindows()</c>-guarded, mirroring
    /// <c>--encrypt-password</c>). The operator pastes the string into the VIEWER machine's darling.json
    /// (<c>postgres.managed = false</c>, into <c>postgres.connectionString</c>, consumed verbatim — no viewer
    /// code change) and saves the emitted PEM where <c>Root Certificate</c> points. Returns 0 on success; 1 on a
    /// mode/role/credential error. Managed-mode only (BYO governs its own exposure, D-BYO); network config lives
    /// out of the all-fatal <see cref="DarlingConfig.Validate"/>, so this verb never calls it.
    /// <para><b>STDOUT carries a LIVE SECRET</b> (the role password) — the verb warns (on STDERR) to redirect it
    /// to an ACL'd file or the clipboard, never scrollback / CI / a screenshare.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<int> PrintViewerConnectionAsync(
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

        var postgres = config.Postgres;
        if (postgres is null)
        {
            error.WriteLine("postgres section is required.");
            return 1;
        }

        /* Managed-mode only: the DPAPI credential files + the generated TLS cert this verb reads exist only in
           managed mode. In BYO the operator's own PostgreSQL governs exposure + credentials (D-BYO). */
        if (!postgres.Managed)
        {
            error.WriteLine(
                "--print-viewer-connection is for the managed store only. In bring-your-own mode " +
                "(postgres.connectionString), your own PostgreSQL governs network exposure and credentials — " +
                "build the remote viewer's connection string from your own role + TLS setup.");
            return 1;
        }

        /* The pg_hba login role the network exposure names — default viewer (read-only, the secure default).
           An explicitly-invalid value is a hard error: the store degrades to loopback for it, so no remote
           connection exists to print. */
        var network = postgres.Network;
        var role = DarlingNetwork.NormalizeNetworkRole(network?.Role);
        if (role is null)
        {
            error.WriteLine(
                $"postgres.network.role '{network?.Role}' is invalid — it must be \"viewer\" (default, read-only) " +
                "or \"admin\". The store degrades to loopback for an unknown role, so there is no remote connection to print.");
            return 1;
        }

        /* Warn (not fail) when the store is not actually network-exposed: the operator still gets a template,
           but the endpoint will not accept it until postgres.network.listen is set and the service restarted. */
        if (!DarlingNetwork.IsExposedListenAddress(network?.Listen))
        {
            error.WriteLine(
                "WARNING: postgres.network.listen is not a network address, so the managed store is loopback-only " +
                "right now. Set postgres.network (listen + allowFrom) and restart the service to expose it (which " +
                "also generates the TLS cert), then re-run this command.");
        }

        var host = ResolveViewerHost(network?.Listen);

        /* Decrypt the role's DPAPI-LocalMachine credential (Windows-only; the caller is IsWindows-guarded).
           The cert lives in the same directory as the credential (ParentOf(dataDirectory)). */
        var dataDirectory = DarlingManagedPostgres.ResolveDataDirectory(postgres);
        var credentialPath = string.Equals(role, "admin", StringComparison.Ordinal)
            ? DarlingManagedPostgres.AdminCredentialPathFor(dataDirectory)
            : DarlingManagedPostgres.ViewerCredentialPathFor(dataDirectory);

        if (!File.Exists(credentialPath))
        {
            error.WriteLine(
                $"The '{role}' role credential ({credentialPath}) does not exist yet. Start the PerformanceMonitor " +
                "Darling service once so its first run provisions the least-privilege roles and their credentials, " +
                "then re-run this command.");
            return 1;
        }

        string password;
        try
        {
            password = DarlingSecrets.Unprotect((await File.ReadAllTextAsync(credentialPath, cancellationToken)).Trim());
        }
        catch (Exception ex)
        {
            error.WriteLine(
                $"Could not decrypt the '{role}' credential at {credentialPath}: {ex.Message} (DPAPI-LocalMachine — " +
                "run this on the same machine as the service, under an account that can read the credential).");
            return 1;
        }

        /* The client-side Root Certificate placeholder: the operator saves the PEM below at this path on the
           VIEWER machine (a bare filename resolves beside the viewer's working directory; an absolute path
           also works). Kept as a literal so the printed string is paste-ready. */
        const string clientCertificatePath = "server.crt";
        var connectionString = BuildViewerConnectionString(host, postgres.Port, role, password, clientCertificatePath);

        /* Guidance + the live-secret warning go to STDERR, so redirecting STDOUT to a file or the clipboard
           captures the connection string + cert WITHOUT swallowing the warning (D8). */
        error.WriteLine();
        error.WriteLine(
            $"WARNING: the connection string below contains a LIVE database password (the '{role}' role), written " +
            "to STDOUT. Redirect it to an ACL'd file or pipe it to the clipboard; do not leave it in shell " +
            "scrollback, CI logs, or a screenshare.");
        error.WriteLine("  Example (file):      PerformanceMonitor.Darling.Service.exe --print-viewer-connection > viewer-connection.txt");
        error.WriteLine("  Example (clipboard): PerformanceMonitor.Darling.Service.exe --print-viewer-connection | clip");
        if (string.Equals(role, "admin", StringComparison.Ordinal))
        {
            error.WriteLine(
                "  NOTE: 'admin' is a WRITE credential holding the config-table pivot surface. Prefer the default " +
                "'viewer' (read-only) for a remote seat; if you must use 'admin', NTFS-ACL the laptop file too.");
        }

        error.WriteLine(
            $"Save the certificate block below as '{clientCertificatePath}' on the viewer machine (beside its " +
            "darling.json) and point \"Root Certificate\" at it — the store uses SSL Mode=VerifyFull, so the cert must match.");
        error.WriteLine();

        output.WriteLine(
            "# Paste into the viewer machine's darling.json -> postgres.connectionString (with postgres.managed = false):");
        output.WriteLine(connectionString);
        output.WriteLine();

        /* Emit the server cert PEM so the operator can copy it to the viewer machine. */
        var certificatePath = Path.Combine(
            Path.GetDirectoryName(credentialPath)!, DarlingManagedPostgres.ServerCertFileName);
        if (File.Exists(certificatePath))
        {
            output.WriteLine($"# Server TLS certificate ({DarlingManagedPostgres.ServerCertFileName}) — save as '{clientCertificatePath}' on the viewer machine:");
            output.WriteLine((await File.ReadAllTextAsync(certificatePath, cancellationToken)).Trim());
        }
        else
        {
            error.WriteLine(
                $"NOTE: the server TLS certificate ({certificatePath}) does not exist yet — the service generates it " +
                "on its first managed start with postgres.network exposed. Enable postgres.network, restart the " +
                "service, then re-run this command to emit the cert for verify-full.");
        }

        return 0;
    }

    /// <summary>
    /// The <c>Host=</c> value for the remote viewer connection (D8 / Round 4 #12): the bind IP itself when
    /// <paramref name="listen"/> is a concrete IP (not IPv4 loopback, not a <c>0.0.0.0</c>/<c>::</c> wildcard) —
    /// verify-full then validates it against the cert's iPAddress SAN — otherwise the machine's hostname, which
    /// the cert also carries as a dnsName SAN (the fallback for a wildcard bind, a hostname listen, or an unset
    /// listen). Pure — unit-testable.
    /// </summary>
    public static string ResolveViewerHost(string? listen)
    {
        var trimmed = listen?.Trim();
        if (!string.IsNullOrEmpty(trimmed)
            && IPAddress.TryParse(trimmed, out var ip)
            && !(ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes()[0] == 127)
            && !ip.Equals(IPAddress.Any)
            && !ip.Equals(IPAddress.IPv6Any))
        {
            return trimmed;
        }

        return Environment.MachineName;
    }

    /// <summary>
    /// The remote viewer's paste-ready Npgsql connection string (D8): the resolved host, the network role,
    /// verify-full TLS against the pinned server cert, the <c>darling</c> database, and the collect/config
    /// search path — the exact string the operator drops into the viewer machine's darling.json
    /// <c>postgres.connectionString</c> (<c>managed = false</c>, consumed verbatim). The managed role password
    /// is service-generated alphanumeric (no connection-string metacharacters), so a hand-built string is safe
    /// and yields the exact documented shape. Pure — unit-testable.
    /// </summary>
    public static string BuildViewerConnectionString(
        string host, int port, string role, string password, string rootCertificatePath) =>
        $"Host={host};Port={port};Username={role};Password={password};Database=darling;" +
        $"Search Path=collect,config,public;SSL Mode=VerifyFull;Root Certificate={rootCertificatePath}";
}

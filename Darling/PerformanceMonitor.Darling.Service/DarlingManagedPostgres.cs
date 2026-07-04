/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// The bundled-Postgres bootstrap — the shipped zero-admin default (headless plan Phase 8 /
/// v4.3 "bundled-managed"): when darling.json says <c>postgres.managed = true</c>, the worker
/// calls <see cref="EnsureRunningAsync"/> BEFORE touching the store, and this class unpacks the
/// runtime shipped beside the service (<c>pg-runtime\pgsql\</c>, self-healing from
/// <c>pg-runtime.zip</c>), initializes a cluster on first run (initdb), starts the server when
/// it is not already running (pg_ctl, loopback only), creates the <c>darling</c> database, and
/// returns the derived connection string. On service shutdown the worker calls
/// <see cref="StopIfStartedByThisProcessAsync"/> — the flag matters: a server this process did
/// NOT start (an operator's own pg_ctl, a previous service crash's surviving postmaster) is
/// adopted for connections but never stopped, because stopping someone else's server is not
/// this service's call to make.
///
/// <para><b>Security posture — why not trust auth, even on localhost:</b> initdb runs with
/// <c>-A scram-sha-256</c> and a generated 32-character random password, never <c>-A trust</c>.
/// Trust would hand superuser DDL/DML to ANY local code that can open a loopback socket —
/// including network-capable-but-not-filesystem-capable attack primitives (SSRF from a co-hosted
/// web app, sandboxed code with socket access) and every other local user — silently and
/// unauditably. A shipped default must also survive an enterprise security scan, where a
/// trust-auth listener is an automatic finding. With scram the credential is required on the
/// wire, failed attempts are auditable, and access is confined to what can read the
/// DPAPI-LocalMachine-protected credential file (<c>pg-credential.dpapi</c> beside the data
/// directory — the same machine-bound posture as darling.json's <c>encryptedPassword</c>, so
/// the interactive Viewer on the same machine can derive the connection string too). Defense in
/// depth on top: <c>listen_addresses = '127.0.0.1'</c>, so the server is never reachable off
/// the machine.</para>
///
/// <para>Every step throws with an actionable message; the worker turns a bootstrap failure
/// into LogCritical + clean service exit (the existing no-store behavior). All idempotent:
/// a second <see cref="EnsureRunningAsync"/> against an initialized, running cluster does no
/// initdb, no restart, no credential rewrite. The conf append is marker-guarded and re-checked
/// every start, so a crash between initdb and the append self-heals on the next run instead of
/// silently degrading TimescaleDB to plain-PG mode.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DarlingManagedPostgres
{
    /// <summary>The cluster's bootstrap superuser AND the store's database name — the managed twin of the sample's unmanaged string.</summary>
    public const string UserName = "darling";
    public const string DatabaseName = "darling";

    /// <summary>DPAPI-LocalMachine blob holding the generated password, beside (not inside) the data directory.</summary>
    public const string CredentialFileName = "pg-credential.dpapi";

    /// <summary>
    /// DPAPI-LocalMachine blobs holding the generated passwords for the least-privilege login roles
    /// the V8 security hardening provisions (<see cref="DarlingManagedRoles"/>): <c>admin</c> reads
    /// both schemas + writes <c>config</c> (the Viewer's default identity), <c>viewer</c> reads only.
    /// Same location/posture as <see cref="CredentialFileName"/> — beside the data directory, machine
    /// bound. Generated idempotently and self-healing (a deleted file regenerates and the superuser
    /// re-asserts the role's password, unlike the owner's unrecoverable password).
    /// </summary>
    public const string AdminCredentialFileName = "pg-admin-credential.dpapi";
    public const string ViewerCredentialFileName = "pg-viewer-credential.dpapi";

    /// <summary>The least-privilege login role names provisioned into the managed cluster (V8 hardening).</summary>
    public const string AdminRoleName = "admin";
    public const string ViewerRoleName = "viewer";

    /// <summary>
    /// The search path (schemas in resolution order) the managed connection strings carry, so pooled
    /// connections resolve the bare table names to collect/config even if the database default was
    /// not (or could not be) set. Same schemas, same order as the SQL-side
    /// <c>PgSchemaGenerator.SearchPath</c> the V8 split writes as the database default — the
    /// connection-string form omits spaces; a test pins the order against the SQL form.
    /// </summary>
    public const string SearchPath = "collect,config,public";

    /// <summary>The server log pg_ctl appends to, beside (not inside) the data directory.</summary>
    public const string ServerLogFileName = "pg.log";

    /// <summary>Marker line guarding the idempotent postgresql.conf append.</summary>
    public const string ConfMarker = "# Managed by PerformanceMonitor Darling -- do not remove this block";

    /// <summary>
    /// Marker for the v2 worker-sizing conf block. A separate versioned block rather than an edit
    /// of the v1 block, so clusters initialized before the sizing existed self-heal on their next
    /// start — <see cref="EnsureConfAppended"/> checks each marker independently, and a
    /// marker-present-but-different-block conf is never rewritten in place.
    /// </summary>
    public const string ConfMarkerV2 = "# Managed by PerformanceMonitor Darling (v2 worker sizing) -- do not remove this block";

    private const string PasswordAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const int PasswordLength = 32;

    /* Process budgets. pg_ctl start/stop get -w -t 60 of their own, so the outer budget only
       has to outlive them; initdb on a cold disk can take tens of seconds. */
    private static readonly TimeSpan s_initDbTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan s_pgCtlTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_statusTimeout = TimeSpan.FromSeconds(30);
    private const int PgCtlWaitSeconds = 60;

    private readonly PostgresConfig _config;
    private readonly ILogger _logger;
    private readonly string _runtimeRoot;
    private readonly string _runtimeZipPath;
    private readonly string _dataDirectory;
    private readonly string _credentialPath;
    private readonly string _serverLogPath;

    private bool _startedByThisProcess;

    public DarlingManagedPostgres(PostgresConfig config, ILogger logger, string? runtimeRootOverride = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runtimeRoot = runtimeRootOverride ?? Path.Combine(AppContext.BaseDirectory, "pg-runtime");
        _runtimeZipPath = Path.Combine(AppContext.BaseDirectory, "pg-runtime.zip");
        _dataDirectory = ResolveDataDirectory(config);
        _credentialPath = CredentialPathFor(_dataDirectory);
        _serverLogPath = Path.Combine(ParentOf(_dataDirectory), ServerLogFileName);
    }

    /// <summary>True when THIS process started the server — the only case shutdown may stop it.</summary>
    public bool StartedByThisProcess => _startedByThisProcess;

    public string DataDirectory => _dataDirectory;

    /// <summary>null/empty dataDirectory means %ProgramData%\PerformanceMonitorDarling\pg (created with inherited ACLs).</summary>
    public static string ResolveDataDirectory(PostgresConfig config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        return string.IsNullOrWhiteSpace(config.DataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PerformanceMonitorDarling", "pg")
            : Path.GetFullPath(config.DataDirectory);
    }

    public static string CredentialPathFor(string dataDirectory)
        => Path.Combine(ParentOf(dataDirectory), CredentialFileName);

    /// <summary>Path to the <c>admin</c> role's DPAPI credential, beside the data directory.</summary>
    public static string AdminCredentialPathFor(string dataDirectory)
        => Path.Combine(ParentOf(dataDirectory), AdminCredentialFileName);

    /// <summary>Path to the <c>viewer</c> role's DPAPI credential, beside the data directory.</summary>
    public static string ViewerCredentialPathFor(string dataDirectory)
        => Path.Combine(ParentOf(dataDirectory), ViewerCredentialFileName);

    /// <summary>
    /// 32 characters from [A-Za-z0-9] via the crypto RNG (~190 bits) — deliberately alphanumeric
    /// only, so the password survives initdb's --pwfile line, the connection string, and any
    /// future conf/pgpass surface without escaping bugs; the length carries the strength.
    /// </summary>
    public static string GeneratePassword()
        => RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength);

    /// <summary>
    /// The block appended once (marker-guarded) to the fresh cluster's postgresql.conf:
    /// TimescaleDB preloaded (the extension refuses to CREATE without it), the configured port,
    /// and loopback-only listening. The port line is a default for anyone starting the cluster
    /// by hand; the service itself passes -o "-p &lt;port&gt;" so a darling.json port change
    /// wins on the next start without editing the conf.
    /// </summary>
    public static string BuildConfAppend(int port)
    {
        var builder = new StringBuilder();
        builder.Append('\n');
        builder.Append(ConfMarker).Append('\n');
        builder.Append("shared_preload_libraries = 'timescaledb'\n");
        builder.Append("port = ").Append(port).Append('\n');
        builder.Append("listen_addresses = '127.0.0.1'\n");
        return builder.ToString();
    }

    /// <summary>
    /// The v2 worker-sizing block. PostgreSQL's default <c>max_worker_processes = 8</c> cannot
    /// launch TimescaleDB's 26 per-hypertable compression policy jobs — the postmaster logs
    /// "failed to launch job ... failed to start a background worker" storms and most policy runs
    /// fail (caught live: 21 failures vs 7 successes in timescaledb_information.job_stats on a
    /// fresh managed instance). Sizing follows the TimescaleDB guidance
    /// (max_worker_processes = 3 + timescaledb.max_background_workers + max_parallel_workers,
    /// background workers sized to total jobs + 2): 26 policy jobs + scheduler + slack = 28, and
    /// 3 + 28 + 8 (default max_parallel_workers) = 39, rounded to 40. Idle background workers
    /// cost a few MB each and no CPU. Both settings need a PostgreSQL restart, so an existing
    /// cluster picks this up on its next service-owned start — an adopted (not-started-by-us)
    /// server heals the conf now and applies it whenever its operator next restarts it.
    /// </summary>
    public static string BuildWorkerSizingConfAppend()
    {
        var builder = new StringBuilder();
        builder.Append('\n');
        builder.Append(ConfMarkerV2).Append('\n');
        builder.Append("timescaledb.max_background_workers = 28\n");
        builder.Append("max_worker_processes = 40\n");
        return builder.ToString();
    }

    /// <summary>
    /// The derived managed-mode connection string: localhost + port + darling/darling + the generated
    /// password, carrying the collect/config <see cref="SearchPath"/> so every pooled connection
    /// resolves the bare table names to the V8 schemas regardless of the database default.
    /// </summary>
    public static string BuildConnectionString(int port, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = port,
            Username = UserName,
            Password = password,
            Database = DatabaseName,
            SearchPath = SearchPath,
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Derives the managed connection string from the stored credential WITHOUT touching the
    /// server — for secondary consumers (the MCP host) that must never bootstrap; the worker
    /// owns the lifecycle. Null until the worker's first initdb has written the credential.
    /// </summary>
    public static string? TryBuildConnectionStringFromStoredCredential(PostgresConfig config)
    {
        var credentialPath = CredentialPathFor(ResolveDataDirectory(config));
        if (!File.Exists(credentialPath))
        {
            return null;
        }

        return BuildConnectionString(config.Port, DarlingSecrets.Unprotect(File.ReadAllText(credentialPath).Trim()));
    }

    /// <summary>
    /// The whole first-run story, idempotent: locate/unpack the runtime, initdb if the data
    /// directory has no cluster, self-heal the conf append, start the server if nothing is
    /// listening on the data directory, create the darling database if missing, and return the
    /// ready-to-use connection string. Throws (actionably) on any failure — the worker logs it
    /// critical and exits cleanly.
    /// </summary>
    public async Task<string> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        var binDirectory = await EnsureRuntimeAsync(cancellationToken);

        /* Create the directory that holds the data dir + the DPAPI credential files, then LOCK it
           down (V8 hardening): strip the inherited world-readable ACLs %ProgramData% would otherwise
           give it, leaving SYSTEM + Administrators + the service account, plus INTERACTIVE traverse so
           the operator's Viewer can reach the admin/viewer credential files beside the data directory.
           Re-applied every start (self-healing, like the conf append) so an existing loose install is
           tightened too. Done BEFORE initdb so the data-dir subtree inherits the locked-down ACL. */
        Directory.CreateDirectory(ParentOf(_dataDirectory));
        TryHardenDirectory(ParentOf(_dataDirectory));

        if (!File.Exists(Path.Combine(_dataDirectory, "PG_VERSION")))
        {
            await InitializeClusterAsync(binDirectory, cancellationToken);
        }

        EnsureConfAppended();

        var password = ReadStoredPassword();

        if (await IsRunningAsync(binDirectory, cancellationToken))
        {
            /* Already running — a previous service crash's surviving postmaster, or an operator
               started it by hand. Use it, never stop it (the flag stays false). */
            _logger.LogInformation(
                "Managed Postgres is already running for {DataDirectory} — connecting to it (this service did not start it and will not stop it)",
                _dataDirectory);
        }
        else
        {
            await StartServerAsync(binDirectory, cancellationToken);
            _startedByThisProcess = true;
        }

        var connectionString = BuildConnectionString(_config.Port, password);
        await EnsureDatabaseAsync(connectionString, cancellationToken);
        return connectionString;
    }

    /// <summary>
    /// pg_ctl stop -m fast, ONLY when this process started the server. Called on worker
    /// shutdown; never throws (a failed stop at shutdown is a warning, not a crash) and never
    /// takes the (already-cancelled) stopping token.
    /// </summary>
    public async Task StopIfStartedByThisProcessAsync()
    {
        if (!_startedByThisProcess)
        {
            return;
        }

        _startedByThisProcess = false;
        try
        {
            var binDirectory = Path.Combine(_runtimeRoot, "pgsql", "bin");
            var (exitCode, output) = await RunToolAsync(
                Path.Combine(binDirectory, "pg_ctl.exe"),
                $"stop -D \"{_dataDirectory}\" -m fast -w -t {PgCtlWaitSeconds}",
                s_pgCtlTimeout,
                CancellationToken.None);

            if (exitCode == 0)
            {
                _logger.LogInformation("Managed Postgres stopped (fast shutdown)");
            }
            else
            {
                _logger.LogWarning("Managed Postgres stop reported exit code {ExitCode}: {Output}", exitCode, output);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Managed Postgres stop failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Locates pg-runtime\pgsql\bin beside the service; when only pg-runtime.zip is present
    /// (fresh install, or someone deleted the extracted copy) it self-heals by extracting.
    /// Neither present is a packaging problem with a packaging answer, not a retry loop.
    /// </summary>
    private async Task<string> EnsureRuntimeAsync(CancellationToken cancellationToken)
    {
        var pgsqlDirectory = Path.Combine(_runtimeRoot, "pgsql");
        var binDirectory = Path.Combine(pgsqlDirectory, "bin");
        var pgCtl = Path.Combine(binDirectory, "pg_ctl.exe");
        if (File.Exists(pgCtl))
        {
            return binDirectory;
        }

        if (File.Exists(_runtimeZipPath))
        {
            _logger.LogInformation("Extracting the bundled Postgres runtime from {Zip} (first run)", _runtimeZipPath);
            await Task.Run(
                () => ZipFile.ExtractToDirectory(_runtimeZipPath, _runtimeRoot, overwriteFiles: true),
                cancellationToken);

            if (File.Exists(pgCtl))
            {
                return binDirectory;
            }

            throw new InvalidOperationException(
                $"Extracted {_runtimeZipPath} but {pgCtl} is still missing — the archive does not contain pgsql\\bin. " +
                "Rebuild it with Darling\\tools\\fetch-pg-runtime.ps1 and redeploy.");
        }

        throw new InvalidOperationException(
            $"Managed Postgres runtime not found: neither {pgsqlDirectory} nor {_runtimeZipPath} exists. " +
            "Packaging builds pg-runtime.zip with Darling\\tools\\fetch-pg-runtime.ps1 and ships it beside the service binary; " +
            "put it there, or set postgres.managed = false and point postgres.connectionString at your own PostgreSQL.");
    }

    /// <summary>
    /// First-run initdb. The credential is generated and DPAPI-persisted BEFORE initdb runs:
    /// if the order were reversed, a crash after a successful initdb would leave a cluster whose
    /// password nobody knows. A failed initdb leaves the credential file behind harmlessly —
    /// the next attempt regenerates and overwrites it (initdb itself cleans up its partial data
    /// directory on failure).
    /// </summary>
    private async Task InitializeClusterAsync(string binDirectory, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing managed Postgres cluster in {DataDirectory} (first run)", _dataDirectory);

        var password = GeneratePassword();
        File.WriteAllText(_credentialPath, DarlingSecrets.Protect(password));
        /* The SUPERUSER credential — locked to SYSTEM + Administrators + the service account, NEVER an
           interactive user (post-split the Viewer connects as admin/viewer, never darling). */
        TryHardenCredentialFile(_credentialPath, allowInteractiveRead: false);

        /* --pwfile is the non-interactive way to hand initdb the superuser password; the file
           lives next to the (equally sensitive, DPAPI-protected) credential for its few seconds
           of life and is deleted in finally. Alphanumeric-only password, UTF8 no BOM — a BOM
           would corrupt the first (and only) line initdb reads. Same restrictive ACL: it briefly
           holds the superuser password in the clear. */
        var passwordFile = Path.Combine(ParentOf(_dataDirectory), "pg-pwfile.tmp");
        File.WriteAllText(passwordFile, password + "\n");
        TryHardenCredentialFile(passwordFile, allowInteractiveRead: false);
        try
        {
            var (exitCode, output) = await RunToolAsync(
                Path.Combine(binDirectory, "initdb.exe"),
                $"-D \"{_dataDirectory}\" -U {UserName} -A scram-sha-256 --pwfile=\"{passwordFile}\" -E UTF8 --locale=C --data-checksums",
                s_initDbTimeout,
                cancellationToken);

            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"initdb failed (exit code {exitCode}) for {_dataDirectory}. Output:\n{output}");
            }
        }
        finally
        {
            try
            {
                File.Delete(passwordFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not delete the temporary password file {Path} — delete it by hand", passwordFile);
            }
        }

        _logger.LogInformation("Managed Postgres cluster initialized (scram-sha-256, data checksums, UTF8/C locale)");
    }

    /// <summary>
    /// Marker-guarded conf append, re-checked on EVERY start — heals the crash window between
    /// initdb and the first append, which would otherwise silently cost TimescaleDB
    /// (shared_preload_libraries missing = CREATE EXTENSION fails = plain-PG degradation).
    /// </summary>
    private void EnsureConfAppended()
    {
        var confPath = Path.Combine(_dataDirectory, "postgresql.conf");
        if (!File.Exists(confPath))
        {
            throw new InvalidOperationException(
                $"{confPath} is missing although PG_VERSION exists — the data directory looks damaged. " +
                "Stop the service, move/delete the data directory to re-initialize (destroys collected history), " +
                "or restore it from backup.");
        }

        var conf = File.ReadAllText(confPath);
        if (!conf.Contains(ConfMarker, StringComparison.Ordinal))
        {
            File.AppendAllText(confPath, BuildConfAppend(_config.Port));
            _logger.LogInformation("Appended managed settings to postgresql.conf (timescaledb preload, port {Port}, loopback only)", _config.Port);
        }

        /* Checked independently of the v1 marker: clusters initialized before the worker sizing
           existed have v1 but not v2, and heal here on their next start. */
        if (!conf.Contains(ConfMarkerV2, StringComparison.Ordinal))
        {
            File.AppendAllText(confPath, BuildWorkerSizingConfAppend());
            _logger.LogInformation("Appended v2 worker sizing to postgresql.conf (timescaledb.max_background_workers 28, max_worker_processes 40; effective from the next PostgreSQL restart)");
        }
    }

    /// <summary>pg_ctl status: 0 = a postmaster is running on this data directory, 3 = not running, 4 = bad/inaccessible data directory.</summary>
    private async Task<bool> IsRunningAsync(string binDirectory, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunToolAsync(
            Path.Combine(binDirectory, "pg_ctl.exe"),
            $"status -D \"{_dataDirectory}\"",
            s_statusTimeout,
            cancellationToken);

        return exitCode switch
        {
            0 => true,
            3 => false,
            _ => throw new InvalidOperationException(
                $"pg_ctl status reported exit code {exitCode} for {_dataDirectory} — the data directory is not usable. Output:\n{output}"),
        };
    }

    /// <summary>
    /// pg_ctl start, windowed (-w): returns only when the server accepts connections. -o "-p"
    /// makes darling.json's port authoritative over the conf line. pg_ctl itself handles a
    /// stale postmaster.pid from a crash (the new postmaster validates and replaces it); when
    /// start still fails, the server log tail is surfaced in the error because that is where
    /// Postgres explains itself.
    /// </summary>
    private async Task StartServerAsync(string binDirectory, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting managed Postgres on 127.0.0.1:{Port} (log: {Log})", _config.Port, _serverLogPath);

        var exitCode = await RunDetachingToolAsync(
            Path.Combine(binDirectory, "pg_ctl.exe"),
            $"-D \"{_dataDirectory}\" -o \"-p {_config.Port}\" -l \"{_serverLogPath}\" -w -t {PgCtlWaitSeconds} start",
            s_pgCtlTimeout,
            cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"pg_ctl start failed (exit code {exitCode}) for {_dataDirectory}.\n" +
                $"Server log tail ({_serverLogPath}):\n{ReadServerLogTail()}");
        }

        _logger.LogInformation("Managed Postgres started");
    }

    private string ReadServerLogTail()
    {
        try
        {
            if (!File.Exists(_serverLogPath))
            {
                return "(no server log written)";
            }

            var lines = File.ReadAllLines(_serverLogPath);
            var take = Math.Min(40, lines.Length);
            return string.Join('\n', lines[^take..]);
        }
        catch (IOException ex)
        {
            return $"(could not read server log: {ex.Message})";
        }
    }

    /// <summary>
    /// CREATE DATABASE darling if missing, via the maintenance database. Doubles as the
    /// bootstrap's end-to-end auth check: this is the first real connection with the derived
    /// credential, so a wrong/stale credential fails HERE with a clear Postgres auth error
    /// instead of somewhere inside the migration path.
    /// </summary>
    private async Task EnsureDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        using (var exists = new NpgsqlCommand($"SELECT 1 FROM pg_database WHERE datname = '{DatabaseName}'", connection))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return;
            }
        }

        _logger.LogInformation("Creating the '{Database}' database", DatabaseName);

        /* Identifier from the class constant, never from input — same interpolation reasoning
           as TimescaleSupport/DarlingRetention. CREATE DATABASE cannot run in a transaction;
           plain ExecuteNonQuery is the correct shape. */
        using var create = new NpgsqlCommand($"CREATE DATABASE {DatabaseName}", connection);
        await create.ExecuteNonQueryAsync(cancellationToken);
    }

    private string ReadStoredPassword()
    {
        if (!File.Exists(_credentialPath))
        {
            throw new InvalidOperationException(
                $"The managed Postgres data directory {_dataDirectory} is initialized but its credential file " +
                $"{_credentialPath} is missing, so the service cannot authenticate. Restore the file from backup, " +
                "or stop the service and move/delete the data directory to re-initialize (destroys collected history), " +
                "or switch to unmanaged mode (postgres.connectionString) against a server you manage.");
        }

        /* Pre-plant guard: never trust a superuser credential file owned by an arbitrary local user
           (SYSTEM / Administrators / the service account only). A file someone else owns may have been
           planted to feed the service a password they know — refuse rather than authenticate with it. */
        if (!DarlingFileSecurity.IsTrustedOwner(_credentialPath))
        {
            throw new InvalidOperationException(
                $"The managed Postgres credential file {_credentialPath} is not owned by SYSTEM, Administrators, or the " +
                "service account — it may have been tampered with or pre-planted. Refusing to trust it. Investigate, then " +
                "restore it from backup or re-initialize the data directory (destroys collected history).");
        }

        return DarlingSecrets.Unprotect(File.ReadAllText(_credentialPath).Trim());
    }

    /// <summary>
    /// Best-effort restrictive ACL on the credential directory (V8 hardening). A failure is logged
    /// loud but never bricks the service — the fresh-install path (the service account owns the
    /// just-created directory) succeeds, and the trusted-owner read guard is the complementary defense.
    /// </summary>
    private void TryHardenDirectory(string path)
    {
        try
        {
            DarlingFileSecurity.HardenDirectory(path, allowInteractiveTraverse: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Could not restrict the ACL on {Path} ({Message}). The DPAPI credential files it holds may be readable by " +
                "other local users — fix the directory permissions by hand (SYSTEM/Administrators/the service account only).",
                path, ex.Message);
        }
    }

    /// <summary>Best-effort restrictive ACL on one credential file — same posture as <see cref="TryHardenDirectory"/>.</summary>
    private void TryHardenCredentialFile(string path, bool allowInteractiveRead)
    {
        try
        {
            DarlingFileSecurity.HardenFile(path, allowInteractiveRead);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Could not restrict the ACL on {Path} ({Message}). This DPAPI credential may be readable by other local " +
                "users — fix the file permissions by hand.", path, ex.Message);
        }
    }

    private static string ParentOf(string dataDirectory)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)));
        if (string.IsNullOrEmpty(parent))
        {
            throw new InvalidOperationException(
                $"postgres.dataDirectory '{dataDirectory}' has no parent directory — use a subdirectory " +
                "(the credential and server log live beside the data directory), not a drive root.");
        }

        return parent;
    }

    /// <summary>
    /// Runs one PG tool with captured, interleaved stdout+stderr and a hard timeout. The
    /// service's stopping token cancels a bootstrap mid-flight; the shutdown stop path passes
    /// CancellationToken.None because its token is by definition already cancelled.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunToolAsync(
        string exePath, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException(
                $"{exePath} is missing — the pg-runtime directory is incomplete. Rebuild pg-runtime.zip with " +
                "Darling\\tools\\fetch-pg-runtime.ps1 and redeploy (deleting the pg-runtime directory makes the " +
                "service re-extract the zip on its next start).");
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var output = new StringBuilder();
        var outputLock = new object();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (outputLock) { output.AppendLine(e.Data); }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (outputLock) { output.AppendLine(e.Data); }
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {exePath}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                /* Exited between the timeout and the kill. */
            }

            cancellationToken.ThrowIfCancellationRequested();

            string capturedSoFar;
            lock (outputLock) { capturedSoFar = output.ToString().Trim(); }
            throw new TimeoutException(
                $"{Path.GetFileName(exePath)} {arguments} did not finish within {timeout.TotalSeconds:0}s. Output so far:\n{capturedSoFar}");
        }

        lock (outputLock)
        {
            return (process.ExitCode, output.ToString().Trim());
        }
    }

    /// <summary>
    /// Runs the ONE tool whose spawned server outlives it — pg_ctl start — with NO output
    /// redirection, waiting on process exit alone. Redirecting here is a guaranteed hang on
    /// SUCCESS: pg_ctl launches postgres.exe with handle inheritance, the postmaster keeps the
    /// pipe write-ends open for its whole lifetime, so the pipes never reach EOF — and
    /// <see cref="Process.WaitForExitAsync(CancellationToken)"/> waits for redirected output to
    /// drain (dotnet/runtime#42556), so a healthy start times out after pg_ctl itself has long
    /// exited. Caught live by the gated bootstrap E2E. The lost pg_ctl console text is the
    /// throwaway "waiting for server to start..." narration; the real failure story is the -l
    /// server log, which the caller surfaces via <see cref="ReadServerLogTail"/>. initdb and
    /// pg_ctl stop/status stay on <see cref="RunToolAsync"/> — nothing they spawn survives them,
    /// so their pipes close and their captured output is worth having.
    /// </summary>
    private static async Task<int> RunDetachingToolAsync(
        string exePath, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException(
                $"{exePath} is missing — the pg-runtime directory is incomplete. Rebuild pg-runtime.zip with " +
                "Darling\\tools\\fetch-pg-runtime.ps1 and redeploy (deleting the pg-runtime directory makes the " +
                "service re-extract the zip on its next start).");
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {exePath}.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            /* A start stuck past pg_ctl's own -w -t budget is a real failure; the tree kill
               reaps the half-started postmaster while it is still pg_ctl's child. */
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                /* Exited between the timeout and the kill. */
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"{Path.GetFileName(exePath)} {arguments} did not finish within {timeout.TotalSeconds:0}s.");
        }

        return process.ExitCode;
    }
}

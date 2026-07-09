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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The bundled-Postgres bootstrap (the shipped zero-admin default). Ungated: the derived
/// connection string, the postgresql.conf append pins (timescaledb preload, port, loopback
/// only), the generated password's shape, the DPAPI credential round-trip through the stored
/// file, and the data-directory/credential path conventions. Gated on DARLING_TEST_PGRUNTIME
/// (the path of an assembled pg-runtime directory — the folder containing
/// pgsql\bin\pg_ctl.exe; <c>fetch-pg-runtime.ps1 -KeepWork</c> leaves one at
/// artifacts\pg-runtime-work\assemble\pg-runtime): the full first-run story into a temp data
/// directory on a scratch port — initdb, start, create database, authenticate, then an
/// idempotent second EnsureRunning and an ownership-respecting stop — never touching a real
/// Postgres and never downloading anything.
/// </summary>
public sealed class DarlingManagedPostgresTests
{
    [Fact]
    public void DerivedConnectionString_LocalhostPortDarlingDarling()
    {
        var parsed = new NpgsqlConnectionStringBuilder(DarlingManagedPostgres.BuildConnectionString(5641, "pw123"));

        /* Explicit IPv4 loopback (not the name "localhost"): listen_addresses binds 127.0.0.1 (plus the
           optional network IP when exposed), NOT ::1, so a host resolving "localhost" to IPv6 first could
           otherwise miss the listener (darling-network-endpoints). */
        Assert.Equal("127.0.0.1", parsed.Host);
        Assert.Equal(5641, parsed.Port);
        Assert.Equal("darling", parsed.Username);
        Assert.Equal("pw123", parsed.Password);
        Assert.Equal("darling", parsed.Database);

        /* V8 split: the owner connection string carries the collect/config search path so the
           service's bare-name COPY writes and reads resolve to the new schemas on every pooled
           connection, regardless of the database default. Same schemas, same order as the SQL-side
           PgSchemaGenerator.SearchPath. */
        Assert.Equal("collect,config,public", parsed.SearchPath);
        Assert.Equal(
            PerformanceMonitor.Darling.Storage.PgSchemaGenerator.SearchPath.Replace(" ", "", StringComparison.Ordinal),
            parsed.SearchPath);
    }

    [Fact]
    public void RoleCredentialPaths_BesideTheDataDirectory()
    {
        /* The admin/viewer role credentials live beside the data directory, same posture as the
           owner's pg-credential.dpapi (trailing separator tolerated). */
        Assert.Equal(@"D:\darling\pg-admin-credential.dpapi", DarlingManagedPostgres.AdminCredentialPathFor(@"D:\darling\pg"));
        Assert.Equal(@"D:\darling\pg-admin-credential.dpapi", DarlingManagedPostgres.AdminCredentialPathFor(@"D:\darling\pg\"));
        Assert.Equal(@"D:\darling\pg-viewer-credential.dpapi", DarlingManagedPostgres.ViewerCredentialPathFor(@"D:\darling\pg"));

        /* Three distinct files: owner, admin, viewer. */
        Assert.Equal("pg-credential.dpapi", DarlingManagedPostgres.CredentialFileName);
        Assert.Equal("pg-admin-credential.dpapi", DarlingManagedPostgres.AdminCredentialFileName);
        Assert.Equal("pg-viewer-credential.dpapi", DarlingManagedPostgres.ViewerCredentialFileName);
    }

    [Fact]
    public void ConfAppend_PinsPreloadPortAndLoopbackOnly()
    {
        var block = DarlingManagedPostgres.BuildConfAppend(5641);

        Assert.Contains(DarlingManagedPostgres.ConfMarker, block, StringComparison.Ordinal);
        Assert.Contains("shared_preload_libraries = 'timescaledb'", block, StringComparison.Ordinal);
        Assert.Contains("port = 5641", block, StringComparison.Ordinal);
        Assert.Contains("listen_addresses = '127.0.0.1'", block, StringComparison.Ordinal);

        /* Worker sizing lives in the v2 block, never in v1 — pre-v2 clusters heal by gaining the
           SECOND block, so v1's content must stay stable. */
        Assert.DoesNotContain("max_worker_processes", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// PostgreSQL's default max_worker_processes = 8 cannot launch the 26 per-hypertable
    /// compression policy jobs (live smoke: "failed to start a background worker" storms,
    /// 21 failed vs 7 successful policy runs). Pins the TimescaleDB-guidance sizing:
    /// background workers = jobs + 2 = 28; max_worker_processes = 3 + 28 + 8 parallel = 39 -> 40.
    /// </summary>
    [Fact]
    public void WorkerSizingConfAppend_PinsV2MarkerAndSizing()
    {
        var block = DarlingManagedPostgres.BuildWorkerSizingConfAppend();

        Assert.Contains(DarlingManagedPostgres.ConfMarkerV2, block, StringComparison.Ordinal);
        Assert.Contains("timescaledb.max_background_workers = 28", block, StringComparison.Ordinal);
        Assert.Contains("max_worker_processes = 40", block, StringComparison.Ordinal);

        /* v2 must not restate v1 settings — the blocks compose, they don't compete. */
        Assert.DoesNotContain("shared_preload_libraries", block, StringComparison.Ordinal);
        Assert.DoesNotContain("listen_addresses", block, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratePassword_32AlphanumericCryptoRandom()
    {
        var first = DarlingManagedPostgres.GeneratePassword();
        var second = DarlingManagedPostgres.GeneratePassword();

        /* Alphanumeric-only by design (survives --pwfile and connection strings without
           escaping); the 32-char length carries the strength (~190 bits over a 62 charset). */
        Assert.Equal(32, first.Length);
        Assert.All(first, c => Assert.True(char.IsAsciiLetterOrDigit(c), $"unexpected password character '{c}'"));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void PathConventions_DefaultDataDirectory_AndCredentialBesideIt()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PerformanceMonitorDarling", "pg"),
            DarlingManagedPostgres.ResolveDataDirectory(new PostgresConfig()));

        /* The credential lives BESIDE the data directory (not inside it — initdb wants the
           directory empty), trailing separator tolerated. */
        Assert.Equal(@"D:\darling\pg-credential.dpapi", DarlingManagedPostgres.CredentialPathFor(@"D:\darling\pg"));
        Assert.Equal(@"D:\darling\pg-credential.dpapi", DarlingManagedPostgres.CredentialPathFor(@"D:\darling\pg\"));
    }

    [Fact]
    public void StoredCredential_DpapiRoundTrip_DerivesTheConnectionString()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-pgcred-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var config = new PostgresConfig { Managed = true, Port = 5991, DataDirectory = dataDirectory };

            /* No credential yet → null (the MCP host's first-boot wait relies on this). */
            Assert.Null(DarlingManagedPostgres.TryBuildConnectionStringFromStoredCredential(config));

            var password = DarlingManagedPostgres.GeneratePassword();
            var credentialPath = DarlingManagedPostgres.CredentialPathFor(dataDirectory);
            File.WriteAllText(credentialPath, DarlingSecrets.Protect(password));

            /* The blob on disk is never the plaintext. */
            Assert.DoesNotContain(password, File.ReadAllText(credentialPath), StringComparison.Ordinal);

            var derived = DarlingManagedPostgres.TryBuildConnectionStringFromStoredCredential(config);
            Assert.NotNull(derived);
            var parsed = new NpgsqlConnectionStringBuilder(derived);
            Assert.Equal(password, parsed.Password);
            Assert.Equal(5991, parsed.Port);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void McpCredentialPath_BesideTheDataDirectory_AndDistinctFile()
    {
        /* The mcp role credential lives beside the data directory, same posture as owner/admin/viewer
           (trailing separator tolerated) — a fourth distinct file (darling-network-endpoints, D3-role). */
        Assert.Equal(@"D:\darling\pg-mcp-credential.dpapi", DarlingManagedPostgres.McpCredentialPathFor(@"D:\darling\pg"));
        Assert.Equal(@"D:\darling\pg-mcp-credential.dpapi", DarlingManagedPostgres.McpCredentialPathFor(@"D:\darling\pg\"));
        Assert.Equal("pg-mcp-credential.dpapi", DarlingManagedPostgres.McpCredentialFileName);
        Assert.Equal("mcp", DarlingManagedPostgres.McpRoleName);
    }

    [Fact]
    public void McpStoredCredential_DpapiRoundTrip_DerivesMcpConnectionString()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-mcpcred-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var config = new PostgresConfig { Managed = true, Port = 5992, DataDirectory = dataDirectory };

            /* No mcp credential yet → null (the MCP host polls for it after the worker provisions it). */
            Assert.Null(DarlingManagedPostgres.TryBuildMcpConnectionStringFromStoredCredential(config));

            var password = DarlingManagedPostgres.GeneratePassword();
            File.WriteAllText(DarlingManagedPostgres.McpCredentialPathFor(dataDirectory), DarlingSecrets.Protect(password));

            var derived = DarlingManagedPostgres.TryBuildMcpConnectionStringFromStoredCredential(config);
            Assert.NotNull(derived);
            var parsed = new NpgsqlConnectionStringBuilder(derived);

            /* The mcp pool connects as the mcp role over the explicit IPv4 loopback, same search path. */
            Assert.Equal("127.0.0.1", parsed.Host);
            Assert.Equal(5992, parsed.Port);
            Assert.Equal("mcp", parsed.Username);
            Assert.Equal(password, parsed.Password);
            Assert.Equal("darling", parsed.Database);
            Assert.Equal("collect,config,public", parsed.SearchPath);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Bootstrap_EndToEnd_FirstRunThenIdempotentSecondRun_Gated()
    {
        var runtimeRoot = Environment.GetEnvironmentVariable("DARLING_TEST_PGRUNTIME");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(runtimeRoot),
            "Set DARLING_TEST_PGRUNTIME to an assembled pg-runtime directory (the folder containing pgsql\\bin\\pg_ctl.exe; " +
            "Darling\\tools\\fetch-pg-runtime.ps1 -KeepWork leaves one under artifacts\\pg-runtime-work\\assemble\\pg-runtime) " +
            "to run the managed-Postgres bootstrap E2E.");
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The bundled runtime is Windows-only.");
        Assert.SkipUnless(File.Exists(Path.Combine(runtimeRoot!, "pgsql", "bin", "pg_ctl.exe")),
            $"DARLING_TEST_PGRUNTIME={runtimeRoot} does not contain pgsql\\bin\\pg_ctl.exe.");

        var root = Directory.CreateTempSubdirectory("darling-pgboot-");
        var dataDirectory = Path.Combine(root.FullName, "pg");
        var config = new PostgresConfig
        {
            Managed = true,
            Port = FindFreeTcpPort(),
            DataDirectory = dataDirectory,
        };

        var owner = new DarlingManagedPostgres(config, NullLogger.Instance, runtimeRoot);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            /* First run: initdb (scram + generated credential) + conf append + start + create db. */
            var connectionString = await owner.EnsureRunningAsync(timeout.Token);
            Assert.True(owner.StartedByThisProcess);
            Assert.True(File.Exists(Path.Combine(dataDirectory, "PG_VERSION")));

            var credentialPath = DarlingManagedPostgres.CredentialPathFor(dataDirectory);
            Assert.True(File.Exists(credentialPath));
            var credentialBytes = File.ReadAllBytes(credentialPath);

            var conf = File.ReadAllText(Path.Combine(dataDirectory, "postgresql.conf"));
            Assert.Contains("shared_preload_libraries = 'timescaledb'", conf, StringComparison.Ordinal);
            Assert.Contains("listen_addresses = '127.0.0.1'", conf, StringComparison.Ordinal);
            Assert.Contains("max_worker_processes = 40", conf, StringComparison.Ordinal);

            /* The derived credential really authenticates (scram, not trust) into the darling
               database — and the server started with our appended conf, so the timescaledb
               preload line was accepted; the v2 worker sizing was accepted too (the setting is
               live, not just written). */
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(timeout.Token);
                using var current = new NpgsqlCommand("SELECT current_database(), current_user, current_setting('max_worker_processes')", connection);
                using var reader = await current.ExecuteReaderAsync(timeout.Token);
                Assert.True(await reader.ReadAsync(timeout.Token));
                Assert.Equal("darling", reader.GetString(0));
                Assert.Equal("darling", reader.GetString(1));
                Assert.Equal("40", reader.GetString(2));
            }

            /* Second EnsureRunning against the live server: idempotent — no re-init (credential
               bytes untouched), no ownership grab (this instance did not start the server),
               the same derived connection string, and no duplicate conf blocks (one v1 marker,
               one v2 marker). */
            var second = new DarlingManagedPostgres(config, NullLogger.Instance, runtimeRoot);
            var secondConnectionString = await second.EnsureRunningAsync(timeout.Token);
            Assert.False(second.StartedByThisProcess);
            Assert.Equal(credentialBytes, File.ReadAllBytes(credentialPath));
            Assert.Equal(connectionString, secondConnectionString);

            var confAfterSecond = File.ReadAllText(Path.Combine(dataDirectory, "postgresql.conf"));
            Assert.Equal(1, CountOccurrences(confAfterSecond, DarlingManagedPostgres.ConfMarker));
            Assert.Equal(1, CountOccurrences(confAfterSecond, DarlingManagedPostgres.ConfMarkerV2));

            /* Both up/down probes below must bypass Npgsql's pool: OpenAsync on a pooled string
               can hand back an idle socket with no I/O at all, which "succeeds" against a stopped
               server — the refused-connection assert below failed exactly that way live. */
            var unpooled = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

            /* A non-owner's stop must be a no-op — the server keeps accepting connections. */
            await second.StopIfStartedByThisProcessAsync();
            await using (var stillUp = new NpgsqlConnection(unpooled))
            {
                await stillUp.OpenAsync(timeout.Token);
            }

            /* The owner's stop is real: fast shutdown, then connections are refused. */
            await owner.StopIfStartedByThisProcessAsync();
            Assert.False(owner.StartedByThisProcess);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using var refused = new NpgsqlConnection(unpooled);
                await refused.OpenAsync(timeout.Token);
            });
        }
        finally
        {
            /* Idempotent when the happy path already stopped it; the safety net when an assert
               threw mid-flight. */
            await owner.StopIfStartedByThisProcessAsync();
            TryDeleteRecursive(root.FullName);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static int FindFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Postgres releases its files a beat after fast shutdown — retry the temp-dir delete.</summary>
    private static void TryDeleteRecursive(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }
}

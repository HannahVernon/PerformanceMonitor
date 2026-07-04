/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The live round-trips for the V8 security split (#1262), gated on DARLING_TEST_PG (a dev
/// Postgres + TimescaleDB connection string; the connecting role must be able to CREATE ROLE and
/// own the store). Proves what the ungated shape tests cannot: after migration the tables really
/// live in collect/config and resolve by their bare names; a least-privilege role can read collect
/// but is DENIED a config write (42501); a table created into collect AFTER the grants auto-inherits
/// SELECT (the ALTER DEFAULT PRIVILEGES proof); and — the design's flagged validation item — an
/// already-COMPRESSED TimescaleDB hypertable survives ALTER TABLE ... SET SCHEMA and stays readable
/// by the least-privilege role.
///
/// <para>Uses distinct <c>sec_admin_test</c>/<c>sec_viewer_test</c> roles (not the real
/// admin/viewer) so a shared dev store running an actual service is never clobbered, and grants on
/// the schemas only (no REVOKE on a named database), so the test does not depend on the store's
/// database name. Every object it creates is cleaned up.</para>
/// </summary>
[Collection("live-postgres")]
public sealed class DarlingSecuritySplitLiveTests
{
    private const string AdminRole = "sec_admin_test";
    private const string ViewerRole = "sec_viewer_test";
    private const string RolePassword = "SecSplitTestPw0123456789abcdef01"; // alnum, like the real generator

    private static string RequireLivePostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres (+TimescaleDB) connection string, owner/superuser, to run the security-split live tests.");
        return connectionString!;
    }

    [Fact]
    public async Task V8_MovesTablesToCollectAndConfig_AndBareNamesResolve()
    {
        var connectionString = RequireLivePostgres();
        var ct = TestContext.Current.CancellationToken;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        /* MigrateAsync applies V8 (idempotent) and best-effort sets the database-default search_path. */
        await PgMigrations.MigrateAsync(connection, ct);

        /* A collector table and a metadata table live in collect; a coordination table in config. */
        Assert.Equal("collect", await SchemaOfAsync(connection, "wait_stats", ct));
        Assert.Equal("collect", await SchemaOfAsync(connection, "analysis_findings", ct));
        Assert.Equal("config", await SchemaOfAsync(connection, "config_mute_rules", ct));

        /* The bare, unqualified name every SQL site uses resolves — proof the search_path works, so
           no query file had to be re-qualified. A fresh connection inherits the database default. */
        await using var fresh = new NpgsqlConnection(connectionString);
        await fresh.OpenAsync(ct);
        using var bare = new NpgsqlCommand("SELECT count(*) FROM config_mute_rules", fresh);
        Assert.NotNull(await bare.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Roles_AdminWritesConfig_ViewerDenied_AndNewCollectTableAutoGrants()
    {
        var connectionString = RequireLivePostgres();
        var ct = TestContext.Current.CancellationToken;

        await using var owner = new NpgsqlConnection(connectionString);
        await owner.OpenAsync(ct);
        await PgMigrations.MigrateAsync(owner, ct);

        await CreateTestRolesAndGrantsAsync(owner, ct);
        try
        {
            var adminString = RoleConnectionString(connectionString, AdminRole);
            var viewerString = RoleConnectionString(connectionString, ViewerRole);

            /* admin reads collect and writes a config table. */
            await using (var admin = new NpgsqlConnection(adminString))
            {
                await admin.OpenAsync(ct);
                await ExecAsync(admin, "SELECT count(*) FROM wait_stats", ct);
                await ExecAsync(admin,
                    "INSERT INTO config_mute_rules (id, enabled, created_at_utc) VALUES ('sec-split-admin', true, now())", ct);
                await ExecAsync(admin, "DELETE FROM config_mute_rules WHERE id = 'sec-split-admin'", ct);
            }

            /* viewer reads collect but is DENIED the same config write — 42501. */
            await using (var viewer = new NpgsqlConnection(viewerString))
            {
                await viewer.OpenAsync(ct);
                await ExecAsync(viewer, "SELECT count(*) FROM wait_stats", ct);

                var denied = await Assert.ThrowsAsync<PostgresException>(async () =>
                    await ExecAsync(viewer,
                        "INSERT INTO config_mute_rules (id, enabled, created_at_utc) VALUES ('sec-split-viewer', true, now())", ct));
                Assert.Equal("42501", denied.SqlState); // insufficient_privilege
            }

            /* ALTER DEFAULT PRIVILEGES proof: a table created into collect AFTER the grants is
               readable by both roles with no explicit per-table grant. */
            await ExecAsync(owner, "CREATE TABLE IF NOT EXISTS collect.sec_split_newtable (id integer)", ct);
            try
            {
                await using var viewer = new NpgsqlConnection(viewerString);
                await viewer.OpenAsync(ct);
                await ExecAsync(viewer, "SELECT count(*) FROM collect.sec_split_newtable", ct);
            }
            finally
            {
                await ExecAsync(owner, "DROP TABLE IF EXISTS collect.sec_split_newtable", ct);
            }
        }
        finally
        {
            await DropTestRolesAsync(owner, ct);
        }
    }

    [Fact]
    public async Task CompressedHypertable_SetSchema_StaysReadableByLeastPrivilegeRole()
    {
        var connectionString = RequireLivePostgres();
        var ct = TestContext.Current.CancellationToken;

        await using var owner = new NpgsqlConnection(connectionString);
        await owner.OpenAsync(ct);
        await PgMigrations.MigrateAsync(owner, ct);

        Assert.SkipUnless(await TimescaleSupport.DetectAsync(owner, ct),
            "TimescaleDB is not installed in the DARLING_TEST_PG store — the compressed-hypertable move validation needs it.");

        await CreateTestRolesAndGrantsAsync(owner, ct);
        const string table = "sec_split_compress";
        try
        {
            /* Build a compressed hypertable in public, then move it to collect and confirm the least-
               privilege role still reads it THROUGH compression (the historically-missing propagation,
               fixed upstream — validated here on the pinned TimescaleDB). */
            await ExecAsync(owner, $"DROP TABLE IF EXISTS public.{table}, collect.{table}", ct);
            await ExecAsync(owner, $"CREATE TABLE public.{table} (server_id integer NOT NULL, collection_time timestamp NOT NULL, val integer)", ct);
            await ExecAsync(owner, $"SELECT create_hypertable('public.{table}', by_range('collection_time'))", ct);
            await ExecAsync(owner,
                $"INSERT INTO public.{table} SELECT 1, now() - (n || ' days')::interval, n FROM generate_series(1, 40) n", ct);
            await ExecAsync(owner, $"ALTER TABLE public.{table} SET (timescaledb.compress, timescaledb.compress_segmentby = 'server_id')", ct);

            /* Force at least one chunk to compress before the move (older_than covers the old rows). */
            await ExecAsync(owner,
                $"SELECT compress_chunk(c) FROM show_chunks('public.{table}', older_than => INTERVAL '7 days') c", ct);

            /* Grant the least-privilege role SELECT on the hypertable parent (propagates to chunks incl.
               the compressed half), then perform the schema move on the compressed hypertable. */
            await ExecAsync(owner, $"GRANT SELECT ON public.{table} TO {ViewerRole}", ct);
            await ExecAsync(owner, $"ALTER TABLE public.{table} SET SCHEMA collect", ct);

            Assert.Equal("collect", await SchemaOfAsync(owner, table, ct));

            /* The move preserved the rows, and the least-privilege role reads them through compression. */
            await using var viewer = new NpgsqlConnection(RoleConnectionString(connectionString, ViewerRole));
            await viewer.OpenAsync(ct);
            using var count = new NpgsqlCommand($"SELECT count(*) FROM collect.{table}", viewer);
            Assert.Equal(40L, Convert.ToInt64(await count.ExecuteScalarAsync(ct)));
        }
        finally
        {
            await ExecAsync(owner, $"DROP TABLE IF EXISTS public.{table}, collect.{table}", ct);
            await DropTestRolesAsync(owner, ct);
        }
    }

    private static async Task CreateTestRolesAndGrantsAsync(NpgsqlConnection owner, System.Threading.CancellationToken ct)
    {
        /* Mirrors the DarlingManagedRoles grant model with distinct, disposable role names and no
           database-level REVOKE (so the test is independent of the store's database name). */
        var ddl = $@"
DO $do$
BEGIN
   IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AdminRole}') THEN
      CREATE ROLE {AdminRole} LOGIN NOSUPERUSER PASSWORD '{RolePassword}';
   END IF;
   IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{ViewerRole}') THEN
      CREATE ROLE {ViewerRole} LOGIN NOSUPERUSER PASSWORD '{RolePassword}';
   END IF;
END $do$;
GRANT USAGE ON SCHEMA collect, config TO {AdminRole}, {ViewerRole};
GRANT SELECT ON ALL TABLES IN SCHEMA collect TO {AdminRole}, {ViewerRole};
GRANT SELECT ON ALL TABLES IN SCHEMA config  TO {AdminRole}, {ViewerRole};
GRANT INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA config TO {AdminRole};
ALTER DEFAULT PRIVILEGES FOR ROLE {OwnerRoleOf(owner)} IN SCHEMA collect GRANT SELECT ON TABLES TO {AdminRole}, {ViewerRole};";
        await ExecAsync(owner, ddl, ct);
    }

    /// <summary>ALTER DEFAULT PRIVILEGES keys on the role that CREATEs the object — here the connected owner.</summary>
    private static string OwnerRoleOf(NpgsqlConnection owner)
        => new NpgsqlConnectionStringBuilder(owner.ConnectionString).Username ?? "darling";

    private static async Task DropTestRolesAsync(NpgsqlConnection owner, System.Threading.CancellationToken ct)
    {
        /* Revoke first so DROP ROLE doesn't fail on dependent grants; ignore cleanup errors. */
        try
        {
            var db = owner.Database;
            await ExecAsync(owner, $@"
REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA collect FROM {AdminRole}, {ViewerRole};
REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA config  FROM {AdminRole}, {ViewerRole};
REVOKE ALL PRIVILEGES ON SCHEMA collect, config FROM {AdminRole}, {ViewerRole};
ALTER DEFAULT PRIVILEGES FOR ROLE {OwnerRoleOf(owner)} IN SCHEMA collect REVOKE SELECT ON TABLES FROM {AdminRole}, {ViewerRole};
DROP ROLE IF EXISTS {AdminRole};
DROP ROLE IF EXISTS {ViewerRole};", ct);
        }
        catch (PostgresException)
        {
            /* Best-effort cleanup — a leftover disposable role in a dev store is harmless. */
        }
    }

    private static string RoleConnectionString(string baseConnectionString, string role)
        => new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Username = role,
            Password = RolePassword,
            SearchPath = "collect,config,public",
            Pooling = false,
        }.ConnectionString;

    private static async Task<string?> SchemaOfAsync(NpgsqlConnection connection, string table, System.Threading.CancellationToken ct)
    {
        using var command = new NpgsqlCommand(
            "SELECT n.nspname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relname = $1", connection);
        command.Parameters.AddWithValue(table);
        return (string?)await command.ExecuteScalarAsync(ct);
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, System.Threading.CancellationToken ct)
    {
        using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}

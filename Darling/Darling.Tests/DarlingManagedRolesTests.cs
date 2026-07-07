/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The least-privilege role provisioning DDL (V8 security hardening, #1262). Ungated: the
/// generated statements' shape — DO-guarded idempotent CREATE ROLE, the ALTER ROLE password
/// re-assert, the collect/config grants, the config-only writes, the ALTER DEFAULT PRIVILEGES that
/// auto-grant new tables, the public hardening + the admin/viewer CONNECT re-grant — plus the
/// alphanumeric-password safety guard. The live round-trip (roles actually created, admin writes /
/// viewer denied, default-privileges proof) is in the gated <see cref="DarlingSecuritySplitLiveTests"/>.
/// </summary>
public sealed class DarlingManagedRolesTests
{
    [Fact]
    public void BuildProvisioningSql_CreatesRolesIdempotently_LoginNoSuperuser()
    {
        var sql = DarlingManagedRoles.BuildProvisioningSql("AdminPassword01", "ViewerPassword02");

        /* DO-guarded CREATE ROLE (no IF NOT EXISTS on CREATE ROLE) + a password re-assert every start. */
        Assert.Contains("FROM pg_roles WHERE rolname = 'admin'", sql, StringComparison.Ordinal);
        Assert.Contains("FROM pg_roles WHERE rolname = 'viewer'", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE ROLE admin LOGIN NOSUPERUSER PASSWORD 'AdminPassword01';", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE ROLE viewer LOGIN NOSUPERUSER PASSWORD 'ViewerPassword02';", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER ROLE admin  LOGIN NOSUPERUSER PASSWORD 'AdminPassword01';", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER ROLE viewer LOGIN NOSUPERUSER PASSWORD 'ViewerPassword02';", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProvisioningSql_StampsMarker_AndFailsLoudOnUnmarkedCollision()
    {
        var sql = DarlingManagedRoles.BuildProvisioningSql("AdminPassword01", "ViewerPassword02");

        Assert.Equal("darling-managed", DarlingManagedRoles.RoleMarker);

        /* Fresh roles are STAMPED with the marker comment. */
        Assert.Contains("COMMENT ON ROLE admin IS 'darling-managed';", sql, StringComparison.Ordinal);
        Assert.Contains("COMMENT ON ROLE viewer IS 'darling-managed';", sql, StringComparison.Ordinal);

        /* An existing same-named role is trusted only if it carries the marker (read via
           shobj_description); otherwise provisioning RAISEs rather than repurposing it. */
        Assert.Contains("shobj_description((SELECT oid FROM pg_roles WHERE rolname = 'admin'), 'pg_authid') IS DISTINCT FROM 'darling-managed'", sql, StringComparison.Ordinal);
        Assert.Contains("shobj_description((SELECT oid FROM pg_roles WHERE rolname = 'viewer'), 'pg_authid') IS DISTINCT FROM 'darling-managed'", sql, StringComparison.Ordinal);
        Assert.Contains("RAISE EXCEPTION 'Role \"admin\" already exists and was not created by Darling", sql, StringComparison.Ordinal);
        Assert.Contains("RAISE EXCEPTION 'Role \"viewer\" already exists and was not created by Darling", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProvisioningSql_GrantsReadBothSchemas_WritesConfigForAdminOnly()
    {
        var sql = DarlingManagedRoles.BuildProvisioningSql("AdminPassword01", "ViewerPassword02");

        Assert.Contains("GRANT USAGE ON SCHEMA collect, config TO admin, viewer;", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON ALL TABLES IN SCHEMA collect TO admin, viewer;", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON ALL TABLES IN SCHEMA config  TO admin, viewer;", sql, StringComparison.Ordinal);

        /* config writes are admin-only — viewer never gets INSERT/UPDATE/DELETE. */
        Assert.Contains("GRANT INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA config TO admin;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA config TO admin, viewer", sql, StringComparison.Ordinal);

        /* collect is read-only to everyone but the owner — no write grant on it at all. */
        Assert.DoesNotContain("INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA collect", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProvisioningSql_DefaultPrivileges_AutoGrantNewTables()
    {
        var sql = DarlingManagedRoles.BuildProvisioningSql("AdminPassword01", "ViewerPassword02");

        /* New collector tables (created bare into collect via search_path) auto-inherit SELECT. */
        Assert.Contains("ALTER DEFAULT PRIVILEGES FOR ROLE darling IN SCHEMA collect", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON TABLES TO admin, viewer;", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER DEFAULT PRIVILEGES FOR ROLE darling IN SCHEMA config", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT INSERT, UPDATE, DELETE ON TABLES TO admin;", sql, StringComparison.Ordinal);

        /* Fail-closed: a future serial/identity config column needs sequence USAGE for admin's INSERT. */
        Assert.Contains("GRANT USAGE, SELECT ON SEQUENCES TO admin;", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProvisioningSql_CarvesViewerSecretColumns_FailClosed()
    {
        var sql = DarlingManagedRoles.BuildProvisioningSql("AdminPassword01", "ViewerPassword02");

        /* The blanket config SELECT to viewer is still present (covers the non-secret config tables), but
           the three credential-bearing tables are then carved to column-level for viewer. */
        Assert.Contains("GRANT SELECT ON ALL TABLES IN SCHEMA config  TO admin, viewer;", sql, StringComparison.Ordinal);

        foreach (var acl in DarlingManagedRoles.ViewerRestrictedConfigTables)
        {
            /* viewer's table-wide SELECT is dropped, then re-granted on ONLY the non-secret columns. */
            Assert.Contains($"REVOKE SELECT ON config.{acl.Table} FROM viewer;", sql, StringComparison.Ordinal);
            Assert.Contains($"GRANT SELECT ({string.Join(", ", acl.NonSecretColumns)}) ON config.{acl.Table} TO viewer;",
                sql, StringComparison.Ordinal);

            /* The carve is viewer-only — admin is never column-restricted (it writes these tables). */
            Assert.DoesNotContain($"REVOKE SELECT ON config.{acl.Table} FROM admin", sql, StringComparison.Ordinal);
        }

        /* No secret column name appears anywhere in a GRANT ... TO viewer (belt over the per-table check). */
        var viewerGrantLines = sql.Split('\n')
            .Where(line => line.Contains("TO viewer", StringComparison.Ordinal) && line.Contains("GRANT SELECT (", StringComparison.Ordinal));
        foreach (var secret in DarlingManagedRoles.ViewerRestrictedConfigTables.SelectMany(a => a.SecretColumns))
        {
            foreach (var line in viewerGrantLines)
            {
                Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ViewerRestrictedConfigTables_SecretAndNonSecretColumns_AreDisjointAndNonEmpty()
    {
        var expectedSecrets = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["config_monitored_servers"] = new[] { "encrypted_password" },
            ["config_command"] = new[] { "args_json" },
            ["config_notification"] = new[] { "smtp_encrypted_password", "smtp_username", "teams_url", "slack_url" },
        };

        /* Exactly the three known secret-bearing tables, with the expected secret columns. */
        Assert.Equal(
            expectedSecrets.Keys.OrderBy(k => k, StringComparer.Ordinal),
            DarlingManagedRoles.ViewerRestrictedConfigTables.Select(a => a.Table).OrderBy(k => k, StringComparer.Ordinal));

        foreach (var acl in DarlingManagedRoles.ViewerRestrictedConfigTables)
        {
            Assert.NotEmpty(acl.NonSecretColumns);
            Assert.NotEmpty(acl.SecretColumns);

            /* A column is never in both sets — a granted column can't also be a denied one. */
            Assert.Empty(acl.NonSecretColumns.Intersect(acl.SecretColumns, StringComparer.Ordinal));

            Assert.Equal(
                expectedSecrets[acl.Table].OrderBy(c => c, StringComparer.Ordinal),
                acl.SecretColumns.OrderBy(c => c, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void BuildViewerColumnAclSql_UsesTheGivenSchemaAndRole()
    {
        /* The same generator the live test applies to its throwaway roles — pin that it honors the passed
           schema + role names (so the test and the real provisioning can never drift). */
        var sql = DarlingManagedRoles.BuildViewerColumnAclSql("config", "sec_viewer_test");

        Assert.Contains("REVOKE SELECT ON config.config_monitored_servers FROM sec_viewer_test;", sql, StringComparison.Ordinal);
        Assert.Contains("ON config.config_monitored_servers TO sec_viewer_test;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("encrypted_password", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("args_json", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("teams_url", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProvisioningSql_HardensPublic_ButKeepsAdminViewerConnect()
    {
        var sql = DarlingManagedRoles.BuildProvisioningSql("AdminPassword01", "ViewerPassword02");

        Assert.Contains("REVOKE CREATE ON SCHEMA public FROM PUBLIC;", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL ON DATABASE darling FROM PUBLIC;", sql, StringComparison.Ordinal);

        /* REVOKE ALL drops PUBLIC's implicit CONNECT, so admin/viewer must be re-granted it, or the
           Viewer could no longer connect — the correctness fix over the design's literal DDL. */
        Assert.Contains("GRANT CONNECT ON DATABASE darling TO admin, viewer;", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("semi;colon")]
    [InlineData("quote'here")]
    [InlineData("dash-dash")]
    [InlineData("")]
    public void BuildProvisioningSql_RejectsNonAlphanumericPassword(string badPassword)
    {
        /* Passwords are interpolated into DDL literals; the alnum guard fails closed if the
           generator's alphabet is ever widened without switching to quote_literal. */
        Assert.Throws<ArgumentException>(() => DarlingManagedRoles.BuildProvisioningSql(badPassword, "ViewerPassword02"));
        Assert.Throws<ArgumentException>(() => DarlingManagedRoles.BuildProvisioningSql("AdminPassword01", badPassword));
    }

    [Fact]
    public void BuildProvisioningSql_AcceptsGeneratedPasswords()
    {
        /* The real generated passwords (32-char alnum) pass the guard and inject cleanly. */
        var admin = DarlingManagedPostgres.GeneratePassword();
        var viewer = DarlingManagedPostgres.GeneratePassword();

        var sql = DarlingManagedRoles.BuildProvisioningSql(admin, viewer);

        Assert.Contains($"PASSWORD '{admin}'", sql, StringComparison.Ordinal);
        Assert.Contains($"PASSWORD '{viewer}'", sql, StringComparison.Ordinal);
    }
}

/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using PerformanceMonitor.Darling.Storage;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Least-privilege role provisioning for the managed store (V8 security hardening, #1262) — the
/// conf-append discipline of <see cref="DarlingManagedPostgres"/> applied to roles and credentials.
/// The service connects as the bootstrap superuser <c>darling</c> (it does DDL: migrations,
/// hypertable conversion, retention), and it provisions two least-privilege LOGIN roles the
/// interactive Viewer connects as instead of the superuser:
/// <list type="bullet">
/// <item><b><c>admin</c></b> — SELECT on both schemas + INSERT/UPDATE/DELETE on <c>config</c> only.
/// The Viewer's default identity: it owns the alert-dismiss, mute-rule, and analysis-mute writes but
/// can never DROP, alter schema, touch <c>collect</c> data, or create objects.</item>
/// <item><b><c>viewer</c></b> — SELECT on both schemas, no writes anywhere. A locked-down
/// deployment points the Viewer at this ("look but don't touch").</item>
/// </list>
///
/// <para>On every managed startup (after migration, before TimescaleDB conversion), for each role:
/// read its DPAPI-LocalMachine credential file beside the data directory, or GENERATE one if missing
/// (self-heal — a superuser can always <c>ALTER ROLE … PASSWORD</c>, so a deleted file just
/// regenerates, a nicer property than the owner's unrecoverable password). Then run the idempotent
/// provisioning DDL with the passwords injected: <c>DO</c>-guarded <c>CREATE ROLE</c>, an
/// <c>ALTER ROLE … PASSWORD</c> re-assert so role and file never drift, and the
/// <c>GRANT</c>/<c>ALTER DEFAULT PRIVILEGES</c> that make new collector tables auto-inherit SELECT.
/// Every statement is idempotent, so re-running each start converges — no version stamp, existence
/// checks drive it exactly as <see cref="DarlingManagedPostgres.EnsureConfAppended"/> uses its conf
/// marker.</para>
///
/// <para>Windows-only (the DPAPI credential files), like every DPAPI surface here. Bring-your-own
/// Postgres provisions the same roles out-of-band via <c>Darling/tools/provision-roles.sql</c>.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class DarlingManagedRoles
{
    /// <summary>
    /// The comment stamped on every Darling-created login role (<c>COMMENT ON ROLE … IS</c>, read back
    /// via <c>shobj_description(oid, 'pg_authid')</c>). Because the role names are the bare, un-prefixed
    /// <c>admin</c>/<c>viewer</c> (Erik's decision — no <c>darling_</c> namespace), provisioning must not
    /// silently repurpose a same-named role someone else created: an existing role WITHOUT this marker
    /// makes provisioning fail loud rather than reset its password/privileges.
    /// </summary>
    public const string RoleMarker = "darling-managed";

    /// <summary>
    /// Ensures the <c>admin</c>/<c>viewer</c> roles, their DPAPI credentials, and the collect/config
    /// grants exist and match — idempotent and self-healing. Opens one connection from the
    /// owner-<c>darling</c> data source (ALTER DEFAULT PRIVILEGES FOR ROLE darling only governs objects
    /// darling creates, which is all of them). Throws on a hard failure; the caller degrades (the
    /// Viewer cannot connect as admin/viewer until a later start succeeds) but keeps collecting.
    /// </summary>
    public static async Task EnsureProvisionedAsync(
        NpgsqlDataSource dataSource, string dataDirectory, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (dataSource is null)
        {
            throw new ArgumentNullException(nameof(dataSource));
        }

        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("Data directory is required.", nameof(dataDirectory));
        }

        var adminPassword = EnsureRoleCredential(
            DarlingManagedPostgres.AdminCredentialPathFor(dataDirectory), DarlingManagedPostgres.AdminRoleName, logger);
        var viewerPassword = EnsureRoleCredential(
            DarlingManagedPostgres.ViewerCredentialPathFor(dataDirectory), DarlingManagedPostgres.ViewerRoleName, logger);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(BuildProvisioningSql(adminPassword, viewerPassword), connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        logger.LogInformation(
            "Least-privilege roles ready (admin: read both schemas + write config; viewer: read-only) — the Viewer no longer connects as the superuser");
    }

    /// <summary>
    /// Reads a TRUSTED existing role credential, or generates + DPAPI-persists a fresh one (self-heal),
    /// then restricts its ACL. Same 32-char alnum <see cref="DarlingManagedPostgres.GeneratePassword"/>
    /// and DPAPI-LocalMachine posture as the owner credential; unlike the owner's, a role password can
    /// always be re-asserted (<c>ALTER ROLE … PASSWORD</c>), so an untrusted-owned (possibly pre-planted)
    /// file is discarded and regenerated rather than trusted.
    /// </summary>
    private static string EnsureRoleCredential(string credentialPath, string roleName, ILogger logger)
    {
        string password;
        if (File.Exists(credentialPath) && DarlingFileSecurity.IsTrustedOwner(credentialPath))
        {
            password = DarlingSecrets.Unprotect(File.ReadAllText(credentialPath).Trim());
        }
        else
        {
            if (File.Exists(credentialPath))
            {
                /* Pre-plant defense: a role credential owned by an arbitrary local user would feed the
                   caller's ALTER ROLE … PASSWORD re-assert a password the attacker chose. Discard it. */
                logger.LogWarning(
                    "The managed '{Role}' credential {File} is not owned by a trusted principal — discarding and regenerating it (possible pre-plant).",
                    roleName, Path.GetFileName(credentialPath));
                TryDelete(credentialPath, logger);
            }

            password = DarlingManagedPostgres.GeneratePassword();
            File.WriteAllText(credentialPath, DarlingSecrets.Protect(password));
            logger.LogInformation(
                "Generated the managed '{Role}' role credential ({File})", roleName, Path.GetFileName(credentialPath));
        }

        /* Re-harden every start (self-healing): the admin/viewer credentials are readable by SYSTEM +
           Administrators + the service account + the interactive operator (whose Viewer reads them). */
        TryHardenRoleCredential(credentialPath, logger);
        return password;
    }

    /// <summary>Best-effort restrictive ACL on a role credential; a failure is logged loud, not fatal.</summary>
    private static void TryHardenRoleCredential(string path, ILogger logger)
    {
        try
        {
            DarlingFileSecurity.HardenFile(path, allowInteractiveRead: true);
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Could not restrict the ACL on {Path} ({Message}) — the role credential may be readable by other local users; fix the file permissions by hand.",
                path, ex.Message);
        }
    }

    private static void TryDelete(string path, ILogger logger)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                "Could not delete the untrusted credential file {Path} ({Message}) — remove it by hand so a fresh one can be generated.",
                path, ex.Message);
        }
    }

    /// <summary>
    /// The idempotent, self-healing provisioning DDL with the role passwords injected. Passwords are
    /// alnum-only (<see cref="DarlingManagedPostgres.GeneratePassword"/>), verified here before the
    /// interpolation, so string-building the <c>PASSWORD '…'</c> literals is escaping-safe — the same
    /// reasoning <see cref="DarlingManagedPostgres"/> relies on for <c>--pwfile</c>. Public + parameter-free
    /// so a test can pin the shape without a live Postgres; shared with nothing else.
    /// </summary>
    public static string BuildProvisioningSql(string adminPassword, string viewerPassword)
    {
        RequireAlphanumeric(adminPassword, nameof(adminPassword));
        RequireAlphanumeric(viewerPassword, nameof(viewerPassword));

        const string owner = DarlingManagedPostgres.UserName;      // darling (owner/superuser)
        const string database = DarlingManagedPostgres.DatabaseName; // darling
        const string admin = DarlingManagedPostgres.AdminRoleName;
        const string viewer = DarlingManagedPostgres.ViewerRoleName;
        const string collect = PgSchemaGenerator.CollectSchema;
        const string config = PgSchemaGenerator.ConfigSchema;
        const string marker = RoleMarker;

        return $@"
/* Least-privilege roles for the Darling security split (#1262). Idempotent + self-healing:
   re-run every managed start, converging role state to the DPAPI credential files. */

-- 1. Roles (CREATE ROLE has no IF NOT EXISTS -> guard with a DO block). The names are bare
--    admin/viewer, so a fresh role is STAMPED with a marker comment and an existing SAME-NAMED role
--    is trusted only if it carries that marker; an unmarked collision fails loud (never repurposed).
DO $do$
BEGIN
   IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{admin}') THEN
      CREATE ROLE {admin} LOGIN NOSUPERUSER PASSWORD '{adminPassword}';
      COMMENT ON ROLE {admin} IS '{marker}';
   ELSIF shobj_description((SELECT oid FROM pg_roles WHERE rolname = '{admin}'), 'pg_authid') IS DISTINCT FROM '{marker}' THEN
      RAISE EXCEPTION 'Role ""{admin}"" already exists and was not created by Darling (missing the ''{marker}'' marker comment). Rename or drop it before provisioning so Darling does not repurpose an unrelated login.';
   END IF;

   IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{viewer}') THEN
      CREATE ROLE {viewer} LOGIN NOSUPERUSER PASSWORD '{viewerPassword}';
      COMMENT ON ROLE {viewer} IS '{marker}';
   ELSIF shobj_description((SELECT oid FROM pg_roles WHERE rolname = '{viewer}'), 'pg_authid') IS DISTINCT FROM '{marker}' THEN
      RAISE EXCEPTION 'Role ""{viewer}"" already exists and was not created by Darling (missing the ''{marker}'' marker comment). Rename or drop it before provisioning so Darling does not repurpose an unrelated login.';
   END IF;
END $do$;

-- 1b. Re-assert password + attributes every start (the credential file is the source of truth).
--     Only reached when the guard above passed (fresh + marked, or already Darling-marked).
ALTER ROLE {admin}  LOGIN NOSUPERUSER PASSWORD '{adminPassword}';
ALTER ROLE {viewer} LOGIN NOSUPERUSER PASSWORD '{viewerPassword}';

-- 2. Schema usage + SELECT everywhere (ALL TABLES covers tables AND views).
GRANT USAGE ON SCHEMA {collect}, {config} TO {admin}, {viewer};
GRANT SELECT ON ALL TABLES IN SCHEMA {collect} TO {admin}, {viewer};
GRANT SELECT ON ALL TABLES IN SCHEMA {config}  TO {admin}, {viewer};

-- 3. config writes -- admin only.
GRANT INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {config} TO {admin};

-- 4. Default privileges so NEW tables/views auto-inherit (no per-table-grant foot-gun).
ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {collect}
   GRANT SELECT ON TABLES TO {admin}, {viewer};
ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {config}
   GRANT SELECT ON TABLES TO {admin}, {viewer};
ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {config}
   GRANT INSERT, UPDATE, DELETE ON TABLES TO {admin};
-- Fail-closed: today no config table has a sequence (ids are app-generated / text), but a future
-- serial/identity column would give admin INSERT with no sequence USAGE -> the write breaks. Grant it now.
ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {config}
   GRANT USAGE, SELECT ON SEQUENCES TO {admin};

-- 5. Public hardening: no world-writable public schema, no anonymous connect. The REVOKE ALL drops
--    PUBLIC's implicit CONNECT, so admin/viewer are re-granted CONNECT explicitly (darling is
--    superuser + owner and never needs it).
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON DATABASE {database} FROM PUBLIC;
GRANT CONNECT ON DATABASE {database} TO {admin}, {viewer};
";
    }

    /// <summary>
    /// The generated passwords are alnum by construction; this fails closed if that ever changes,
    /// because the passwords are string-interpolated into DDL literals (belt: <c>quote_literal</c> if
    /// the alphabet is ever widened).
    /// </summary>
    private static void RequireAlphanumeric(string password, string parameterName)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Role password must not be empty.", parameterName);
        }

        foreach (var c in password)
        {
            if (!char.IsLetterOrDigit(c) || c > 127)
            {
                throw new ArgumentException(
                    "Role password must be ASCII alphanumeric (it is interpolated into DDL); use DarlingManagedPostgres.GeneratePassword.",
                    parameterName);
            }
        }
    }
}

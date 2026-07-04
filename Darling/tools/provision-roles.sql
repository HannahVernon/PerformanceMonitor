-- ============================================================================================
-- Darling security hardening (#1262) — least-privilege role provisioning for BRING-YOUR-OWN
-- PostgreSQL (postgres.managed = false).
--
-- In managed mode the service provisions these roles automatically on every start (see
-- DarlingManagedRoles) and generates their DPAPI credentials. In BYO mode YOU run this script
-- once, as the database OWNER (the role that owns the Darling store — the one your
-- postgres.connectionString connects as for collection). It creates the two least-privilege
-- login roles the Viewer connects as instead of the owner:
--
--   admin   -- reads both schemas + writes the operator-config tables (mute rules, alert
--              dismissals, analysis mutes). The Viewer's default identity (darling.json
--              postgres.connectAs = "admin").
--   viewer  -- reads both schemas, writes nothing. Point a locked-down Viewer at this with
--              postgres.connectAs = "viewer"; its write actions degrade gracefully.
--
-- PREREQUISITE: the V8 migration (which the service applies on startup) must already have created
-- the collect and config schemas and moved the tables into them. Run this AFTER the service has
-- started at least once against your store.
--
-- BEFORE RUNNING:
--   1. Replace CHANGE_ME_ADMIN_PASSWORD and CHANGE_ME_VIEWER_PASSWORD with strong passwords.
--   2. If your database is not named "darling", change it in the REVOKE/GRANT ... ON DATABASE
--      lines and in the ALTER DATABASE note at the bottom.
--   3. If the owner role is not "darling", change it in the ALTER DEFAULT PRIVILEGES FOR ROLE
--      lines (it must be the role that CREATEs the tables — your collection connection's role).
--
--   psql -h <host> -U <owner> -d darling -f provision-roles.sql
--
-- NAME-COLLISION SAFETY: the roles are the bare, un-prefixed names "admin" and "viewer". If your
-- cluster ALREADY has a role by either name that this script did not create, it will NOT be silently
-- repurposed: fresh roles are stamped with a marker comment ('darling-managed'), and an existing
-- same-named role without that marker makes this script FAIL LOUD (rename or drop the other role
-- first, or use a dedicated cluster/database for the Darling store).
-- ============================================================================================

-- 1. Roles (CREATE ROLE has no IF NOT EXISTS -> guard with a DO block). Idempotent: re-running
--    this script re-asserts the password below, so it doubles as a password rotation. A fresh role
--    is stamped 'darling-managed'; an unmarked same-named role fails loud (never repurposed).
DO $$
BEGIN
   IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'admin') THEN
      CREATE ROLE admin LOGIN NOSUPERUSER PASSWORD 'CHANGE_ME_ADMIN_PASSWORD';
      COMMENT ON ROLE admin IS 'darling-managed';
   ELSIF shobj_description((SELECT oid FROM pg_roles WHERE rolname = 'admin'), 'pg_authid') IS DISTINCT FROM 'darling-managed' THEN
      RAISE EXCEPTION 'Role "admin" already exists and was not created by Darling (missing the ''darling-managed'' marker comment). Rename or drop it before provisioning so Darling does not repurpose an unrelated login.';
   END IF;
   IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'viewer') THEN
      CREATE ROLE viewer LOGIN NOSUPERUSER PASSWORD 'CHANGE_ME_VIEWER_PASSWORD';
      COMMENT ON ROLE viewer IS 'darling-managed';
   ELSIF shobj_description((SELECT oid FROM pg_roles WHERE rolname = 'viewer'), 'pg_authid') IS DISTINCT FROM 'darling-managed' THEN
      RAISE EXCEPTION 'Role "viewer" already exists and was not created by Darling (missing the ''darling-managed'' marker comment). Rename or drop it before provisioning so Darling does not repurpose an unrelated login.';
   END IF;
END $$;

ALTER ROLE admin  LOGIN NOSUPERUSER PASSWORD 'CHANGE_ME_ADMIN_PASSWORD';
ALTER ROLE viewer LOGIN NOSUPERUSER PASSWORD 'CHANGE_ME_VIEWER_PASSWORD';

-- 2. Schema usage + SELECT on everything that exists now (ALL TABLES covers tables AND views).
GRANT USAGE ON SCHEMA collect, config TO admin, viewer;
GRANT SELECT ON ALL TABLES IN SCHEMA collect TO admin, viewer;
GRANT SELECT ON ALL TABLES IN SCHEMA config  TO admin, viewer;

-- 3. config writes -- admin only.
GRANT INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA config TO admin;

-- 4. Default privileges so NEW tables/views (future collectors, created bare into collect via
--    search_path) auto-inherit SELECT. FOR ROLE <owner> must name the role that creates them.
ALTER DEFAULT PRIVILEGES FOR ROLE darling IN SCHEMA collect
   GRANT SELECT ON TABLES TO admin, viewer;
ALTER DEFAULT PRIVILEGES FOR ROLE darling IN SCHEMA config
   GRANT SELECT ON TABLES TO admin, viewer;
ALTER DEFAULT PRIVILEGES FOR ROLE darling IN SCHEMA config
   GRANT INSERT, UPDATE, DELETE ON TABLES TO admin;
-- Fail-closed for a future serial/identity config column: INSERT needs sequence USAGE too.
ALTER DEFAULT PRIVILEGES FOR ROLE darling IN SCHEMA config
   GRANT USAGE, SELECT ON SEQUENCES TO admin;

-- 5. Public hardening: no world-writable public schema, no anonymous connect. REVOKE ALL drops
--    PUBLIC's implicit CONNECT, so admin/viewer are re-granted CONNECT explicitly.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON DATABASE darling FROM PUBLIC;
GRANT CONNECT ON DATABASE darling TO admin, viewer;

-- 6. Search path. The service best-effort runs this on every start, but if your collection login
--    lacks the database-owner privilege it needs, run it once yourself as the owner so every
--    connection (the Viewer, psql, pg_dump) resolves the bare table names to collect/config:
--
--       ALTER DATABASE darling SET search_path = collect, config, public;

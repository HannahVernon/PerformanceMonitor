/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PerformanceMonitorDashboard.Services.Remediation;

namespace PerformanceMonitorDashboard.Services
{
    /// <summary>
    /// Narrow, purpose-built remediation execution methods. There is no general
    /// SQL-exec API here: the IDs are always typed BigInt parameters and the
    /// target database is applied ONLY as the connection's InitialCatalog, built
    /// solely through <see cref="SqlConnectionStringBuilder"/> (never concatenated).
    ///
    /// <para>
    /// These methods run under the EXISTING per-server monitoring connection
    /// (<see cref="DatabaseService.ConnectionString"/>), retargeted to the user
    /// database by catalog only. There is no elevated path: <c>UserID</c>/
    /// <c>Password</c> are never set from any prompt and no credential is captured.
    /// They are <c>internal</c> and reachable only through
    /// <see cref="Remediation.DatabaseServiceRemediationExecutor"/>; nothing in the
    /// MCP or menu/command surface calls them (verified by a no-caller test).
    /// </para>
    /// </summary>
    public partial class DatabaseService
    {
        private const int RemediationCommandTimeoutSeconds = 30;

        /*
        sp_query_store_force_plan requires the standard ANSI SET options (it touches
        Query Store internals that error 1934 without them). Microsoft.Data.SqlClient
        opens connections with ARITHABORT OFF by default, so we set the full block
        explicitly right after opening, matching install/_template.sql. SET options are
        session-scoped, so they persist for the gate read AND the EXEC on the same
        connection — no re-open, R2-MOD-1 intact.
        */
        private const string RemediationSetOptions = @"
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;";

        private static async Task ApplySessionOptionsAsync(SqlConnection connection, CancellationToken ct)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = RemediationCommandTimeoutSeconds;
            command.CommandText = RemediationSetOptions;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Read-only display probe (advisory only). Opens its own retargeted
        /// connection; it is NOT the authoritative gate.
        /// </summary>
        internal async Task<TargetPreflight> PreflightForcePlanAsync(string database, long queryId, long planId, CancellationToken ct)
        {
            var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = database };
            using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ApplySessionOptionsAsync(connection, ct).ConfigureAwait(false);

            var gate = await ReadGateAsync(connection, queryId, planId, ct).ConfigureAwait(false);
            return new TargetPreflight
            {
                Database = database,
                QueryId = queryId,
                PlanId = planId,
                ExecutingLogin = gate.ExecutingLogin,
                CurrentDatabase = gate.CurrentDatabase,
                HasAlter = gate.HasAlter,
                QueryStoreState = gate.QueryStoreState,
                PlanPresent = gate.PlanPresent,
                IsForcedPlan = gate.IsForcedPlan,
                ForceFailureCount = gate.ForceFailureCount
            };
        }

        /// <summary>
        /// Self-gating force. R2-MOD-1: the gate (DB_NAME assert + ALTER check +
        /// freshness) and the <c>sp_query_store_force_plan</c> EXEC run on ONE open
        /// retargeted connection with no re-open between them. <see cref="ForcePlanOutcome.GateSpid"/>
        /// and <see cref="ForcePlanOutcome.ExecSpid"/> are emitted so a caller can
        /// prove the gate and the mutation shared the same SPID.
        /// </summary>
        internal Task<ForcePlanOutcome> ForcePlanAsync(string database, long queryId, long planId, RemediationIdentity identity, CancellationToken ct)
            => ForceOrUnforceAsync(database, queryId, planId, isUnforce: false, ct);

        /// <summary>Self-gating inverse of <see cref="ForcePlanAsync"/>.</summary>
        internal Task<ForcePlanOutcome> UnforcePlanAsync(string database, long queryId, long planId, RemediationIdentity identity, CancellationToken ct)
            => ForceOrUnforceAsync(database, queryId, planId, isUnforce: true, ct);

        private async Task<ForcePlanOutcome> ForceOrUnforceAsync(string database, long queryId, long planId, bool isUnforce, CancellationToken ct)
        {
            var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = database };

            // ONE connection for the whole gate + mutation. No re-open between the
            // gate read and the EXEC — this is what makes the gate unbypassable and
            // closes the preflight→apply TOCTOU (R2-MOD-1).
            using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ApplySessionOptionsAsync(connection, ct).ConfigureAwait(false);

            var gate = await ReadGateAsync(connection, queryId, planId, ct).ConfigureAwait(false);

            // (1) Correct-database assert (A5). Open with InitialCatalog normally
            // fails closed before here; this makes the wrong-DB boundary explicit.
            if (!string.Equals(gate.CurrentDatabase, database, StringComparison.Ordinal))
            {
                return Outcome(database, queryId, planId, RemediationStatus.Blocked, forced: false, gate,
                    $"Connected database '{gate.CurrentDatabase}' does not match target '{database}'; no change made.");
            }

            // (2) ALTER permission — fail closed with grant guidance, no elevation.
            if (!gate.HasAlter)
            {
                return Outcome(database, queryId, planId, RemediationStatus.PermissionDenied, forced: false, gate,
                    $"The monitoring login '{gate.ExecutingLogin}' lacks ALTER on '{database}'. " +
                    $"Map it into '{database}' (CREATE USER ... FOR LOGIN) and GRANT ALTER, or connect with a login that has it.");
            }

            // (3) Freshness — plan present, Query Store forceable, current force state.
            if (!gate.PlanPresent)
            {
                return Outcome(database, queryId, planId, RemediationStatus.Skipped, forced: false, gate,
                    "The plan/query is no longer present in Query Store — the suggestion may be superseded.");
            }
            if (!string.Equals(gate.QueryStoreState, "READ_WRITE", StringComparison.OrdinalIgnoreCase))
            {
                return Outcome(database, queryId, planId, RemediationStatus.Skipped, forced: false, gate,
                    $"Query Store is '{gate.QueryStoreState}' (not READ_WRITE) on '{database}' — forcing is not possible.");
            }

            if (!isUnforce && gate.IsForcedPlan)
            {
                return Outcome(database, queryId, planId, RemediationStatus.Skipped, forced: false, gate,
                    "Already forced to this plan — nothing to do.");
            }
            if (isUnforce && !gate.IsForcedPlan)
            {
                return Outcome(database, queryId, planId, RemediationStatus.Skipped, forced: false, gate,
                    "This plan is not currently forced — nothing to unforce.");
            }

            string? warning = null;
            if (!isUnforce && gate.ForceFailureCount > 0)
            {
                // Warn-and-proceed (the operator has confirmed the apply). Re-forcing
                // a failing plan may not help; the warning rides the outcome message.
                warning = $"Note: force_failure_count was {gate.ForceFailureCount}; re-forcing may not help.";
            }

            // Gate passed — issue the mutation on the SAME open connection.
            var proc = isUnforce ? "sys.sp_query_store_unforce_plan" : "sys.sp_query_store_force_plan";
            int? execSpid;
            try
            {
                using var exec = connection.CreateCommand();
                exec.CommandTimeout = RemediationCommandTimeoutSeconds;
                exec.CommandText = $"EXEC {proc} @query_id = @query_id, @plan_id = @plan_id; SELECT exec_spid = @@SPID;";
                exec.Parameters.Add(new SqlParameter("@query_id", SqlDbType.BigInt) { Value = queryId });
                exec.Parameters.Add(new SqlParameter("@plan_id", SqlDbType.BigInt) { Value = planId });
                var raw = await exec.ExecuteScalarAsync(ct).ConfigureAwait(false);
                execSpid = raw is null || raw is DBNull ? (int?)null : Convert.ToInt32(raw);
            }
            catch (SqlException ex)
            {
                var status = ex.Number is 297 or 15247 or 229 ? RemediationStatus.PermissionDenied : RemediationStatus.Error;
                return Outcome(database, queryId, planId, status, forced: false, gate,
                    $"sp_query_store_{(isUnforce ? "unforce" : "force")}_plan failed (error {ex.Number}): {ex.Message}");
            }

            var verb = isUnforce ? "Unforced" : "Forced";
            var message = warning is null ? $"{verb} plan {planId} for query {queryId}." : $"{verb} plan {planId} for query {queryId}. {warning}";
            return new ForcePlanOutcome
            {
                Database = database,
                QueryId = queryId,
                PlanId = planId,
                Status = RemediationStatus.Success,
                Forced = true,
                ExecutingLogin = gate.ExecutingLogin,
                Message = message,
                GateSpid = gate.Spid,
                ExecSpid = execSpid
            };
        }

        private static ForcePlanOutcome Outcome(string database, long queryId, long planId, RemediationStatus status, bool forced, GateReading gate, string message) => new()
        {
            Database = database,
            QueryId = queryId,
            PlanId = planId,
            Status = status,
            Forced = forced,
            ExecutingLogin = gate.ExecutingLogin,
            Message = message,
            GateSpid = gate.Spid,
            ExecSpid = null
        };

        /// <summary>
        /// One round-trip read of the per-target gate state on the supplied open
        /// connection. The IDs are typed BigInt parameters; nothing is concatenated.
        /// </summary>
        private static async Task<GateReading> ReadGateAsync(SqlConnection connection, long queryId, long planId, CancellationToken ct)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = RemediationCommandTimeoutSeconds;
            command.CommandText = @"
SELECT
    current_db = DB_NAME(),
    executing_login = SUSER_SNAME(),
    has_alter = HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER'),
    qs_state = (SELECT TOP (1) dqso.actual_state_desc FROM sys.database_query_store_options AS dqso),
    plan_present = CASE WHEN EXISTS (SELECT 1 FROM sys.query_store_plan AS qsp WHERE qsp.query_id = @query_id AND qsp.plan_id = @plan_id) THEN 1 ELSE 0 END,
    is_forced = ISNULL((SELECT TOP (1) CONVERT(int, qsp.is_forced_plan) FROM sys.query_store_plan AS qsp WHERE qsp.query_id = @query_id AND qsp.plan_id = @plan_id), 0),
    force_failure_count = ISNULL((SELECT TOP (1) qsp.force_failure_count FROM sys.query_store_plan AS qsp WHERE qsp.query_id = @query_id AND qsp.plan_id = @plan_id), 0),
    spid = @@SPID;";
            command.Parameters.Add(new SqlParameter("@query_id", SqlDbType.BigInt) { Value = queryId });
            command.Parameters.Add(new SqlParameter("@plan_id", SqlDbType.BigInt) { Value = planId });

            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return new GateReading();

            return new GateReading
            {
                CurrentDatabase = reader.IsDBNull(0) ? null : reader.GetString(0),
                ExecutingLogin = reader.IsDBNull(1) ? null : reader.GetString(1),
                HasAlter = !reader.IsDBNull(2) && reader.GetInt32(2) == 1,
                QueryStoreState = reader.IsDBNull(3) ? null : reader.GetString(3),
                PlanPresent = !reader.IsDBNull(4) && reader.GetInt32(4) == 1,
                IsForcedPlan = !reader.IsDBNull(5) && reader.GetInt32(5) == 1,
                ForceFailureCount = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                Spid = reader.IsDBNull(7) ? (int?)null : Convert.ToInt32(reader.GetInt16(7))
            };
        }

        private sealed class GateReading
        {
            public string? CurrentDatabase { get; init; }
            public string? ExecutingLogin { get; init; }
            public bool HasAlter { get; init; }
            public string? QueryStoreState { get; init; }
            public bool PlanPresent { get; init; }
            public bool IsForcedPlan { get; init; }
            public long ForceFailureCount { get; init; }
            public int? Spid { get; init; }
        }
    }
}

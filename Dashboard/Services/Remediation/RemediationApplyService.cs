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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Analysis;
using PerformanceMonitorDashboard.Interfaces;
using PerformanceMonitorDashboard.Models;
using PerformanceMonitorDashboard.Services;

namespace PerformanceMonitorDashboard.Services.Remediation
{
    /// <summary>
    /// The single, UI-agnostic entry point the Dashboard uses to apply / un-apply a
    /// remediation. It is the ONLY non-core caller of the privileged remediation
    /// machinery (registry / handler / executor) — the UI binds to this facade and
    /// never touches those types directly, which is what keeps "reachable only
    /// through the gate" both true and statically checkable.
    ///
    /// <para>
    /// The confirm gate lives INSIDE this service: <see cref="RunAsync"/> invokes the
    /// operator's confirm callback and calls the handler's privileged
    /// <c>ApplyAsync</c>/<c>UnapplyAsync</c> ONLY when that callback returns true.
    /// There is no auto-apply, no apply-on-load, and no batch-without-confirm path.
    /// The read-only preflight that drives the modal is display only; the handler /
    /// executor re-derive the authoritative gate on the mutating connection (PR-A).
    /// </para>
    /// </summary>
    public sealed class RemediationApplyService
    {
        private readonly ServerManager _serverManager;
        private readonly RemediationHandlerRegistry _registry;
        private readonly Func<ServerConnection, IRemediationExecutor> _executorFactory;
        private readonly Func<ServerConnection, CancellationToken, Task<AuditWriteFailureKind>> _auditFailureClassifier;

        /// <summary>
        /// Production constructor. Builds the v1 registry (force-plan only) and wires
        /// the executor + audit-failure classifier over the existing per-server
        /// monitoring connection (no elevation — the connection is reused as-is).
        /// </summary>
        public RemediationApplyService(ServerManager serverManager, ICredentialService credentialService)
        {
            _serverManager = serverManager ?? throw new ArgumentNullException(nameof(serverManager));
            if (credentialService is null) throw new ArgumentNullException(nameof(credentialService));

            _registry = new RemediationHandlerRegistry(new IRemediationHandler[] { new ForcePlanHandler() });
            _executorFactory = server =>
                new DatabaseServiceRemediationExecutor(new DatabaseService(server.GetConnectionString(credentialService)));
            _auditFailureClassifier = (server, ct) =>
                AuditWritabilityProbe.ClassifyAsync(server.GetConnectionString(credentialService), ct);
        }

        /// <summary>
        /// Test seam (InternalsVisibleTo Dashboard.Tests): inject a fake registry,
        /// executor factory, and audit-failure classifier. Routes through the exact
        /// same gated <see cref="RunAsync"/>, so it cannot bypass the confirm gate.
        /// </summary>
        internal RemediationApplyService(
            ServerManager serverManager,
            RemediationHandlerRegistry registry,
            Func<ServerConnection, IRemediationExecutor> executorFactory,
            Func<ServerConnection, CancellationToken, Task<AuditWriteFailureKind>>? auditFailureClassifier = null)
        {
            _serverManager = serverManager;
            _registry = registry;
            _executorFactory = executorFactory;
            _auditFailureClassifier = auditFailureClassifier
                ?? ((_, _) => Task.FromResult(AuditWriteFailureKind.Unknown));
        }

        /// <summary>
        /// Whether a registered handler exists for this fact key (one half of the
        /// UI's Apply-affordance gate; the other half is unambiguous server
        /// resolution). Null / unknown fact keys yield no Apply button.
        /// </summary>
        public bool HasHandlerFor(string? factKey) => _registry.TryGet(factKey) is not null;

        /// <summary>
        /// M3 fail-closed server resolution. GUID match first; on a miss (incl. the
        /// int-id fallback / legacy / empty ServerId) fall back to a UNIQUE
        /// ServerName match; ambiguous (&gt;1) or unresolved (0) yields no server and
        /// a reason for the disabled-Apply tooltip. Never silently picks a server.
        /// </summary>
        public ServerResolution ResolveServer(string? serverId, string serverName)
            => ResolveServer(serverId, serverName, _serverManager.GetAllServers());

        /// <summary>Pure resolution logic (unit-testable without a live ServerManager).</summary>
        public static ServerResolution ResolveServer(string? serverId, string serverName, IReadOnlyList<ServerConnection> allServers)
        {
            if (allServers is null)
                return new ServerResolution { Reason = "No servers are configured." };

            // 1. Exact GUID match (the normal path for alerts produced by the GUID resolver).
            if (!string.IsNullOrEmpty(serverId))
            {
                var byId = allServers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.Ordinal));
                if (byId is not null)
                    return new ServerResolution { Server = byId };
            }

            // 2/3. GUID miss (incl. the int-id fallback from MainWindow's notify-time
            // resolver, and legacy/empty ServerId) -> resolve by a UNIQUE ServerName.
            var byName = allServers
                .Where(s => string.Equals(s.ServerName, serverName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (byName.Count == 1)
                return new ServerResolution { Server = byName[0], ResolvedByName = true };

            // 4. Ambiguous or unresolved -> FAIL CLOSED. This is the wrong-server boundary.
            var reason = byName.Count > 1
                ? $"Cannot unambiguously resolve the source server for this alert: " +
                  $"{byName.Count} configured servers are named \"{serverName}\". Apply is disabled."
                : "Cannot resolve the source server for this alert (it may have been renamed or " +
                  "removed since the alert fired). Apply is disabled.";
            return new ServerResolution { Reason = reason };
        }

        /// <summary>
        /// Apply a remediation against a resolved server. Runs read-only preflight,
        /// presents the confirm request, and — ONLY if the operator confirms —
        /// invokes the privileged handler.
        /// </summary>
        public Task<RemediationRunReport> ApplyAsync(
            RemediationAction action,
            ServerConnection server,
            string? previewSql,
            string operatorIdentity,
            string? sourceAlertRef,
            Func<RemediationConfirmRequest, Task<bool>> confirm,
            CancellationToken ct)
            => RunAsync(action, server, previewSql, operatorIdentity, sourceAlertRef, confirm, isUnapply: false, ct);

        /// <summary>Un-apply (unforce) a previously applied remediation. Same gated shape as Apply.</summary>
        public Task<RemediationRunReport> UnapplyAsync(
            RemediationAction action,
            ServerConnection server,
            string operatorIdentity,
            string? sourceAlertRef,
            Func<RemediationConfirmRequest, Task<bool>> confirm,
            CancellationToken ct)
            => RunAsync(action, server, previewSql: null, operatorIdentity, sourceAlertRef, confirm, isUnapply: true, ct);

        private async Task<RemediationRunReport> RunAsync(
            RemediationAction action,
            ServerConnection server,
            string? previewSql,
            string operatorIdentity,
            string? sourceAlertRef,
            Func<RemediationConfirmRequest, Task<bool>> confirm,
            bool isUnapply,
            CancellationToken ct)
        {
            if (action is null) throw new ArgumentNullException(nameof(action));
            if (server is null) throw new ArgumentNullException(nameof(server));
            if (confirm is null) throw new ArgumentNullException(nameof(confirm));

            var handler = _registry.TryGet(action.FactKey);
            if (handler is null)
                return new RemediationRunReport { Status = RemediationRunStatus.NoHandler, IsUnapply = isUnapply };

            var exec = _executorFactory(server);

            // Read-only preflight: DISPLAY DRIVER ONLY. Populates the confirm modal's
            // per-target dispositions and executing identity. It is NOT the gate —
            // the handler/executor re-derive the authoritative gate (correct-DB +
            // ALTER + freshness) on the mutating connection (PR-A, R2-MOD-1).
            var preflight = await handler.PreflightAsync(action, exec, ct).ConfigureAwait(false);

            var preview = string.IsNullOrEmpty(previewSql)
                ? RenderPreview(action, isUnapply)
                : previewSql!;

            var request = BuildConfirmRequest(action, server, preview, operatorIdentity, isUnapply, preflight);

            // ── THE GATE ──────────────────────────────────────────────────────────
            // The privileged handler.ApplyAsync/UnapplyAsync below is reached ONLY
            // when the operator's confirm callback returns true. This is the single
            // sanctioned path to the executor; there is no automatic, on-load, or
            // batch-without-confirm route.
            var confirmed = await confirm(request).ConfigureAwait(false);
            if (!confirmed)
                return new RemediationRunReport { Status = RemediationRunStatus.NotConfirmed, IsUnapply = isUnapply };

            var identity = new RemediationIdentity(operatorIdentity, sourceAlertRef);
            var result = isUnapply
                ? await handler.UnapplyAsync(action, exec, identity, ct).ConfigureAwait(false)
                : await handler.ApplyAsync(action, exec, identity, ct).ConfigureAwait(false);

            var targets = new List<RemediationTargetReport>(result.Outcomes.Count);
            foreach (var o in result.Outcomes)
            {
                // LOW-2: only an applied-but-unlogged target needs the permanent-vs-
                // transient classification; everything else is None.
                var failureKind = AuditWriteFailureKind.None;
                if (o.AppliedButUnlogged)
                    failureKind = await _auditFailureClassifier(server, ct).ConfigureAwait(false);

                targets.Add(new RemediationTargetReport
                {
                    Database = o.Database,
                    QueryId = o.QueryId,
                    PlanId = o.PlanId,
                    Status = o.Status,
                    Message = o.Message,
                    AuditWritten = o.AuditWritten,
                    AppliedButUnlogged = o.AppliedButUnlogged,
                    AuditFailureKind = failureKind
                });
            }

            return new RemediationRunReport
            {
                Status = RemediationRunStatus.Ran,
                IsUnapply = isUnapply,
                Targets = targets
            };
        }

        private static RemediationConfirmRequest BuildConfirmRequest(
            RemediationAction action,
            ServerConnection server,
            string preview,
            string operatorIdentity,
            bool isUnapply,
            PreflightResult preflight)
        {
            // Preflight targets align 1:1 with action targets (the handler builds them
            // in order). Match by index; tolerate a short preflight list defensively.
            var confirmTargets = new List<RemediationConfirmTarget>(action.Targets.Count);
            for (var i = 0; i < action.Targets.Count; i++)
            {
                var t = action.Targets[i];
                var pf = i < preflight.Targets.Count ? preflight.Targets[i] : null;
                confirmTargets.Add(new RemediationConfirmTarget
                {
                    Database = t.Database,
                    QueryId = t.QueryId,
                    PlanId = t.PlanId,
                    RegressionFactor = t.RegressionFactor,
                    Disposition = pf?.Disposition ?? RemediationDisposition.Error,
                    DispositionMessage = pf?.Message
                });
            }

            var executingLogin = preflight.Targets
                .Select(p => p.ExecutingLogin)
                .FirstOrDefault(l => !string.IsNullOrEmpty(l));

            return new RemediationConfirmRequest
            {
                ServerDisplayName = string.IsNullOrEmpty(server.DisplayName) ? server.ServerName : server.DisplayName,
                IsUnapply = isUnapply,
                PreviewSql = preview,
                OperatorIdentity = operatorIdentity,
                ExecutingLogin = executingLogin,
                Targets = confirmTargets,
                AuditTableExists = preflight.AuditTableExists
            };
        }

        /// <summary>
        /// Canonical EXEC preview rendered from the typed targets — exactly the
        /// statement that will run (matches the audited generated_sql). Used for the
        /// un-apply modal, and as a fallback when no code-block preview is supplied.
        /// </summary>
        private static string RenderPreview(RemediationAction action, bool isUnapply)
        {
            var proc = isUnapply ? "sp_query_store_unforce_plan" : "sp_query_store_force_plan";
            var sb = new StringBuilder();
            foreach (var t in action.Targets)
            {
                sb.Append("-- [").Append(t.Database).Append("]\n");
                sb.Append("EXEC sys.").Append(proc)
                  .Append(" @query_id = ").Append(t.QueryId)
                  .Append(", @plan_id = ").Append(t.PlanId).Append(";\n");
            }
            return sb.ToString().TrimEnd('\n');
        }
    }
}

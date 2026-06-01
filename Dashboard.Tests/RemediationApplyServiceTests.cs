using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Analysis;
using PerformanceMonitorDashboard;
using PerformanceMonitorDashboard.Models;
using PerformanceMonitorDashboard.Services.Remediation;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// PR-B coverage for the gated Apply Fix orchestrator: the confirm gate (the
/// privileged handler is unreachable unless confirm() returns true), M3 fail-closed
/// server resolution, applied-but-unlogged permanent-vs-transient surfacing (LOW-2),
/// and the AlertDetailWindow view-model gating (CanApply).
/// </summary>
public class RemediationApplyServiceTests
{
    private static readonly ServerConnection Server =
        new() { Id = "11111111-1111-1111-1111-111111111111", ServerName = "SQL2022", DisplayName = "Prod SQL2022" };

    private static RemediationAction ForceAction(double regression = 7.5) =>
        new("PLAN_REGRESSION", "force", new List<ForcePlanTarget> { new("AdventureWorks", 4242, 17, RegressionFactor: regression) });

    private static RemediationApplyService BuildService(
        FakeExecutor exec,
        IRemediationHandler? handler = null,
        Func<ServerConnection, CancellationToken, Task<AuditWriteFailureKind>>? classifier = null)
    {
        var handlers = handler is null ? new[] { (IRemediationHandler)new ForcePlanHandler() } : new[] { handler };
        var registry = new RemediationHandlerRegistry(handlers);
        return new RemediationApplyService(serverManager: null!, registry, _ => exec, classifier);
    }

    private static RemediationApplyService BuildServiceNoHandler(FakeExecutor exec) =>
        new(serverManager: null!, new RemediationHandlerRegistry(Array.Empty<IRemediationHandler>()), _ => exec, null);

    // ── Confirm gate ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gate_ConfirmFalse_NeverReachesHandler()
    {
        var exec = new FakeExecutor();
        var service = BuildService(exec);

        var report = await service.ApplyAsync(
            ForceAction(), Server, previewSql: "preview", operatorIdentity: "DOM\\op", sourceAlertRef: "ref",
            confirm: _ => Task.FromResult(false), CancellationToken.None);

        Assert.Equal(RemediationRunStatus.NotConfirmed, report.Status);
        Assert.Empty(report.Targets);
        Assert.Equal(0, exec.ForceCalls);          // the privileged path was never entered
        Assert.Empty(exec.AuditRecords);
    }

    [Fact]
    public async Task Gate_ConfirmTrue_ReachesHandlerExactlyOnce()
    {
        var exec = new FakeExecutor();
        var confirmCalls = 0;
        var service = BuildService(exec);

        var report = await service.ApplyAsync(
            ForceAction(), Server, "preview", "DOM\\op", "ref",
            confirm: req => { confirmCalls++; return Task.FromResult(true); }, CancellationToken.None);

        Assert.Equal(1, confirmCalls);
        Assert.Equal(RemediationRunStatus.Ran, report.Status);
        Assert.Equal(1, exec.ForceCalls);
        Assert.Equal(RemediationStatus.Success, Assert.Single(report.Targets).Status);
    }

    [Fact]
    public async Task Gate_ConfirmRequest_CarriesServerRegressionAndCaveat()
    {
        var exec = new FakeExecutor();
        var service = BuildService(exec);
        RemediationConfirmRequest? captured = null;

        await service.ApplyAsync(ForceAction(regression: 9.0), Server, "preview", "DOM\\op", "ref",
            confirm: req => { captured = req; return Task.FromResult(false); }, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("Prod SQL2022", captured!.ServerDisplayName);
        Assert.Equal("preview", captured.PreviewSql);
        Assert.Equal(9.0, Assert.Single(captured.Targets).RegressionFactor);
        Assert.Contains("still the better choice", RemediationConfirmRequest.StillBetterCaveat);
    }

    [Fact]
    public async Task Apply_NoHandler_ReturnsNoHandler_NeverConfirms()
    {
        var exec = new FakeExecutor();
        var service = BuildServiceNoHandler(exec);
        var confirmed = false;

        var report = await service.ApplyAsync(ForceAction(), Server, "preview", "DOM\\op", "ref",
            confirm: _ => { confirmed = true; return Task.FromResult(true); }, CancellationToken.None);

        Assert.Equal(RemediationRunStatus.NoHandler, report.Status);
        Assert.False(confirmed);
        Assert.Equal(0, exec.ForceCalls);
    }

    [Fact]
    public async Task Unapply_ConfirmTrue_ReachesUnforce()
    {
        var exec = new FakeExecutor { PriorForce = true };
        var service = BuildService(exec);

        var report = await service.UnapplyAsync(ForceAction(), Server, "DOM\\op", "ref",
            confirm: _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(RemediationRunStatus.Ran, report.Status);
        Assert.True(report.IsUnapply);
        Assert.Equal(1, exec.UnforceCalls);
    }

    // ── Apply-only enforcement (m-1 / m-C): un-apply for SupportsUnapply==false ──

    [Fact]
    public async Task Unapply_HandlerDoesNotSupport_ShortCircuits_NeverConfirms_NeverReachesHandler()
    {
        var exec = new FakeExecutor();
        var handler = new DbConfigHandler();        // SupportsUnapply == false
        var service = new RemediationApplyService(serverManager: null!,
            new RemediationHandlerRegistry(new IRemediationHandler[] { handler }), _ => exec, null);

        var confirmed = false;
        var dbConfigAction = new RemediationAction("DB_CONFIG", "set",
            Array.Empty<ForcePlanTarget>(),
            new List<DbConfigTarget> { new("Foo", DbConfigSetting.AutoShrinkOff, "ON") });

        // Even if a future mis-wired caller invokes UnapplyAsync, it must fail SAFE
        // (clean report, no NotSupportedException) and never confirm or mutate.
        var report = await service.UnapplyAsync(dbConfigAction, Server, "DOM\\op", "ref",
            confirm: _ => { confirmed = true; return Task.FromResult(true); }, CancellationToken.None);

        Assert.Equal(RemediationRunStatus.UnapplyNotSupported, report.Status);
        Assert.True(report.IsUnapply);
        Assert.False(confirmed);                    // the gate was never even shown
        Assert.Equal(0, exec.SetDbCalls);
        Assert.Empty(exec.AuditRecords);
    }

    [Fact]
    public async Task Apply_DbConfig_ConfirmRequest_RendersDbConfigRows_NoQueryId()
    {
        var exec = new FakeExecutor();
        var service = new RemediationApplyService(serverManager: null!,
            new RemediationHandlerRegistry(new IRemediationHandler[] { new DbConfigHandler() }), _ => exec, null);
        RemediationConfirmRequest? captured = null;

        var action = new RemediationAction("DB_CONFIG", "set", Array.Empty<ForcePlanTarget>(),
            new List<DbConfigTarget> { new("Foo", DbConfigSetting.AutoShrinkOff, "ON") });

        await service.ApplyAsync(action, Server, previewSql: null, "DOM\\op", "ref",
            confirm: req => { captured = req; return Task.FromResult(false); }, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("DB_CONFIG", captured!.FactKey);
        var row = Assert.Single(captured.Targets);
        Assert.Contains("AUTO_SHRINK OFF", row.StatusTitle);
        Assert.True(captured.AnyActionable);        // a DB_CONFIG Ok is actionable
        // The fallback preview renders the ALTER statement, not sp_query_store_*.
        Assert.Contains("ALTER DATABASE [Foo] SET AUTO_SHRINK OFF;", captured.PreviewSql);
        Assert.DoesNotContain("sp_query_store", captured.PreviewSql);
    }

    [Fact]
    public async Task Apply_DbConfig_AllAlreadyDesired_NotActionable()
    {
        // Preflight all-AlreadyInDesiredState => AnyActionable false => Apply disabled.
        var exec = new FakeExecutor();   // PreflightDbConfigAsync returns Ok by default;
        // override via a handler driven by a preflight that's already-desired:
        var service = new RemediationApplyService(serverManager: null!,
            new RemediationHandlerRegistry(new IRemediationHandler[] { new DbConfigHandler() }),
            _ => new AlreadyDesiredExecutor(), null);
        RemediationConfirmRequest? captured = null;

        var action = new RemediationAction("DB_CONFIG", "set", Array.Empty<ForcePlanTarget>(),
            new List<DbConfigTarget> { new("Foo", DbConfigSetting.AutoShrinkOff, "OFF") });

        await service.ApplyAsync(action, Server, previewSql: null, "DOM\\op", "ref",
            confirm: req => { captured = req; return Task.FromResult(false); }, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.False(captured!.AnyActionable);
        Assert.Equal(RemediationDisposition.AlreadyInDesiredState, Assert.Single(captured.Targets).Disposition);
    }

    // ── LOW-2: applied-but-unlogged permanent vs transient ───────────────────────

    [Fact]
    public async Task Apply_AppliedButUnlogged_PermanentClassification_Surfaced()
    {
        var exec = new FakeExecutor { AuditWriteResult = false };   // force succeeds, audit INSERT fails
        var classifierCalls = 0;
        var service = BuildService(exec, classifier: (_, _) => { classifierCalls++; return Task.FromResult(AuditWriteFailureKind.Permanent); });

        var report = await service.ApplyAsync(ForceAction(), Server, "preview", "DOM\\op", "ref",
            confirm: _ => Task.FromResult(true), CancellationToken.None);

        var t = Assert.Single(report.Targets);
        Assert.True(t.AppliedButUnlogged);
        Assert.Equal(AuditWriteFailureKind.Permanent, t.AuditFailureKind);
        Assert.Equal(1, classifierCalls);
    }

    [Fact]
    public async Task Apply_AppliedButUnlogged_TransientClassification_Surfaced()
    {
        var exec = new FakeExecutor { AuditWriteResult = false };
        var service = BuildService(exec, classifier: (_, _) => Task.FromResult(AuditWriteFailureKind.Transient));

        var report = await service.ApplyAsync(ForceAction(), Server, "preview", "DOM\\op", "ref",
            confirm: _ => Task.FromResult(true), CancellationToken.None);

        var t = Assert.Single(report.Targets);
        Assert.True(t.AppliedButUnlogged);
        Assert.Equal(AuditWriteFailureKind.Transient, t.AuditFailureKind);
    }

    [Fact]
    public async Task Apply_Success_AuditWritten_DoesNotClassify()
    {
        var exec = new FakeExecutor { AuditWriteResult = true };
        var classifierCalls = 0;
        var service = BuildService(exec, classifier: (_, _) => { classifierCalls++; return Task.FromResult(AuditWriteFailureKind.Permanent); });

        var report = await service.ApplyAsync(ForceAction(), Server, "preview", "DOM\\op", "ref",
            confirm: _ => Task.FromResult(true), CancellationToken.None);

        var t = Assert.Single(report.Targets);
        Assert.False(t.AppliedButUnlogged);
        Assert.Equal(AuditWriteFailureKind.None, t.AuditFailureKind);
        Assert.Equal(0, classifierCalls);          // classifier only runs for an unlogged target
    }

    // ── M3 fail-closed server resolution ─────────────────────────────────────────

    private static List<ServerConnection> Servers(params (string id, string name)[] defs) =>
        defs.Select(d => new ServerConnection { Id = d.id, ServerName = d.name, DisplayName = d.name }).ToList();

    [Fact]
    public void Resolve_GuidMatch_Resolves()
    {
        var servers = Servers(("guid-a", "SQL2022"), ("guid-b", "SQL2019"));
        var r = RemediationApplyService.ResolveServer("guid-a", "SQL2022", servers);

        Assert.True(r.IsResolved);
        Assert.False(r.ResolvedByName);
        Assert.Equal("guid-a", r.Server!.Id);
    }

    [Fact]
    public void Resolve_IntIdFallback_GuidMiss_ResolvesByUniqueName()
    {
        // The notify-time resolver wrote the finding's int id ("3"); GetServerById misses.
        var servers = Servers(("guid-a", "SQL2022"), ("guid-b", "SQL2019"));
        var r = RemediationApplyService.ResolveServer("3", "SQL2022", servers);

        Assert.True(r.IsResolved);
        Assert.True(r.ResolvedByName);
        Assert.Equal("guid-a", r.Server!.Id);
    }

    [Fact]
    public void Resolve_EmptyServerId_ResolvesByUniqueName()
    {
        var servers = Servers(("guid-a", "SQL2022"));
        var r = RemediationApplyService.ResolveServer("", "SQL2022", servers);

        Assert.True(r.IsResolved);
        Assert.True(r.ResolvedByName);
    }

    [Fact]
    public void Resolve_AmbiguousByName_FailsClosed()
    {
        var servers = Servers(("guid-a", "SQL2022"), ("guid-b", "SQL2022"));
        var r = RemediationApplyService.ResolveServer("3", "SQL2022", servers);

        Assert.False(r.IsResolved);
        Assert.Null(r.Server);
        Assert.Contains("unambiguously", r.Reason);
    }

    [Fact]
    public void Resolve_Unresolved_FailsClosed()
    {
        var servers = Servers(("guid-a", "SQL2022"));
        var r = RemediationApplyService.ResolveServer("3", "GhostServer", servers);

        Assert.False(r.IsResolved);
        Assert.Null(r.Server);
        Assert.False(string.IsNullOrEmpty(r.Reason));
    }

    [Fact]
    public void HasHandlerFor_KnownAndUnknown()
    {
        var service = BuildService(new FakeExecutor());
        Assert.True(service.HasHandlerFor("PLAN_REGRESSION"));
        Assert.False(service.HasHandlerFor("PARAMETER_SENSITIVITY"));
        Assert.False(service.HasHandlerFor(null));
    }

    // ── AlertDetailWindow view-model gating (CanApply) ───────────────────────────

    [Fact]
    public void CanApply_RequiresKnownFix_ResolvedServer_AndNotBusy()
    {
        // Known fix + resolved server -> enabled.
        var enabled = new AlertDetailWindow.DetailItemView { ShowApply = true };
        enabled.SetServerResolved(true);
        Assert.True(enabled.CanApply);

        // Resolved-but-unknown-fix (ShowApply false) -> never enabled.
        var noFix = new AlertDetailWindow.DetailItemView { ShowApply = false };
        noFix.SetServerResolved(true);
        Assert.False(noFix.CanApply);

        // Known fix but server unresolved (M3) -> hard-disabled.
        var unresolved = new AlertDetailWindow.DetailItemView { ShowApply = true };
        unresolved.SetServerResolved(false);
        Assert.False(unresolved.CanApply);

        // Mid-run -> disabled.
        var busy = new AlertDetailWindow.DetailItemView { ShowApply = true };
        busy.SetServerResolved(true);
        busy.BeginRun("Applying…");
        Assert.False(busy.CanApply);
    }

    // ── Fake executor (PR-B-local) ───────────────────────────────────────────────

    private sealed class FakeExecutor : IRemediationExecutor
    {
        public bool AuditTableExists = true;
        public bool PriorForce = true;
        public bool AuditWriteResult = true;

        public int ForceCalls;
        public int UnforceCalls;
        public readonly List<RemediationAuditRecord> AuditRecords = new();

        public Task<TargetPreflight> PreflightForcePlanAsync(string database, long queryId, long planId, CancellationToken ct)
            => Task.FromResult(new TargetPreflight
            {
                Database = database, QueryId = queryId, PlanId = planId,
                CurrentDatabase = database, HasAlter = true, QueryStoreState = "READ_WRITE",
                PlanPresent = true, ExecutingLogin = "PerfMonLogin"
            });

        public Task<bool> AuditTableExistsAsync(CancellationToken ct) => Task.FromResult(AuditTableExists);
        public Task<bool> HasPriorForceAsync(string database, long queryId, long planId, CancellationToken ct) => Task.FromResult(PriorForce);

        public Task<ForcePlanOutcome> ForcePlanAsync(string database, long queryId, long planId, RemediationIdentity identity, CancellationToken ct)
        {
            ForceCalls++;
            return Task.FromResult(new ForcePlanOutcome
            {
                Database = database, QueryId = queryId, PlanId = planId,
                Status = RemediationStatus.Success, Forced = true, ExecutingLogin = "PerfMonLogin", GateSpid = 55, ExecSpid = 55
            });
        }

        public Task<ForcePlanOutcome> UnforcePlanAsync(string database, long queryId, long planId, RemediationIdentity identity, CancellationToken ct)
        {
            UnforceCalls++;
            return Task.FromResult(new ForcePlanOutcome
            {
                Database = database, QueryId = queryId, PlanId = planId,
                Status = RemediationStatus.Success, Forced = true, ExecutingLogin = "PerfMonLogin", GateSpid = 55, ExecSpid = 55
            });
        }

        public int SetDbCalls;

        public Task<DbConfigPreflight> PreflightDbConfigAsync(string database, DbConfigSetting setting, CancellationToken ct)
            => Task.FromResult(new DbConfigPreflight
            {
                Database = database, Setting = setting, DatabaseExists = true, HasAlter = true,
                AlreadyInDesiredState = false, ExecutingLogin = "PerfMonLogin", CurrentValue = "ON"
            });

        public Task<DbConfigOutcome> SetDatabaseOptionAsync(string database, DbConfigSetting setting, RemediationIdentity identity, CancellationToken ct)
        {
            SetDbCalls++;
            return Task.FromResult(new DbConfigOutcome
            {
                Database = database, Setting = setting, Status = RemediationStatus.Success, Applied = true,
                ExecutingLogin = "PerfMonLogin", PriorValue = "ON", GeneratedSql = "ALTER DATABASE [x] SET AUTO_SHRINK OFF;",
                GateSpid = 55, ExecSpid = 55
            });
        }

        public Task<bool> WriteAuditAsync(RemediationAuditRecord record, CancellationToken ct)
        {
            AuditRecords.Add(record);
            return Task.FromResult(AuditWriteResult);
        }
    }

    /// <summary>Executor whose DB-config preflight always reports already-in-desired-state.</summary>
    private sealed class AlreadyDesiredExecutor : IRemediationExecutor
    {
        public Task<TargetPreflight> PreflightForcePlanAsync(string database, long queryId, long planId, CancellationToken ct)
            => Task.FromResult(new TargetPreflight { Database = database });
        public Task<bool> AuditTableExistsAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<bool> HasPriorForceAsync(string database, long queryId, long planId, CancellationToken ct) => Task.FromResult(false);
        public Task<ForcePlanOutcome> ForcePlanAsync(string database, long queryId, long planId, RemediationIdentity identity, CancellationToken ct)
            => Task.FromResult(new ForcePlanOutcome { Database = database });
        public Task<ForcePlanOutcome> UnforcePlanAsync(string database, long queryId, long planId, RemediationIdentity identity, CancellationToken ct)
            => Task.FromResult(new ForcePlanOutcome { Database = database });
        public Task<DbConfigPreflight> PreflightDbConfigAsync(string database, DbConfigSetting setting, CancellationToken ct)
            => Task.FromResult(new DbConfigPreflight
            {
                Database = database, Setting = setting, DatabaseExists = true, HasAlter = true,
                AlreadyInDesiredState = true, ExecutingLogin = "PerfMonLogin", CurrentValue = "OFF"
            });
        public Task<DbConfigOutcome> SetDatabaseOptionAsync(string database, DbConfigSetting setting, RemediationIdentity identity, CancellationToken ct)
            => Task.FromResult(new DbConfigOutcome { Database = database, Setting = setting, Status = RemediationStatus.Skipped });
        public Task<bool> WriteAuditAsync(RemediationAuditRecord record, CancellationToken ct) => Task.FromResult(true);
    }
}

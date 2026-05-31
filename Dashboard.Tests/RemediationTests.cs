using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Analysis;
using PerformanceMonitorDashboard.Services.Remediation;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// PR-A coverage for the privileged "Apply Fix" core (no UI): the structured
/// extractor + render-stability, the self-gating handler against a faked executor
/// (fail-closed permission, audit-table hard block, freshness/skip dispositions,
/// per-target independence, gate re-derivation, applied-but-unlogged), un-apply
/// restriction, the registry, and the "no caller reaches the executor" guard.
/// The single-connection (R2-MOD-1) guarantee is an executor-level/real-server
/// concern (SPID equality) and cannot be proven against a faked executor.
/// </summary>
public class RemediationTests
{
    private static AnalysisFinding PlanRegressionFinding(List<object>? rows = null) => new()
    {
        ServerId = 1,
        ServerName = "TestServer",
        Category = "plan_regression",
        StoryPath = "PLAN_REGRESSION",
        StoryPathHash = "planreg000000001",
        RootFactKey = "PLAN_REGRESSION",
        DrillDown = new Dictionary<string, object>
        {
            ["regressed_queries"] = rows ?? new List<object>
            {
                new
                {
                    database = "AdventureWorks",
                    query_id = 4242L,
                    best_plan_id = 17L,
                    latest_plan_hash = "0xDEAD",
                    best_plan_hash = "0xBEEF",
                    latest_cpu_per_exec_us = 9000.0,
                    best_cpu_per_exec_us = 1200.0,
                    regression_factor = 7.5
                }
            }
        }
    };

    private static RemediationAction ForceAction(params ForcePlanTarget[] targets) =>
        new("PLAN_REGRESSION", "force", targets.ToList());

    private static ForcePlanTarget Target(string db = "AdventureWorks", long q = 4242, long p = 17) =>
        new(db, q, p);

    private static readonly RemediationIdentity Identity =
        new("TESTDOMAIN\\tester", "Analysis: plan_regression [abcd1234]");

    [Fact]
    public void Extract_SkipsRowsFailingGuards()
    {
        var finding = PlanRegressionFinding(new List<object>
        {
            new { database = "", query_id = 1L, best_plan_id = 1L },
            new { database = "Db", query_id = 0L, best_plan_id = 1L },
            new { database = "Db", query_id = 1L, best_plan_id = 0L },
            new { database = "Good", query_id = 5L, best_plan_id = 9L }
        });

        var targets = FactRemediation.ExtractPlanRegressionTargets(finding);

        var only = Assert.Single(targets);
        Assert.Equal("Good", only.Database);
        Assert.Equal(5, only.QueryId);
        Assert.Equal(9, only.PlanId);
    }

    [Fact]
    public void Extract_CapsAtFiveTargets()
    {
        var rows = Enumerable.Range(1, 8)
            .Select(i => (object)new { database = $"Db{i}", query_id = (long)i, best_plan_id = (long)(i + 100) })
            .ToList();

        var targets = FactRemediation.ExtractPlanRegressionTargets(PlanRegressionFinding(rows));

        Assert.Equal(5, targets.Count);
        Assert.Equal("Db1", targets[0].Database);
        Assert.Equal("Db5", targets[4].Database);
    }

    [Fact]
    public void BuildAction_PlanRegression_ProducesForceActionWithTargets()
    {
        var action = FactRemediation.BuildAction(PlanRegressionFinding());

        Assert.NotNull(action);
        Assert.Equal("PLAN_REGRESSION", action!.FactKey);
        Assert.Equal("force", action.Action);
        var t = Assert.Single(action.Targets);
        Assert.Equal("AdventureWorks", t.Database);
        Assert.Equal(4242, t.QueryId);
        Assert.Equal(17, t.PlanId);
        Assert.Equal(7.5, t.RegressionFactor);
    }

    [Fact]
    public void BuildAction_ParameterSensitivity_ReturnsNull()
    {
        var finding = PlanRegressionFinding();
        finding.RootFactKey = "PARAMETER_SENSITIVITY";
        Assert.Null(FactRemediation.BuildAction(finding));
    }

    [Fact]
    public void BuildAction_UnknownKeyOrNoTargets_ReturnsNull()
    {
        var unknown = PlanRegressionFinding();
        unknown.RootFactKey = "ZZZ_TEST";
        Assert.Null(FactRemediation.BuildAction(unknown));

        Assert.Null(FactRemediation.BuildAction(PlanRegressionFinding(new List<object>())));
    }

    [Fact]
    public void GenerateForFinding_RenderStability_ByteForByte()
    {
        const string expected =
            "-- Database: AdventureWorks\n" +
            "-- query_id = 4242, forcing plan_id = 17\n" +
            "--   latest plan hash: 0xDEAD (cpu/exec 9000 us)\n" +
            "--   best plan hash:   0xBEEF   (cpu/exec 1200 us)\n" +
            "--   regression factor: 7.5x\n" +
            "USE [AdventureWorks];\n" +
            "EXEC sys.sp_query_store_force_plan @query_id = 4242, @plan_id = 17;\n" +
            "\n" +
            "-- To back out:\n" +
            "-- EXEC sys.sp_query_store_unforce_plan @query_id = 4242, @plan_id = 17;";

        var actual = FactRemediation.GenerateForFinding(PlanRegressionFinding());

        Assert.NotNull(actual);
        Assert.Equal(expected, actual!.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Registry_ResolvesForcePlanHandler_AndMissesUnknown()
    {
        var registry = new RemediationHandlerRegistry(new IRemediationHandler[] { new ForcePlanHandler() });

        Assert.IsType<ForcePlanHandler>(registry.TryGet("PLAN_REGRESSION"));
        Assert.Null(registry.TryGet("PARAMETER_SENSITIVITY"));
        Assert.Null(registry.TryGet(null));
    }

    [Fact]
    public async Task Apply_Success_ForcesAndWritesSuccessAudit()
    {
        var exec = new FakeExecutor();
        var result = await new ForcePlanHandler().ApplyAsync(ForceAction(Target()), exec, Identity, CancellationToken.None);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(RemediationStatus.Success, outcome.Status);
        Assert.True(outcome.AuditWritten);
        Assert.False(outcome.AppliedButUnlogged);
        Assert.Equal(1, exec.ForceCalls);

        var row = Assert.Single(exec.AuditRecords);
        Assert.Equal("force", row.Action);
        Assert.Equal("success", row.Result);
        Assert.Equal("TESTDOMAIN\\tester", row.OperatorIdentity);
        Assert.Contains("sp_query_store_force_plan", row.GeneratedSql);
    }

    [Fact]
    public async Task Apply_HasAlterZero_FailsClosed_NoElevation_AuditSkipped()
    {
        var exec = new FakeExecutor
        {
            ForceFunc = (db, q, p) => new ForcePlanOutcome
            {
                Database = db, QueryId = q, PlanId = p,
                Status = RemediationStatus.PermissionDenied, Forced = false, Message = "lacks ALTER"
            }
        };

        var result = await new ForcePlanHandler().ApplyAsync(ForceAction(Target()), exec, Identity, CancellationToken.None);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(RemediationStatus.PermissionDenied, outcome.Status);
        Assert.False(outcome.AppliedButUnlogged);
        Assert.Equal("skipped", Assert.Single(exec.AuditRecords).Result);
        Assert.Equal(1, exec.ForceCalls);
    }

    [Fact]
    public async Task Apply_AuditTableAbsent_HardBlocks_NoMutation_NoAuditNoUnloggedWarning()
    {
        var exec = new FakeExecutor { AuditTableExists = false };

        var result = await new ForcePlanHandler().ApplyAsync(
            ForceAction(Target(), Target("Other", 9, 9)), exec, Identity, CancellationToken.None);

        Assert.Equal(2, result.Outcomes.Count);
        Assert.All(result.Outcomes, o =>
        {
            Assert.Equal(RemediationStatus.Blocked, o.Status);
            Assert.False(o.AuditWritten);
            Assert.False(o.AppliedButUnlogged);
            Assert.Contains("2.12.0", o.Message);
        });
        Assert.Equal(0, exec.ForceCalls);
        Assert.Empty(exec.AuditRecords);
    }

    [Fact]
    public async Task Apply_Skipped_IsNotForced_AndAuditedSkipped()
    {
        var exec = new FakeExecutor
        {
            ForceFunc = (db, q, p) => new ForcePlanOutcome
            {
                Database = db, QueryId = q, PlanId = p, Status = RemediationStatus.Skipped, Forced = false, Message = "already forced"
            }
        };

        var result = await new ForcePlanHandler().ApplyAsync(ForceAction(Target()), exec, Identity, CancellationToken.None);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(RemediationStatus.Skipped, outcome.Status);
        Assert.False(outcome.AppliedButUnlogged);
        Assert.Equal("skipped", Assert.Single(exec.AuditRecords).Result);
    }

    [Fact]
    public async Task Apply_GateChangesAfterPreflight_AbortsTarget()
    {
        var exec = new FakeExecutor
        {
            PreflightFunc = (db, q, p) => new TargetPreflight
            {
                Database = db, QueryId = q, PlanId = p,
                CurrentDatabase = db, HasAlter = true, QueryStoreState = "READ_WRITE",
                PlanPresent = true, IsForcedPlan = false, ForceFailureCount = 0
            },
            ForceFunc = (db, q, p) => new ForcePlanOutcome
            {
                Database = db, QueryId = q, PlanId = p, Status = RemediationStatus.Skipped, Forced = false, Message = "plan vanished"
            }
        };

        var handler = new ForcePlanHandler();
        var pre = await handler.PreflightAsync(ForceAction(Target()), exec, CancellationToken.None);
        Assert.Equal(RemediationDisposition.Ok, pre.Targets.Single().Disposition);

        var result = await handler.ApplyAsync(ForceAction(Target()), exec, Identity, CancellationToken.None);
        Assert.NotEqual(RemediationStatus.Success, result.Outcomes.Single().Status);
    }

    [Fact]
    public async Task Apply_OneTargetThrows_OthersStillRun()
    {
        var exec = new FakeExecutor
        {
            ForceFunc = (db, q, p) =>
            {
                if (q == 1) throw new InvalidOperationException("boom");
                return new ForcePlanOutcome { Database = db, QueryId = q, PlanId = p, Status = RemediationStatus.Success, Forced = true };
            }
        };

        var result = await new ForcePlanHandler().ApplyAsync(
            ForceAction(Target("A", 1, 1), Target("B", 2, 2)), exec, Identity, CancellationToken.None);

        Assert.Equal(2, result.Outcomes.Count);
        Assert.Equal(RemediationStatus.Error, result.Outcomes[0].Status);
        Assert.Equal(RemediationStatus.Success, result.Outcomes[1].Status);
    }

    [Fact]
    public async Task Apply_ForceSucceedsButAuditWriteFails_FlagsAppliedButUnlogged()
    {
        var exec = new FakeExecutor { AuditWriteResult = false };

        var result = await new ForcePlanHandler().ApplyAsync(ForceAction(Target()), exec, Identity, CancellationToken.None);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(RemediationStatus.Success, outcome.Status);
        Assert.False(outcome.AuditWritten);
        Assert.True(outcome.AppliedButUnlogged);
    }

    [Theory]
    [InlineData("Other", true, "READ_WRITE", true, false, 0, RemediationDisposition.BlockWrongDatabase)]
    [InlineData("AdventureWorks", false, "READ_WRITE", true, false, 0, RemediationDisposition.BlockNoAlter)]
    [InlineData("AdventureWorks", true, "READ_ONLY", true, false, 0, RemediationDisposition.BlockQueryStoreOff)]
    [InlineData("AdventureWorks", true, "READ_WRITE", false, false, 0, RemediationDisposition.BlockStale)]
    [InlineData("AdventureWorks", true, "READ_WRITE", true, true, 0, RemediationDisposition.AlreadyForced)]
    [InlineData("AdventureWorks", true, "READ_WRITE", true, false, 3, RemediationDisposition.WarnFailing)]
    [InlineData("AdventureWorks", true, "READ_WRITE", true, false, 0, RemediationDisposition.Ok)]
    public async Task Preflight_ClassifiesDisposition(
        string currentDb, bool hasAlter, string qsState, bool planPresent, bool isForced, long failCount, RemediationDisposition expected)
    {
        var exec = new FakeExecutor
        {
            PreflightFunc = (db, q, p) => new TargetPreflight
            {
                Database = db, QueryId = q, PlanId = p,
                CurrentDatabase = currentDb, HasAlter = hasAlter, QueryStoreState = qsState,
                PlanPresent = planPresent, IsForcedPlan = isForced, ForceFailureCount = failCount
            }
        };

        var pre = await new ForcePlanHandler().PreflightAsync(ForceAction(Target()), exec, CancellationToken.None);
        Assert.Equal(expected, pre.Targets.Single().Disposition);
    }

    [Fact]
    public async Task Preflight_AuditTableAbsent_OverridesPerTargetDisposition()
    {
        var exec = new FakeExecutor
        {
            AuditTableExists = false,
            PreflightFunc = (db, q, p) => new TargetPreflight
            {
                Database = db, QueryId = q, PlanId = p,
                CurrentDatabase = db, HasAlter = true, QueryStoreState = "READ_WRITE", PlanPresent = true
            }
        };

        var pre = await new ForcePlanHandler().PreflightAsync(ForceAction(Target()), exec, CancellationToken.None);
        Assert.False(pre.AuditTableExists);
        Assert.Equal(RemediationDisposition.BlockAuditTableAbsent, pre.Targets.Single().Disposition);
    }

    [Fact]
    public async Task Unapply_NoPriorForce_Skips_NoUnforce()
    {
        var exec = new FakeExecutor { PriorForce = false };

        var result = await new ForcePlanHandler().UnapplyAsync(ForceAction(Target()), exec, Identity, CancellationToken.None);

        Assert.Equal(RemediationStatus.Skipped, result.Outcomes.Single().Status);
        Assert.Equal(0, exec.UnforceCalls);
    }

    [Fact]
    public async Task Unapply_WithPriorForce_Unforces_AuditsUnforce()
    {
        var exec = new FakeExecutor { PriorForce = true };

        var result = await new ForcePlanHandler().UnapplyAsync(ForceAction(Target()), exec, Identity, CancellationToken.None);

        Assert.Equal(RemediationStatus.Success, result.Outcomes.Single().Status);
        Assert.Equal(1, exec.UnforceCalls);
        Assert.Equal("unforce", Assert.Single(exec.AuditRecords).Action);
    }

    // ── Reachability-with-gate guard (replaces PR-A's no-caller guard) ──────────
    //
    // PR-A asserted the privileged machinery had NO non-core caller. PR-B adds a
    // legitimate caller (the Apply Fix UI), so "no caller" can no longer hold. The
    // invariant becomes "reachable ONLY through the gate," proven in two parts:
    //
    //   A. The core machinery TYPES + force/unforce methods (handler, registry,
    //      executor, ForcePlanAsync/UnforcePlanAsync) are still referenced ONLY
    //      inside the remediation core — the UI never touches them directly.
    //   B. The single gated entry point (RemediationApplyService) is referenced
    //      outside the core ONLY by the sanctioned Apply Fix UI files. That facade
    //      runs the operator confirm before any handler.ApplyAsync (proven
    //      behaviourally by Gate_* in RemediationApplyServiceTests).
    //
    // Together: the UI reaches the privileged executor only via RemediationApplyService,
    // and RemediationApplyService reaches the handler only after confirm() == true.

    private static readonly string[] CoreMachineryMarkers =
    {
        "ForcePlanAsync", "UnforcePlanAsync",
        "RemediationHandlerRegistry", "DatabaseServiceRemediationExecutor",
        "ForcePlanHandler", "IRemediationExecutor", "IRemediationHandler",
    };

    [Fact]
    public void CoreMachinery_OnlyReferencedInRemediationCore()
    {
        var dashboardDir = FindDashboardSourceDir();

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dashboardDir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dashboardDir, file).Replace('\\', '/');
            if (rel.StartsWith("bin/") || rel.StartsWith("obj/"))
                continue;

            var allowed =
                rel.StartsWith("Services/Remediation/") ||
                rel == "Services/DatabaseService.Remediation.cs";
            if (allowed)
                continue;

            var text = File.ReadAllText(file);
            if (CoreMachineryMarkers.Any(text.Contains))
                offenders.Add(rel);
        }

        Assert.True(offenders.Count == 0,
            "The privileged remediation machinery (force/unforce, handler, registry, executor) " +
            "must be reached ONLY through RemediationApplyService — no UI/MCP/menu/command file " +
            "may reference the machinery types directly. Offending files: " + string.Join(", ", offenders));
    }

    [Fact]
    public void GatedEntry_ReferencedOnlyBySanctionedUiPath()
    {
        var dashboardDir = FindDashboardSourceDir();

        // The ONLY files outside the remediation core allowed to reach the gated
        // facade. Any other file gaining a reference to RemediationApplyService is a
        // new, unreviewed path to the privileged executor and must fail the build.
        var sanctioned = new HashSet<string>(StringComparer.Ordinal)
        {
            "MainWindow.xaml.cs",                    // constructs + injects the service
            "Controls/AlertsHistoryContent.xaml.cs", // threads it into the alert detail dialog
            "AlertDetailWindow.xaml.cs",             // invokes Apply/Un-apply via the service
            "RemediationConfirmWindow.xaml.cs",      // the confirm modal (gate UI)
        };

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dashboardDir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dashboardDir, file).Replace('\\', '/');
            if (rel.StartsWith("bin/") || rel.StartsWith("obj/"))
                continue;
            if (rel.StartsWith("Services/Remediation/"))
                continue;
            if (sanctioned.Contains(rel))
                continue;

            var text = File.ReadAllText(file);
            if (text.Contains("RemediationApplyService"))
                offenders.Add(rel);
        }

        Assert.True(offenders.Count == 0,
            "Only the sanctioned Apply Fix UI path may reference RemediationApplyService. " +
            "A new reference is an unreviewed path to the privileged executor. Offending files: " +
            string.Join(", ", offenders));
    }

    private static string FindDashboardSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Dashboard", "Services", "Remediation", "ForcePlanHandler.cs");
            if (File.Exists(candidate))
                return Path.Combine(dir.FullName, "Dashboard");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Dashboard source directory from " + AppContext.BaseDirectory);
    }

    private sealed class FakeExecutor : IRemediationExecutor
    {
        public bool AuditTableExists = true;
        public bool PriorForce = true;
        public bool AuditWriteResult = true;
        public Func<string, long, long, TargetPreflight>? PreflightFunc;
        public Func<string, long, long, ForcePlanOutcome>? ForceFunc;
        public Func<string, long, long, ForcePlanOutcome>? UnforceFunc;

        public int ForceCalls;
        public int UnforceCalls;
        public readonly List<RemediationAuditRecord> AuditRecords = new();

        public Task<TargetPreflight> PreflightForcePlanAsync(string database, long queryId, long planId, CancellationToken ct)
            => Task.FromResult(PreflightFunc?.Invoke(database, queryId, planId) ?? new TargetPreflight
            {
                Database = database, QueryId = queryId, PlanId = planId,
                CurrentDatabase = database, HasAlter = true, QueryStoreState = "READ_WRITE", PlanPresent = true
            });

        public Task<bool> AuditTableExistsAsync(CancellationToken ct) => Task.FromResult(AuditTableExists);

        public Task<bool> HasPriorForceAsync(string database, long queryId, long planId, CancellationToken ct)
            => Task.FromResult(PriorForce);

        public Task<ForcePlanOutcome> ForcePlanAsync(string database, long queryId, long planId, RemediationIdentity identity, CancellationToken ct)
        {
            ForceCalls++;
            return Task.FromResult(ForceFunc?.Invoke(database, queryId, planId) ?? new ForcePlanOutcome
            {
                Database = database, QueryId = queryId, PlanId = planId,
                Status = RemediationStatus.Success, Forced = true, ExecutingLogin = "sa", GateSpid = 55, ExecSpid = 55
            });
        }

        public Task<ForcePlanOutcome> UnforcePlanAsync(string database, long queryId, long planId, RemediationIdentity identity, CancellationToken ct)
        {
            UnforceCalls++;
            return Task.FromResult(UnforceFunc?.Invoke(database, queryId, planId) ?? new ForcePlanOutcome
            {
                Database = database, QueryId = queryId, PlanId = planId,
                Status = RemediationStatus.Success, Forced = true, ExecutingLogin = "sa", GateSpid = 55, ExecSpid = 55
            });
        }

        public Task<bool> WriteAuditAsync(RemediationAuditRecord record, CancellationToken ct)
        {
            AuditRecords.Add(record);
            return Task.FromResult(AuditWriteResult);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Analysis;
using PerformanceMonitorDashboard.Services;
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

    // ════════════════════════════════════════════════════════════════════════════
    // B3 Phase 2 — always-safe DB-config fixes (DB_CONFIG)
    // ════════════════════════════════════════════════════════════════════════════

    private static AnalysisFinding DbConfigFinding(List<object>? rows = null) => new()
    {
        ServerId = 1,
        ServerName = "TestServer",
        Category = "config_issues",
        StoryPath = "DB_CONFIG",
        StoryPathHash = "dbconfig00000001",
        RootFactKey = "DB_CONFIG",
        DrillDown = new Dictionary<string, object>
        {
            ["config_issues"] = rows ?? new List<object>
            {
                new { database = "Foo", recovery_model = "FULL", rcsi = true, query_store = true,
                      issues = new[] { "auto_shrink ON", "auto_close ON" },
                      auto_shrink = true, auto_close = true, page_verify = "CHECKSUM" }
            }
        }
    };

    private static RemediationAction DbConfigAction(params DbConfigTarget[] targets) =>
        new("DB_CONFIG", "set", Array.Empty<ForcePlanTarget>(), targets.ToList());

    // ── Extractor: only the three safe settings, NEVER RCSI ──────────────────────

    [Fact]
    public void ExtractDbConfig_EmitsOnlySafeSettings_NeverRcsi()
    {
        var finding = DbConfigFinding(new List<object>
        {
            new { database = "Db1", rcsi = false, query_store = false,
                  auto_shrink = true, auto_close = true, page_verify = "NONE",
                  issues = new[] { "auto_shrink ON", "auto_close ON", "RCSI OFF", "page_verify=NONE" } }
        });

        var targets = FactRemediation.ExtractDbConfigTargets(finding);

        // 3 safe targets (shrink, close, page_verify) — RCSI is NEVER emitted.
        Assert.Equal(3, targets.Count);
        Assert.Contains(targets, t => t.Setting == DbConfigSetting.AutoShrinkOff && t.CurrentValue == "ON");
        Assert.Contains(targets, t => t.Setting == DbConfigSetting.AutoCloseOff && t.CurrentValue == "ON");
        Assert.Contains(targets, t => t.Setting == DbConfigSetting.PageVerifyChecksum && t.CurrentValue == "NONE");
        // No enum value for RCSI exists, but assert defensively none slipped through as page_verify.
        Assert.All(targets, t => Assert.True(Enum.IsDefined(typeof(DbConfigSetting), t.Setting)));
    }

    [Fact]
    public void ExtractDbConfig_PageVerifyChecksum_IsNotEmitted()
    {
        var finding = DbConfigFinding(new List<object>
        {
            new { database = "Db1", rcsi = true, query_store = true,
                  auto_shrink = false, auto_close = false, page_verify = "CHECKSUM",
                  issues = new string[0] }
        });

        Assert.Empty(FactRemediation.ExtractDbConfigTargets(finding));
    }

    [Fact]
    public void ExtractDbConfig_EmptyDatabase_IsSkipped()
    {
        var finding = DbConfigFinding(new List<object>
        {
            new { database = "", rcsi = true, query_store = true, auto_shrink = true, auto_close = false, page_verify = "CHECKSUM", issues = new string[0] }
        });

        Assert.Empty(FactRemediation.ExtractDbConfigTargets(finding));
    }

    [Fact]
    public void ExtractDbConfig_MultipleDatabases_MultipleTargets()
    {
        var finding = DbConfigFinding(new List<object>
        {
            new { database = "A", rcsi = true, query_store = true, auto_shrink = true,  auto_close = false, page_verify = "CHECKSUM", issues = new string[0] },
            new { database = "B", rcsi = true, query_store = true, auto_shrink = false, auto_close = false, page_verify = "TORN_PAGE_DETECTION", issues = new string[0] }
        });

        var targets = FactRemediation.ExtractDbConfigTargets(finding);
        Assert.Equal(2, targets.Count);
        Assert.Equal("A", targets[0].Database);
        Assert.Equal(DbConfigSetting.AutoShrinkOff, targets[0].Setting);
        Assert.Equal("B", targets[1].Database);
        Assert.Equal(DbConfigSetting.PageVerifyChecksum, targets[1].Setting);
        Assert.Equal("TORN_PAGE_DETECTION", targets[1].CurrentValue);
    }

    [Fact]
    public void BuildAction_DbConfig_OnlyRcsi_ReturnsNull()
    {
        // A DB whose only issue is RCSI OFF yields no safe target → no action → no Apply button.
        var finding = DbConfigFinding(new List<object>
        {
            new { database = "Db1", rcsi = false, query_store = true,
                  auto_shrink = false, auto_close = false, page_verify = "CHECKSUM",
                  issues = new[] { "RCSI OFF" } }
        });

        Assert.Null(FactRemediation.BuildAction(finding));
    }

    [Fact]
    public void BuildAction_DbConfig_ProducesSetActionWithTargets()
    {
        var action = FactRemediation.BuildAction(DbConfigFinding());

        Assert.NotNull(action);
        Assert.Equal("DB_CONFIG", action!.FactKey);
        Assert.Equal("set", action.Action);
        Assert.Empty(action.Targets);                       // force-plan list is empty for DB_CONFIG
        Assert.NotNull(action.DbConfigTargets);
        Assert.Equal(2, action.DbConfigTargets!.Count);     // shrink + close
    }

    // ── Render-stability golden (incl. "was X" + RCSI-excluded note) ─────────────

    [Fact]
    public void GenerateForFinding_DbConfig_RenderStability_ByteForByte()
    {
        var finding = DbConfigFinding(new List<object>
        {
            new { database = "Foo", rcsi = false, query_store = true,
                  auto_shrink = true, auto_close = true, page_verify = "NONE",
                  issues = new[] { "auto_shrink ON", "auto_close ON", "RCSI OFF", "page_verify=NONE" } }
        });

        const string expected =
            "-- Database: Foo\n" +
            "ALTER DATABASE [Foo] SET AUTO_SHRINK OFF;   -- was ON\n" +
            "ALTER DATABASE [Foo] SET AUTO_CLOSE OFF;   -- was ON\n" +
            "ALTER DATABASE [Foo] SET PAGE_VERIFY CHECKSUM;   -- was NONE\n" +
            "-- NOTE: [Foo] also has RCSI OFF — intentionally NOT auto-fixed (test on a copy first).";

        var actual = FactRemediation.GenerateForFinding(finding);
        Assert.NotNull(actual);
        Assert.Equal(expected, actual!.Replace("\r\n", "\n"));
    }

    // ── Handler: audit-absent hard block, fail-closed, freshness-skip, db-not-found, success ──

    [Fact]
    public async Task DbConfig_AuditTableAbsent_HardBlocks_NoMutation_NoAudit()
    {
        var exec = new FakeExecutor { AuditTableExists = false };

        var result = await new DbConfigHandler().ApplyAsync(
            DbConfigAction(new DbConfigTarget("Foo", DbConfigSetting.AutoShrinkOff, "ON"),
                           new DbConfigTarget("Bar", DbConfigSetting.AutoCloseOff, "ON")),
            exec, Identity, CancellationToken.None);

        Assert.Equal(2, result.Outcomes.Count);
        Assert.All(result.Outcomes, o =>
        {
            Assert.Equal(RemediationStatus.Blocked, o.Status);
            Assert.False(o.AuditWritten);
            Assert.Contains("2.12.0", o.Message);
        });
        Assert.Equal(0, exec.SetDbCalls);
        Assert.Empty(exec.AuditRecords);
    }

    [Fact]
    public async Task DbConfig_PermissionDenied_FailsClosed_AuditSkipped()
    {
        var exec = new FakeExecutor
        {
            SetDbFunc = (db, s) => new DbConfigOutcome
            {
                Database = db, Setting = s, Status = RemediationStatus.PermissionDenied, Applied = false,
                Message = "lacks ALTER", PriorValue = "ON"
            }
        };

        var result = await new DbConfigHandler().ApplyAsync(
            DbConfigAction(new DbConfigTarget("Foo", DbConfigSetting.AutoShrinkOff, "ON")), exec, Identity, CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(RemediationStatus.PermissionDenied, o.Status);
        Assert.False(o.AppliedButUnlogged);
        Assert.Equal("skipped", Assert.Single(exec.AuditRecords).Result);
        Assert.Equal(1, exec.SetDbCalls);
    }

    [Fact]
    public async Task DbConfig_AlreadyDesired_Skips_NoMutationFlag_AuditSkipped()
    {
        var exec = new FakeExecutor
        {
            SetDbFunc = (db, s) => new DbConfigOutcome
            {
                Database = db, Setting = s, Status = RemediationStatus.Skipped, Applied = false,
                Message = "already desired", PriorValue = "OFF"
            }
        };

        var result = await new DbConfigHandler().ApplyAsync(
            DbConfigAction(new DbConfigTarget("Foo", DbConfigSetting.AutoShrinkOff, "OFF")), exec, Identity, CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(RemediationStatus.Skipped, o.Status);
        Assert.False(o.AppliedButUnlogged);
        Assert.Equal("skipped", Assert.Single(exec.AuditRecords).Result);
    }

    [Fact]
    public async Task DbConfig_DatabaseNotFound_Blocks_AuditAborted()
    {
        var exec = new FakeExecutor
        {
            SetDbFunc = (db, s) => new DbConfigOutcome
            {
                Database = db, Setting = s, Status = RemediationStatus.Blocked, Applied = false,
                Message = "not found", PriorValue = null
            }
        };

        var result = await new DbConfigHandler().ApplyAsync(
            DbConfigAction(new DbConfigTarget("Gone", DbConfigSetting.AutoShrinkOff, "ON")), exec, Identity, CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(RemediationStatus.Blocked, o.Status);
        Assert.Equal("aborted", Assert.Single(exec.AuditRecords).Result);
    }

    [Fact]
    public async Task DbConfig_Success_AppliesAndAudits_NullIds_PriorValue_PreciseAction()
    {
        var exec = new FakeExecutor
        {
            SetDbFunc = (db, s) => new DbConfigOutcome
            {
                Database = db, Setting = s, Status = RemediationStatus.Success, Applied = true,
                ExecutingLogin = "sa", PriorValue = "ON",
                GeneratedSql = "ALTER DATABASE [Foo] SET AUTO_SHRINK OFF;", GateSpid = 7, ExecSpid = 7
            }
        };

        var result = await new DbConfigHandler().ApplyAsync(
            DbConfigAction(new DbConfigTarget("Foo", DbConfigSetting.AutoShrinkOff, "ON")), exec, Identity, CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(RemediationStatus.Success, o.Status);
        Assert.True(o.AuditWritten);
        Assert.False(o.AppliedButUnlogged);

        var row = Assert.Single(exec.AuditRecords);
        Assert.Equal("DB_CONFIG", row.FactKey);
        Assert.Equal("set_auto_shrink_off", row.Action);     // precise taxonomy (24/19/18 chars)
        Assert.Equal("success", row.Result);
        Assert.Null(row.QueryId);                            // B-1: DB_CONFIG rows have no IDs
        Assert.Null(row.PlanId);
        Assert.Equal("ON", row.PriorValue);
        Assert.Contains("ALTER DATABASE", row.GeneratedSql);
    }

    [Fact]
    public async Task DbConfig_AppliesButAuditFails_FlagsAppliedButUnlogged()
    {
        var exec = new FakeExecutor { AuditWriteResult = false };

        var result = await new DbConfigHandler().ApplyAsync(
            DbConfigAction(new DbConfigTarget("Foo", DbConfigSetting.AutoShrinkOff, "ON")), exec, Identity, CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(RemediationStatus.Success, o.Status);
        Assert.False(o.AuditWritten);
        Assert.True(o.AppliedButUnlogged);
    }

    [Fact]
    public async Task DbConfig_OneTargetThrows_OthersStillRun()
    {
        var exec = new FakeExecutor
        {
            SetDbFunc = (db, s) =>
            {
                if (db == "A") throw new InvalidOperationException("boom");
                return new DbConfigOutcome { Database = db, Setting = s, Status = RemediationStatus.Success, Applied = true, PriorValue = "ON" };
            }
        };

        var result = await new DbConfigHandler().ApplyAsync(
            DbConfigAction(new DbConfigTarget("A", DbConfigSetting.AutoShrinkOff, "ON"),
                           new DbConfigTarget("B", DbConfigSetting.AutoCloseOff, "ON")),
            exec, Identity, CancellationToken.None);

        Assert.Equal(2, result.Outcomes.Count);
        Assert.Equal(RemediationStatus.Error, result.Outcomes[0].Status);
        Assert.Equal(RemediationStatus.Success, result.Outcomes[1].Status);
    }

    [Fact]
    public async Task DbConfig_PreciseTaxonomy_AllThreeSettings()
    {
        var exec = new FakeExecutor();
        await new DbConfigHandler().ApplyAsync(
            DbConfigAction(
                new DbConfigTarget("Foo", DbConfigSetting.AutoShrinkOff, "ON"),
                new DbConfigTarget("Foo", DbConfigSetting.AutoCloseOff, "ON"),
                new DbConfigTarget("Foo", DbConfigSetting.PageVerifyChecksum, "NONE")),
            exec, Identity, CancellationToken.None);

        var actions = exec.AuditRecords.Select(r => r.Action).ToList();
        Assert.Contains("set_auto_shrink_off", actions);
        Assert.Contains("set_auto_close_off", actions);
        Assert.Contains("set_page_verify_checksum", actions);     // 24 chars — needs varchar(32) + param(32)
    }

    // ── Preflight dispositions ───────────────────────────────────────────────────

    [Theory]
    [InlineData(false, true, false, RemediationDisposition.BlockDatabaseNotFound)]
    [InlineData(true, false, false, RemediationDisposition.BlockNoAlter)]
    [InlineData(true, true, true, RemediationDisposition.AlreadyInDesiredState)]
    [InlineData(true, true, false, RemediationDisposition.Ok)]
    public async Task DbConfig_Preflight_ClassifiesDisposition(bool exists, bool hasAlter, bool alreadyDesired, RemediationDisposition expected)
    {
        var exec = new FakeExecutor
        {
            DbPreflightFunc = (db, s) => new DbConfigPreflight
            {
                Database = db, Setting = s, DatabaseExists = exists, HasAlter = hasAlter,
                AlreadyInDesiredState = alreadyDesired, CurrentValue = "ON"
            }
        };

        var pre = await new DbConfigHandler().PreflightAsync(
            DbConfigAction(new DbConfigTarget("Foo", DbConfigSetting.AutoShrinkOff, "ON")), exec, CancellationToken.None);
        Assert.Equal(expected, pre.Targets.Single().Disposition);
    }

    [Fact]
    public async Task DbConfig_Preflight_AuditTableAbsent_OverridesDisposition()
    {
        var exec = new FakeExecutor { AuditTableExists = false };

        var pre = await new DbConfigHandler().PreflightAsync(
            DbConfigAction(new DbConfigTarget("Foo", DbConfigSetting.AutoShrinkOff, "ON")), exec, CancellationToken.None);

        Assert.False(pre.AuditTableExists);
        Assert.Equal(RemediationDisposition.BlockAuditTableAbsent, pre.Targets.Single().Disposition);
    }

    // ── Apply-only enforcement (m-1) + SupportsUnapply ───────────────────────────

    [Fact]
    public void DbConfig_SupportsUnapply_IsFalse_NotDestructive()
    {
        var handler = new DbConfigHandler();
        Assert.False(handler.SupportsUnapply);
        Assert.False(handler.IsDestructive);
    }

    [Fact]
    public async Task DbConfig_UnapplyAsync_Throws()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new DbConfigHandler().UnapplyAsync(
                DbConfigAction(new DbConfigTarget("Foo", DbConfigSetting.AutoShrinkOff, "ON")), new FakeExecutor(), Identity, CancellationToken.None));
    }

    [Fact]
    public void CoreMachineryMarkers_CoverNewPrivilegedSurface()
    {
        // M-2: the reachability guard must list the new handler + executor methods,
        // or a future UI/MCP file referencing them would silently pass the guard.
        Assert.Contains("DbConfigHandler", CoreMachineryMarkers);
        Assert.Contains("SetDatabaseOptionAsync", CoreMachineryMarkers);
        Assert.Contains("PreflightDbConfigAsync", CoreMachineryMarkers);
    }

    [Fact]
    public void Registry_ResolvesDbConfigHandler()
    {
        var registry = new RemediationHandlerRegistry(new IRemediationHandler[] { new ForcePlanHandler(), new DbConfigHandler() });
        Assert.IsType<DbConfigHandler>(registry.TryGet("DB_CONFIG"));
        Assert.IsType<ForcePlanHandler>(registry.TryGet("PLAN_REGRESSION"));
    }

    // ── §1 identifier safety: no-injection / QUOTENAME / same-string (M-1) ───────
    //
    // These run against the EXECUTOR's OWN statement builder (DatabaseService.
    // BuildAlterStatement), NOT the display renderer (m-B). The builder is the exact
    // composition SetDatabaseOptionAsync uses for the executed statement.

    [Theory]
    [InlineData(DbConfigSetting.AutoShrinkOff, "SET AUTO_SHRINK OFF")]
    [InlineData(DbConfigSetting.AutoCloseOff, "SET AUTO_CLOSE OFF")]
    [InlineData(DbConfigSetting.PageVerifyChecksum, "SET PAGE_VERIFY CHECKSUM")]
    public void Build_SetClause_IsHardcodedLiteral_RegardlessOfName(DbConfigSetting setting, string expectedClause)
    {
        // The SET clause is byte-identical regardless of the (pathological) db name.
        foreach (var name in new[] { "Db", "Db]; DROP TABLE x--", "[weird]", "Ünîçödé" })
        {
            var stmt = DatabaseService.BuildAlterStatement(name, setting);
            Assert.Contains(expectedClause + ";", stmt);
            Assert.EndsWith(expectedClause + ";", stmt);
        }
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("has]bracket")]
    [InlineData("has]]double")]
    [InlineData("ev;il-- GO DROP")]
    [InlineData("'; ALTER DATABASE master SET")]
    [InlineData("Ünîçödé​")]   // unicode + zero-width
    public void Build_BracketsIdentifierInert_NoInjection(string name)
    {
        var stmt = DatabaseService.BuildAlterStatement(name, DbConfigSetting.AutoShrinkOff);

        // The identifier is bracketed with ]-doubling; the whole thing is one ALTER
        // with exactly one bracketed token and the hardcoded clause.
        var expectedToken = "[" + name.Replace("]", "]]") + "]";
        Assert.Equal($"ALTER DATABASE {expectedToken} SET AUTO_SHRINK OFF;", stmt);

        // No un-doubled close-bracket can prematurely end the identifier: the only
        // ']' runs in the statement are the doubled ones we produced plus the final.
        // Reconstruct: stripping the known prefix/suffix yields exactly the token.
        Assert.StartsWith("ALTER DATABASE [", stmt);
    }

    [Fact]
    public void Build_SameString_ValidatedNameIsBracketedExactly()
    {
        // M-1: the bracketed token is byte-identical to the validated name passed in
        // — an implementer cannot validate one variable and bracket a trimmed/cased one.
        const string validated = "  Padded Name  ";          // deliberate surrounding whitespace
        var stmt = DatabaseService.BuildAlterStatement(validated, DbConfigSetting.AutoCloseOff);
        Assert.Equal("ALTER DATABASE [  Padded Name  ] SET AUTO_CLOSE OFF;", stmt);

        // A name differing only by trailing whitespace produces a DIFFERENT statement,
        // proving the builder brackets exactly what it was given (no normalization).
        var stmt2 = DatabaseService.BuildAlterStatement(validated.TrimEnd(), DbConfigSetting.AutoCloseOff);
        Assert.NotEqual(stmt, stmt2);
    }

    [Fact]
    public void Build_DisplayRenderer_NotExecuted_ExecutorHasOwnBuilder()
    {
        // m-B: the executor's builder is byte-identical to the display QUOTENAME but
        // is its OWN routine in the Dashboard assembly (not the private Analysis one).
        // Spot-check parity on a ]-containing name.
        var stmt = DatabaseService.BuildAlterStatement("a]b", DbConfigSetting.PageVerifyChecksum);
        Assert.Equal("ALTER DATABASE [a]]b] SET PAGE_VERIFY CHECKSUM;", stmt);
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
        // B3 Phase 2 privileged surface (M-2): the new handler + executor methods
        // must be reachable ONLY through the gate, exactly like the force-plan ones.
        "DbConfigHandler", "SetDatabaseOptionAsync", "PreflightDbConfigAsync",
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

        // ── DB-config seams (Phase 2) ────────────────────────────────────────────
        public Func<string, DbConfigSetting, DbConfigPreflight>? DbPreflightFunc;
        public Func<string, DbConfigSetting, DbConfigOutcome>? SetDbFunc;
        public int SetDbCalls;

        public Task<DbConfigPreflight> PreflightDbConfigAsync(string database, DbConfigSetting setting, CancellationToken ct)
            => Task.FromResult(DbPreflightFunc?.Invoke(database, setting) ?? new DbConfigPreflight
            {
                Database = database, Setting = setting, DatabaseExists = true, HasAlter = true,
                AlreadyInDesiredState = false, ExecutingLogin = "sa", CurrentValue = "ON"
            });

        public Task<DbConfigOutcome> SetDatabaseOptionAsync(string database, DbConfigSetting setting, RemediationIdentity identity, CancellationToken ct)
        {
            SetDbCalls++;
            return Task.FromResult(SetDbFunc?.Invoke(database, setting) ?? new DbConfigOutcome
            {
                Database = database, Setting = setting, Status = RemediationStatus.Success, Applied = true,
                ExecutingLogin = "sa", PriorValue = "ON", GeneratedSql = "ALTER DATABASE [x] SET AUTO_SHRINK OFF;",
                GateSpid = 55, ExecSpid = 55
            });
        }

        public Task<bool> WriteAuditAsync(RemediationAuditRecord record, CancellationToken ct)
        {
            AuditRecords.Add(record);
            return Task.FromResult(AuditWriteResult);
        }
    }
}

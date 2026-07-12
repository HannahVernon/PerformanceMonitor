using System.Collections.Generic;
using PerformanceMonitor.Analysis;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// Tests the shared <see cref="AnomalyIncidentReconciler"/> (Stage B, change 3): after clustering
/// stamps each story's incident id, an ANOMALY_* story folds into the REGULAR finding that describes
/// the same symptom in the same run and same database, by rewriting its incident id onto the parent's.
/// Fold-don't-suppress, database-aware, and only when a real parent exists. Lives in both Lite.Tests
/// and Dashboard.Tests because the reconciler is shared and each app references the assembly.
/// </summary>
public class AnomalyIncidentReconcilerTests
{
    private static AnalysisStory Story(
        string rootKey, string incidentId, string? db = null,
        Dictionary<string, double>? metadata = null, double severity = 1.0) =>
        new()
        {
            RootFactKey = rootKey,
            IncidentId = incidentId,
            DatabaseName = db,
            Severity = severity,
            RootFactMetadata = metadata,
            Path = [rootKey],
        };

    [Fact]
    public void CxDominantWaitProfile_FoldsIntoCxpacketIncident()
    {
        // Dominant contrib_ is a CX* type -> family CXPACKET (mirrors GroupParallelismWaits).
        var regular = Story("CXPACKET", "cx-incident", severity: 1.4);
        var anomaly = Story("ANOMALY_WAIT_PROFILE", "anomaly-solo",
            metadata: new() { ["contrib_CXPACKET"] = 5000, ["contrib_SOS_SCHEDULER_YIELD"] = 900 });

        AnomalyIncidentReconciler.Reconcile(new List<AnalysisStory> { regular, anomaly });

        Assert.Equal("cx-incident", anomaly.IncidentId); // folded into the CXPACKET incident
        Assert.Equal("cx-incident", regular.IncidentId);  // parent id untouched
    }

    [Fact]
    public void GeneralLockDominantWaitProfile_FoldsIntoLckIncident()
    {
        // LCK_M_X is a general lock mode -> family LCK (mirrors IsGeneralLockWait grouping).
        var regular = Story("LCK", "lck-incident");
        var anomaly = Story("ANOMALY_WAIT_PROFILE", "anomaly-solo",
            metadata: new() { ["contrib_LCK_M_X"] = 8000, ["contrib_CXPACKET"] = 100 });

        AnomalyIncidentReconciler.Reconcile(new List<AnalysisStory> { regular, anomaly });

        Assert.Equal("lck-incident", anomaly.IncidentId);
    }

    [Fact]
    public void ReaderLockDominantWaitProfile_FoldsIntoOwnKey_NotLck()
    {
        // LCK_M_S is kept separate from the LCK family (RCSI signal) -> family LCK_M_S, so it must
        // fold into the LCK_M_S finding, NOT the general LCK one.
        var lck = Story("LCK", "lck-incident");
        var readerLock = Story("LCK_M_S", "readerlock-incident");
        var anomaly = Story("ANOMALY_WAIT_PROFILE", "anomaly-solo",
            metadata: new() { ["contrib_LCK_M_S"] = 8000, ["contrib_LCK_M_X"] = 10 });

        AnomalyIncidentReconciler.Reconcile(new List<AnalysisStory> { lck, readerLock, anomaly });

        Assert.Equal("readerlock-incident", anomaly.IncidentId);
    }

    [Fact]
    public void CpuSpike_FoldsIntoCpuIncident()
    {
        var regular = Story("CPU_SQL_PERCENT", "cpu-incident");
        var anomaly = Story("ANOMALY_CPU_SPIKE", "anomaly-solo");

        AnomalyIncidentReconciler.Reconcile(new List<AnalysisStory> { regular, anomaly });

        Assert.Equal("cpu-incident", anomaly.IncidentId);
    }

    [Fact]
    public void NoRegularParent_AnomalyStaysSolo()
    {
        // ANOMALY_CPU_SPIKE with no CPU_SQL_PERCENT parent (only an unrelated regular story) stays solo.
        var unrelated = Story("CXPACKET", "cx-incident");
        var anomaly = Story("ANOMALY_CPU_SPIKE", "anomaly-solo");

        AnomalyIncidentReconciler.Reconcile(new List<AnalysisStory> { unrelated, anomaly });

        Assert.Equal("anomaly-solo", anomaly.IncidentId);
    }

    [Fact]
    public void DifferentDatabase_DoesNotFold()
    {
        // The anomaly is in db1, the only regular parent is in db2 -> DB-aware, no fold.
        var regular = Story("CPU_SQL_PERCENT", "cpu-db2", db: "db2");
        var anomaly = Story("ANOMALY_CPU_SPIKE", "anomaly-db1", db: "db1");

        AnomalyIncidentReconciler.Reconcile(new List<AnalysisStory> { regular, anomaly });

        Assert.Equal("anomaly-db1", anomaly.IncidentId);
    }

    [Fact]
    public void SameDatabase_Folds()
    {
        var regular = Story("CPU_SQL_PERCENT", "cpu-db1", db: "db1");
        var anomaly = Story("ANOMALY_CPU_SPIKE", "anomaly-db1", db: "db1");

        AnomalyIncidentReconciler.Reconcile(new List<AnalysisStory> { regular, anomaly });

        Assert.Equal("cpu-db1", anomaly.IncidentId);
    }

    [Fact]
    public void AnomalyObject_NeverCrossDbFolds()
    {
        // ANOMALY_OBJECT_* is unmapped AND db-scoped: it never folds, and certainly not into a
        // regular finding in a different database.
        var regularDb2 = Story("BLOCKING_EVENTS", "blk-db2", db: "db2");
        var objectAnomaly = Story("ANOMALY_OBJECT_CONTENTION", "object-db1", db: "db1");

        AnomalyIncidentReconciler.Reconcile(new List<AnalysisStory> { regularDb2, objectAnomaly });

        Assert.Equal("object-db1", objectAnomaly.IncidentId);
    }

    [Fact]
    public void UnmappedAnomaly_StaysSolo()
    {
        // Batch/session/query-duration anomalies have no single regular counterpart.
        var regular = Story("CPU_SQL_PERCENT", "cpu-incident");
        var anomaly = Story("ANOMALY_BATCH_REQUESTS", "anomaly-solo");

        AnomalyIncidentReconciler.Reconcile(new List<AnalysisStory> { regular, anomaly });

        Assert.Equal("anomaly-solo", anomaly.IncidentId);
    }

    [Fact]
    public void AbsolutionParent_IsIgnored()
    {
        // An absolution story is never a fold target.
        var absolution = new AnalysisStory { RootFactKey = "server_health", IsAbsolution = true, IncidentId = "" };
        var anomaly = Story("ANOMALY_CPU_SPIKE", "anomaly-solo");

        AnomalyIncidentReconciler.Reconcile(new List<AnalysisStory> { absolution, anomaly });

        Assert.Equal("anomaly-solo", anomaly.IncidentId);
    }

    [Fact]
    public void FewerThanTwoStories_NoOp()
    {
        AnomalyIncidentReconciler.Reconcile(null!); // no throw
        var solo = new List<AnalysisStory> { Story("ANOMALY_CPU_SPIKE", "anomaly-solo") };
        AnomalyIncidentReconciler.Reconcile(solo);
        Assert.Equal("anomaly-solo", solo[0].IncidentId);
    }
}

using System.Collections.Generic;
using System.Linq;
using PerformanceMonitor.Analysis;
using PerformanceMonitor.Common;

namespace PerformanceMonitorDashboard.Analysis;

/// <summary>
/// The trivial, mechanical projection from the analysis engine's (internal) reconstruction into the shared
/// public <see cref="BlockingChainModel"/> for the viewer. This is the only per-app block-chain code — it
/// must bridge an internal analysis type to a Common type, and no shared assembly can see both (Ui sees
/// Common but not Analysis; Analysis does not reference Common by design). Kept byte-identical to Lite's copy.
/// </summary>
internal static class BlockingChainViewerProjection
{
    public const string EmptyStateDetail =
        "No blocked-process reports were collected in the selected time range. " +
        "Blocking chains require the blocked-process-report Extended Event; Azure SQL DB may not emit it " +
        "(the blocked-process threshold is set via sp_configure, which Azure SQL DB does not support).";

    public static BlockingChainModel BuildModel(BlockingReconstruction reconstruction)
    {
        var inputs = reconstruction.Chains.Select(static c => new BlockingChainInput
        {
            ApexSpid = c.ApexSpid,
            ApexTranStarted = c.ApexTranStarted,
            ApexSleeping = c.ApexSleeping,
            Magnitude = c.Magnitude,
            Edges = c.Levels.Select(static l => new BlockingEdgeInput
            {
                Level = l.Level,
                BlockingSpid = l.BlockingSpid,
                BlockingTranStarted = l.BlockingTranStarted,
                BlockedSpid = l.BlockedSpid,
                BlockedTranStarted = l.BlockedTranStarted,
                LockMode = l.LockMode,
                WaitTimeMs = l.WaitTimeMs,
                DatabaseName = l.DatabaseName,
                BlockingSqlText = l.BlockingSqlText,
                BlockedSqlText = l.BlockedSqlText
            }).ToList()
        }).ToList();

        return BlockingChainTreeBuilder.Build(
            inputs,
            reconstruction.CycleDetected,
            reconstruction.DepthCapped,
            reconstruction.TraversalTruncated);
    }
}

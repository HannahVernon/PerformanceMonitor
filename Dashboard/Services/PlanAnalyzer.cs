using System;
using System.Collections.Generic;
using PerformanceMonitorDashboard.Models;

namespace PerformanceMonitorDashboard.Services;

/// <summary>
/// Post-parse analysis pass that walks a parsed plan tree and adds warnings
/// for common performance anti-patterns. Called after ShowPlanParser.Parse().
/// </summary>
public static class PlanAnalyzer
{
    public static void Analyze(ParsedPlan plan)
    {
        foreach (var batch in plan.Batches)
        {
            foreach (var stmt in batch.Statements)
            {
                AnalyzeStatement(stmt);

                if (stmt.RootNode != null)
                    AnalyzeNodeTree(stmt.RootNode);
            }
        }
    }

    private static void AnalyzeStatement(PlanStatement stmt)
    {
        // Rule 3: Serial plan with reason — promote to a louder warning
        if (!string.IsNullOrEmpty(stmt.NonParallelPlanReason))
        {
            var reason = stmt.NonParallelPlanReason switch
            {
                "MaxDOPSetToOne" => "MAXDOP is set to 1",
                "EstimatedDOPIsOne" => "Estimated DOP is 1",
                "NoParallelPlansInDesktopOrExpressEdition" => "Express/Desktop edition does not support parallelism",
                "CouldNotGenerateValidParallelPlan" => "Optimizer could not generate a valid parallel plan",
                "QueryHintNoParallelSet" => "OPTION (MAXDOP 1) hint forces serial execution",
                _ => stmt.NonParallelPlanReason
            };

            stmt.PlanWarnings.Add(new PlanWarning
            {
                WarningType = "Serial Plan",
                Message = $"Query forced to run serially: {reason}",
                Severity = PlanWarningSeverity.Warning
            });
        }
    }

    private static void AnalyzeNodeTree(PlanNode node)
    {
        AnalyzeNode(node);

        foreach (var child in node.Children)
            AnalyzeNodeTree(child);
    }

    private static void AnalyzeNode(PlanNode node)
    {
        // Rule 1: Filter operators — rows survived the tree just to be discarded
        if (node.PhysicalOp == "Filter" && !string.IsNullOrEmpty(node.Predicate))
        {
            node.Warnings.Add(new PlanWarning
            {
                WarningType = "Filter Operator",
                Message = $"Filter discards rows late in the plan. Predicate: {Truncate(node.Predicate, 200)}",
                Severity = PlanWarningSeverity.Warning
            });
        }

        // Rule 2: Eager Index Spools — optimizer building temporary indexes on the fly
        if (node.PhysicalOp.Contains("Eager", StringComparison.OrdinalIgnoreCase) &&
            node.PhysicalOp.Contains("Spool", StringComparison.OrdinalIgnoreCase))
        {
            node.Warnings.Add(new PlanWarning
            {
                WarningType = "Eager Index Spool",
                Message = "Optimizer is building a temporary index at runtime. A permanent index may help.",
                Severity = PlanWarningSeverity.Warning
            });
        }

        // Rule 4: UDF timing — any node spending time in UDFs
        if (node.UdfCpuTimeUs > 0 || node.UdfElapsedTimeUs > 0)
        {
            var cpuMs = node.UdfCpuTimeUs / 1000.0;
            var elapsedMs = node.UdfElapsedTimeUs / 1000.0;
            node.Warnings.Add(new PlanWarning
            {
                WarningType = "UDF Execution",
                Message = $"Scalar UDF executing on this operator. UDF elapsed: {elapsedMs:F1}ms, UDF CPU: {cpuMs:F1}ms",
                Severity = elapsedMs >= 1000 ? PlanWarningSeverity.Critical : PlanWarningSeverity.Warning
            });
        }

        // Rule 5: Large estimate vs actual row gaps (actual plans only)
        if (node.HasActualStats && node.EstimateRows > 0)
        {
            var ratio = node.ActualRows / node.EstimateRows;
            if (ratio >= 10.0 || ratio <= 0.1)
            {
                var direction = ratio >= 10.0 ? "underestimated" : "overestimated";
                var factor = ratio >= 10.0 ? ratio : 1.0 / ratio;
                node.Warnings.Add(new PlanWarning
                {
                    WarningType = "Row Estimate Mismatch",
                    Message = $"Estimated {node.EstimateRows:N0} rows, actual {node.ActualRows:N0} ({factor:F0}x {direction}). May cause poor plan choices.",
                    Severity = factor >= 100 ? PlanWarningSeverity.Critical : PlanWarningSeverity.Warning
                });
            }
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}

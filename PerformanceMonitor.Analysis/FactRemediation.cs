using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PerformanceMonitor.Analysis;

/// <summary>
/// Builds copy-paste-ready T-SQL remediation snippets for findings whose
/// drill-down detail carries the data needed to construct a safe, parameterised
/// EXEC statement. Today this is PLAN_REGRESSION only — generates one
/// sp_query_store_force_plan block per top regressed query (up to 5), with a
/// header comment showing the regression factor and a commented unforce
/// statement for back-out.
///
/// <para>
/// PARAMETER_SENSITIVITY is intentionally excluded. Forcing the worst
/// sensitive plan locks in a plan that is bad for some parameter values; the
/// remediation is OPTION(RECOMPILE), OPTIMIZE FOR, plan guides, or query
/// rewrite — not plan force. The advice prose says this; this builder returns
/// null for that fact key.
/// </para>
/// </summary>
public static class FactRemediation
{
    /// <summary>
    /// Returns generated T-SQL for the finding, or null if no remediation
    /// shape applies. Inspects the finding's DrillDown for the data needed
    /// to fill in the EXEC parameters. The output is raw T-SQL with no
    /// markup — renderers wrap it as needed (Slack mrkdwn code block, HTML
    /// &lt;pre&gt;, etc.).
    /// </summary>
    public static string? GenerateForFinding(AnalysisFinding finding)
    {
        if (finding is null || string.IsNullOrEmpty(finding.RootFactKey))
            return null;

        return finding.RootFactKey switch
        {
            "PLAN_REGRESSION" => GenerateForPlanRegression(finding),
            _ => null
        };
    }

    /// <summary>
    /// Builds the structured, typed remediation action for the finding, or null
    /// if no execution shape applies. Mirrors <see cref="GenerateForFinding"/>'s
    /// switch on <see cref="AnalysisFinding.RootFactKey"/>: PLAN_REGRESSION yields
    /// a "force" action over the extracted targets (when any are valid); every
    /// other fact key — including PARAMETER_SENSITIVITY — yields null (no handler,
    /// no Apply affordance), consistent with the "do not force" advice.
    /// </summary>
    public static RemediationAction? BuildAction(AnalysisFinding finding)
    {
        if (finding is null || string.IsNullOrEmpty(finding.RootFactKey))
            return null;

        switch (finding.RootFactKey)
        {
            case "PLAN_REGRESSION":
                var targets = ExtractPlanRegressionTargets(finding);
                return targets.Count == 0
                    ? null
                    : new RemediationAction("PLAN_REGRESSION", "force", targets);
            default:
                return null;
        }
    }

    /// <summary>
    /// Extracts the typed force-plan targets from a PLAN_REGRESSION finding's
    /// drill-down. This is the single parse: the preview renderer
    /// (<see cref="GenerateForPlanRegression"/>) renders entirely from this list,
    /// and <see cref="BuildAction"/> persists it. Applies the same guards as the
    /// renderer always has — database non-empty, query_id &gt; 0, best_plan_id
    /// &gt; 0 — and the same cap of 5 targets. Reads every value the preview
    /// renders (including the two cpu/exec numbers) so the renderer needs no
    /// second drill-down read.
    /// </summary>
    public static IReadOnlyList<ForcePlanTarget> ExtractPlanRegressionTargets(AnalysisFinding finding)
    {
        var targets = new List<ForcePlanTarget>();

        if (finding?.DrillDown is null ||
            !finding.DrillDown.TryGetValue("regressed_queries", out var raw) ||
            raw is null)
            return targets;

        JsonElement element;
        try
        {
            element = JsonSerializer.SerializeToElement(raw);
        }
        catch
        {
            return targets;
        }

        if (element.ValueKind != JsonValueKind.Array)
            return targets;

        foreach (var row in element.EnumerateArray())
        {
            if (targets.Count >= 5) break;
            if (row.ValueKind != JsonValueKind.Object) continue;

            var database = GetString(row, "database");
            var queryId = GetInt64(row, "query_id");
            var bestPlanId = GetInt64(row, "best_plan_id");
            if (string.IsNullOrEmpty(database) || queryId <= 0 || bestPlanId <= 0)
                continue;

            targets.Add(new ForcePlanTarget(
                Database: database,
                QueryId: queryId,
                PlanId: bestPlanId,
                BestPlanHash: GetString(row, "best_plan_hash"),
                LatestPlanHash: GetString(row, "latest_plan_hash"),
                LatestCpuPerExecUs: GetDouble(row, "latest_cpu_per_exec_us"),
                BestCpuPerExecUs: GetDouble(row, "best_cpu_per_exec_us"),
                RegressionFactor: GetDouble(row, "regression_factor")));
        }

        return targets;
    }

    /// <summary>
    /// Thin renderer over <see cref="ExtractPlanRegressionTargets"/>. The output
    /// is byte-for-byte the same preview the inline parse produced before the
    /// extract-once refactor (guarded by the render-stability golden test),
    /// including the two "(cpu/exec ... us)" comment lines.
    /// </summary>
    private static string? GenerateForPlanRegression(AnalysisFinding finding)
    {
        var targets = ExtractPlanRegressionTargets(finding);
        if (targets.Count == 0)
            return null;

        var sb = new StringBuilder();
        var emitted = 0;

        foreach (var target in targets)
        {
            if (emitted > 0)
                sb.AppendLine();

            sb.AppendLine($"-- Database: {target.Database}");
            sb.AppendLine($"-- query_id = {target.QueryId}, forcing plan_id = {target.PlanId}");
            if (!string.IsNullOrEmpty(target.LatestPlanHash))
                sb.AppendLine($"--   latest plan hash: {target.LatestPlanHash} (cpu/exec {target.LatestCpuPerExecUs:F0} us)");
            if (!string.IsNullOrEmpty(target.BestPlanHash))
                sb.AppendLine($"--   best plan hash:   {target.BestPlanHash}   (cpu/exec {target.BestCpuPerExecUs:F0} us)");
            sb.AppendLine($"--   regression factor: {target.RegressionFactor:F1}x");
            sb.AppendLine($"USE {QuoteName(target.Database)};");
            sb.AppendLine($"EXEC sys.sp_query_store_force_plan @query_id = {target.QueryId}, @plan_id = {target.PlanId};");
            sb.AppendLine();
            sb.AppendLine($"-- To back out:");
            sb.AppendLine($"-- EXEC sys.sp_query_store_unforce_plan @query_id = {target.QueryId}, @plan_id = {target.PlanId};");

            emitted++;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// QUOTENAME-equivalent: wrap an identifier in square brackets and double
    /// any embedded close-bracket. The database name comes from
    /// sys.databases (via the drill-down collector), so it is already a valid
    /// SQL identifier — this guards against pathologically bracketed names
    /// without trusting that guarantee.
    /// </summary>
    private static string QuoteName(string identifier)
    {
        return "[" + identifier.Replace("]", "]]") + "]";
    }

    private static string GetString(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var v)) return string.Empty;
        return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty) : string.Empty;
    }

    private static long GetInt64(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var i) => i,
            JsonValueKind.Number => (long)v.GetDouble(),
            _ => 0
        };
    }

    private static double GetDouble(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var v)) return 0.0;
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;
    }
}

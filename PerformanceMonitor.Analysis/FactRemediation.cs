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

    private static string? GenerateForPlanRegression(AnalysisFinding finding)
    {
        if (finding.DrillDown is null || !finding.DrillDown.TryGetValue("regressed_queries", out var raw) || raw is null)
            return null;

        JsonElement element;
        try
        {
            element = JsonSerializer.SerializeToElement(raw);
        }
        catch
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
            return null;

        var sb = new StringBuilder();
        var emitted = 0;

        foreach (var row in element.EnumerateArray())
        {
            if (emitted >= 5) break;
            if (row.ValueKind != JsonValueKind.Object) continue;

            var database = GetString(row, "database");
            var queryId = GetInt64(row, "query_id");
            var bestPlanId = GetInt64(row, "best_plan_id");
            if (string.IsNullOrEmpty(database) || queryId <= 0 || bestPlanId <= 0)
                continue;

            var latestHash = GetString(row, "latest_plan_hash");
            var bestHash = GetString(row, "best_plan_hash");
            var latestCpu = GetDouble(row, "latest_cpu_per_exec_us");
            var bestCpu = GetDouble(row, "best_cpu_per_exec_us");
            var regressionFactor = GetDouble(row, "regression_factor");

            if (emitted > 0)
                sb.AppendLine();

            sb.AppendLine($"-- Database: {database}");
            sb.AppendLine($"-- query_id = {queryId}, forcing plan_id = {bestPlanId}");
            if (!string.IsNullOrEmpty(latestHash))
                sb.AppendLine($"--   latest plan hash: {latestHash} (cpu/exec {latestCpu:F0} us)");
            if (!string.IsNullOrEmpty(bestHash))
                sb.AppendLine($"--   best plan hash:   {bestHash}   (cpu/exec {bestCpu:F0} us)");
            sb.AppendLine($"--   regression factor: {regressionFactor:F1}x");
            sb.AppendLine($"USE {QuoteName(database)};");
            sb.AppendLine($"EXEC sys.sp_query_store_force_plan @query_id = {queryId}, @plan_id = {bestPlanId};");
            sb.AppendLine();
            sb.AppendLine($"-- To back out:");
            sb.AppendLine($"-- EXEC sys.sp_query_store_unforce_plan @query_id = {queryId}, @plan_id = {bestPlanId};");

            emitted++;
        }

        if (emitted == 0)
            return null;

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

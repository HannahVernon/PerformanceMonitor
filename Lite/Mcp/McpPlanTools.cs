using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PerformanceMonitorLite.Models;
using PerformanceMonitorLite.Services;

#pragma warning disable CA1707 // MCP tools use snake_case naming convention

namespace PerformanceMonitorLite.Mcp;

[McpServerToolType]
public sealed class McpPlanTools
{
    [McpServerTool(Name = "analyze_query_plan"), Description(
        "Analyzes an execution plan from the plan cache by query_hash. " +
        "Use after get_top_queries_by_cpu to understand why a query is expensive. " +
        "Returns warnings, missing indexes, parameters, memory grants, and top operators.")]
    public static async Task<string> AnalyzeQueryPlan(
        LocalDataService dataService,
        ServerManager serverManager,
        [Description("The query_hash value from get_top_queries_by_cpu.")] string query_hash,
        [Description("Server name or display name.")] string? server_name = null)
    {
        var resolved = ServerResolver.Resolve(serverManager, server_name);
        if (resolved == null)
            return $"Could not resolve server. Available servers:\n{ServerResolver.ListAvailableServers(serverManager)}";

        try
        {
            var xml = await dataService.GetCachedQueryPlanAsync(resolved.Value.ServerId, query_hash);
            if (string.IsNullOrEmpty(xml))
                return $"No plan found for query_hash '{query_hash}'. The query may have been evicted from the plan cache since the last collection.";

            return BuildAnalysisResult(xml, resolved.Value.ServerName, "query_stats", query_hash);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("analyze_query_plan", ex);
        }
    }

    [McpServerTool(Name = "analyze_procedure_plan"), Description(
        "Analyzes an execution plan from procedure stats by plan_handle. " +
        "Use after get_top_procedures_by_cpu to understand why a procedure is expensive. " +
        "Returns warnings, missing indexes, parameters, memory grants, and top operators.")]
    public static async Task<string> AnalyzeProcedurePlan(
        LocalDataService dataService,
        ServerManager serverManager,
        [Description("The plan_handle value from get_top_procedures_by_cpu.")] string plan_handle,
        [Description("Server name or display name.")] string? server_name = null)
    {
        var resolved = ServerResolver.Resolve(serverManager, server_name);
        if (resolved == null)
            return $"Could not resolve server. Available servers:\n{ServerResolver.ListAvailableServers(serverManager)}";

        try
        {
            var xml = await dataService.GetCachedProcedurePlanAsync(resolved.Value.ServerId, plan_handle);
            if (string.IsNullOrEmpty(xml))
                return $"No plan found for plan_handle '{plan_handle}'. The procedure may have been evicted from the plan cache since the last collection.";

            return BuildAnalysisResult(xml, resolved.Value.ServerName, "procedure_stats", plan_handle);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("analyze_procedure_plan", ex);
        }
    }

    [McpServerTool(Name = "analyze_query_store_plan"), Description(
        "Analyzes an execution plan from Query Store by database name and plan ID. " +
        "Fetches the plan on-demand from the monitored SQL Server instance. " +
        "Use after get_query_store_top to understand why a query is expensive.")]
    public static async Task<string> AnalyzeQueryStorePlan(
        ServerManager serverManager,
        [Description("The database_name from get_query_store_top.")] string database_name,
        [Description("The plan_id from get_query_store_top.")] long plan_id,
        [Description("Server name or display name.")] string? server_name = null)
    {
        var resolved = ServerResolver.Resolve(serverManager, server_name);
        if (resolved == null)
            return $"Could not resolve server. Available servers:\n{ServerResolver.ListAvailableServers(serverManager)}";

        try
        {
            /* Find the server connection to build a connection string */
            var server = serverManager.GetEnabledServers().Find(s =>
            {
                var storageName = RemoteCollectorService.GetServerNameForStorage(s);
                return string.Equals(storageName, resolved.Value.ServerName, StringComparison.OrdinalIgnoreCase);
            });

            if (server == null)
                return $"Could not find connection details for server '{resolved.Value.ServerName}'.";

            var connectionString = server.GetConnectionString(serverManager.CredentialService);
            var xml = await LocalDataService.FetchQueryStorePlanAsync(connectionString, database_name, plan_id);

            if (string.IsNullOrEmpty(xml))
                return $"No plan found for plan_id {plan_id} in database '{database_name}'. Query Store may not be enabled or the plan may have been purged.";

            return BuildAnalysisResult(xml, resolved.Value.ServerName, "query_store", $"{database_name}:{plan_id}");
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("analyze_query_store_plan", ex);
        }
    }

    [McpServerTool(Name = "analyze_plan_xml"), Description(
        "Analyzes raw showplan XML directly. Use when you have plan XML from any source " +
        "(clipboard, file, another tool). Returns warnings, missing indexes, parameters, " +
        "memory grants, and top operators.")]
    public static string AnalyzePlanXml(
        [Description("Raw showplan XML content.")] string plan_xml)
    {
        if (string.IsNullOrWhiteSpace(plan_xml))
            return "No plan XML provided.";

        try
        {
            return BuildAnalysisResult(plan_xml, null, "xml", null);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("analyze_plan_xml", ex);
        }
    }

    [McpServerTool(Name = "get_plan_xml"), Description(
        "Returns the raw showplan XML for a query identified by query_hash. " +
        "Use when you need to inspect plan details not captured in the structured analysis. " +
        "Truncated at 500KB.")]
    public static async Task<string> GetPlanXml(
        LocalDataService dataService,
        ServerManager serverManager,
        [Description("The query_hash value from get_top_queries_by_cpu.")] string query_hash,
        [Description("Server name or display name.")] string? server_name = null)
    {
        var resolved = ServerResolver.Resolve(serverManager, server_name);
        if (resolved == null)
            return $"Could not resolve server. Available servers:\n{ServerResolver.ListAvailableServers(serverManager)}";

        try
        {
            var xml = await dataService.GetCachedQueryPlanAsync(resolved.Value.ServerId, query_hash);
            if (string.IsNullOrEmpty(xml))
                return $"No plan found for query_hash '{query_hash}'.";

            return McpHelpers.Truncate(xml, 512_000) ?? "No plan XML available.";
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_plan_xml", ex);
        }
    }

    /// <summary>
    /// Parses plan XML, runs the analyzer, and builds a structured JSON result.
    /// </summary>
    private static string BuildAnalysisResult(string xml, string? serverName, string source, string? identifier)
    {
        var plan = ShowPlanParser.Parse(xml);
        PlanAnalyzer.Analyze(plan);

        var statements = plan.Batches
            .SelectMany(b => b.Statements)
            .Where(s => s.RootNode != null)
            .Select(s =>
            {
                var allNodes = new List<PlanNode>();
                CollectNodes(s.RootNode!, allNodes);

                var nodeWarnings = allNodes
                    .SelectMany(n => n.Warnings)
                    .ToList();
                var stmtWarnings = s.PlanWarnings;
                var allWarnings = stmtWarnings.Concat(nodeWarnings).ToList();

                var hasActuals = allNodes.Any(n => n.HasActualStats);
                var topOps = (hasActuals
                        ? allNodes.OrderByDescending(n => n.ActualElapsedMs)
                        : allNodes.OrderByDescending(n => n.CostPercent))
                    .Take(10)
                    .Select(n => new
                    {
                        node_id = n.NodeId,
                        physical_op = n.PhysicalOp,
                        logical_op = n.LogicalOp,
                        cost_percent = n.CostPercent,
                        estimated_rows = n.EstimateRows,
                        actual_rows = n.HasActualStats ? n.ActualRows : (long?)null,
                        actual_elapsed_ms = n.HasActualStats ? n.ActualElapsedMs : (long?)null,
                        actual_cpu_ms = n.HasActualStats ? n.ActualCPUMs : (long?)null,
                        logical_reads = n.HasActualStats ? n.ActualLogicalReads : (long?)null,
                        object_name = n.ObjectName,
                        index_name = n.IndexName,
                        predicate = McpHelpers.Truncate(n.Predicate, 500),
                        seek_predicates = McpHelpers.Truncate(n.SeekPredicates, 500),
                        warning_count = n.Warnings.Count
                    });

                return new
                {
                    statement_text = McpHelpers.Truncate(s.StatementText, 2000),
                    statement_type = s.StatementType,
                    estimated_cost = Math.Round(s.StatementSubTreeCost, 4),
                    dop = s.DegreeOfParallelism,
                    serial_reason = s.NonParallelPlanReason,
                    compile_cpu_ms = s.CompileCPUMs,
                    compile_memory_kb = s.CompileMemoryKB,
                    cardinality_model = s.CardinalityEstimationModelVersion,
                    query_hash = s.QueryHash,
                    query_plan_hash = s.QueryPlanHash,
                    has_actual_stats = hasActuals,
                    warnings = allWarnings.Select(w => new
                    {
                        severity = w.Severity.ToString(),
                        type = w.WarningType,
                        message = w.Message
                    }),
                    warning_count = allWarnings.Count,
                    critical_count = allWarnings.Count(w => w.Severity == PlanWarningSeverity.Critical),
                    missing_indexes = s.MissingIndexes.Select(idx => new
                    {
                        table = $"{idx.Schema}.{idx.Table}",
                        database = idx.Database,
                        impact = idx.Impact,
                        equality_columns = idx.EqualityColumns,
                        inequality_columns = idx.InequalityColumns,
                        include_columns = idx.IncludeColumns,
                        create_statement = idx.CreateStatement
                    }),
                    parameters = s.Parameters.Select(p => new
                    {
                        name = p.Name,
                        data_type = p.DataType,
                        compiled_value = p.CompiledValue,
                        runtime_value = p.RuntimeValue,
                        sniffing_mismatch = p.CompiledValue != null && p.RuntimeValue != null
                            && p.CompiledValue != p.RuntimeValue
                    }),
                    memory_grant = s.MemoryGrant == null ? null : new
                    {
                        requested_kb = s.MemoryGrant.RequestedMemoryKB,
                        granted_kb = s.MemoryGrant.GrantedMemoryKB,
                        max_used_kb = s.MemoryGrant.MaxUsedMemoryKB,
                        desired_kb = s.MemoryGrant.DesiredMemoryKB,
                        grant_wait_ms = s.MemoryGrant.GrantWaitTimeMs,
                        feedback = s.MemoryGrant.IsMemoryGrantFeedbackAdjusted
                    },
                    top_operators = topOps
                };
            })
            .ToList();

        var totalWarnings = statements.Sum(s => s.warning_count);
        var totalCritical = statements.Sum(s => s.critical_count);
        var totalMissing = statements.Sum(s => s.missing_indexes.Count());

        var result = new
        {
            server = serverName,
            source,
            identifier,
            statement_count = statements.Count,
            total_warnings = totalWarnings,
            total_critical = totalCritical,
            total_missing_indexes = totalMissing,
            statements
        };

        return JsonSerializer.Serialize(result, McpHelpers.JsonOptions);
    }

    private static void CollectNodes(PlanNode node, List<PlanNode> nodes)
    {
        nodes.Add(node);
        foreach (var child in node.Children)
            CollectNodes(child, nodes);
    }
}

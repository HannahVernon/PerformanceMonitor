using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using PerformanceMonitorDashboard.Models;

namespace PerformanceMonitorDashboard.Services;

public static class ShowPlanParser
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static ParsedPlan Parse(string xml)
    {
        var plan = new ParsedPlan { RawXml = xml };

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch
        {
            return plan;
        }

        var root = doc.Root;
        if (root == null) return plan;

        plan.BuildVersion = root.Attribute("Version")?.Value;

        // Standard path: ShowPlanXML → BatchSequence → Batch → Statements
        var batches = root.Descendants(Ns + "Batch");
        foreach (var batchEl in batches)
        {
            var batch = new PlanBatch();
            var statementsEl = batchEl.Element(Ns + "Statements");
            if (statementsEl != null)
            {
                foreach (var stmtEl in statementsEl.Elements())
                {
                    var stmt = ParseStatement(stmtEl);
                    if (stmt != null)
                        batch.Statements.Add(stmt);
                }
            }
            if (batch.Statements.Count > 0)
                plan.Batches.Add(batch);
        }

        // Fallback: some plan XML has StmtSimple directly under QueryPlan
        if (plan.Batches.Count == 0)
        {
            var batch = new PlanBatch();
            foreach (var stmtEl in root.Descendants(Ns + "StmtSimple"))
            {
                var stmt = ParseStatement(stmtEl);
                if (stmt != null)
                    batch.Statements.Add(stmt);
            }
            if (batch.Statements.Count > 0)
                plan.Batches.Add(batch);
        }

        ComputeOperatorCosts(plan);
        return plan;
    }

    private static PlanStatement? ParseStatement(XElement stmtEl)
    {
        var stmt = new PlanStatement
        {
            StatementText = stmtEl.Attribute("StatementText")?.Value ?? "",
            StatementType = stmtEl.Attribute("StatementType")?.Value ?? "",
            StatementSubTreeCost = ParseDouble(stmtEl.Attribute("StatementSubTreeCost")?.Value),
            StatementEstRows = (int)ParseDouble(stmtEl.Attribute("StatementEstRows")?.Value)
        };

        var queryPlanEl = stmtEl.Element(Ns + "QueryPlan");
        if (queryPlanEl == null) return stmt;

        // Memory grant info
        var memEl = queryPlanEl.Element(Ns + "MemoryGrantInfo");
        if (memEl != null)
        {
            stmt.MemoryGrant = new MemoryGrantInfo
            {
                SerialRequiredMemoryKB = ParseLong(memEl.Attribute("SerialRequiredMemory")?.Value),
                SerialDesiredMemoryKB = ParseLong(memEl.Attribute("SerialDesiredMemory")?.Value),
                RequiredMemoryKB = ParseLong(memEl.Attribute("RequiredMemory")?.Value),
                DesiredMemoryKB = ParseLong(memEl.Attribute("DesiredMemory")?.Value),
                RequestedMemoryKB = ParseLong(memEl.Attribute("RequestedMemory")?.Value),
                GrantedMemoryKB = ParseLong(memEl.Attribute("GrantedMemory")?.Value),
                MaxUsedMemoryKB = ParseLong(memEl.Attribute("MaxUsedMemory")?.Value)
            };
        }

        // Statement-level metadata from QueryPlan attributes
        stmt.CachedPlanSizeKB = ParseLong(queryPlanEl.Attribute("CachedPlanSize")?.Value);
        stmt.DegreeOfParallelism = (int)ParseDouble(queryPlanEl.Attribute("DegreeOfParallelism")?.Value);
        stmt.NonParallelPlanReason = queryPlanEl.Attribute("NonParallelPlanReason")?.Value;
        stmt.RetrievedFromCache = queryPlanEl.Attribute("RetrievedFromCache")?.Value is "true" or "1";
        stmt.CompileTimeMs = ParseLong(queryPlanEl.Attribute("CompileTime")?.Value);
        stmt.CompileMemoryKB = ParseLong(queryPlanEl.Attribute("CompileMemory")?.Value);
        stmt.CompileCPUMs = ParseLong(queryPlanEl.Attribute("CompileCPU")?.Value);
        stmt.CardinalityEstimationModelVersion = (int)ParseDouble(queryPlanEl.Attribute("CardinalityEstimationModelVersion")?.Value);
        stmt.QueryHash = stmtEl.Attribute("QueryHash")?.Value;
        stmt.QueryPlanHash = stmtEl.Attribute("QueryPlanHash")?.Value;

        // Missing indexes
        stmt.MissingIndexes = ParseMissingIndexes(queryPlanEl);

        // Root RelOp — wrap in a synthetic statement-type node (SELECT, INSERT, etc.)
        var relOpEl = queryPlanEl.Element(Ns + "RelOp");
        if (relOpEl != null)
        {
            var opNode = ParseRelOp(relOpEl);
            var stmtType = stmt.StatementType.Length > 0
                ? stmt.StatementType.ToUpperInvariant()
                : "QUERY";

            var stmtNode = new PlanNode
            {
                NodeId = -1,
                PhysicalOp = stmtType,
                LogicalOp = stmtType,
                EstimatedTotalSubtreeCost = stmt.StatementSubTreeCost,
                IconName = stmtType switch
                {
                    "SELECT" => "result",
                    "INSERT" => "insert",
                    "UPDATE" => "update",
                    "DELETE" => "delete",
                    _ => "language_construct_catch_all"
                }
            };
            opNode.Parent = stmtNode;
            stmtNode.Children.Add(opNode);
            stmt.RootNode = stmtNode;
        }

        return stmt;
    }

    private static PlanNode ParseRelOp(XElement relOpEl)
    {
        var node = new PlanNode
        {
            NodeId = (int)ParseDouble(relOpEl.Attribute("NodeId")?.Value),
            PhysicalOp = relOpEl.Attribute("PhysicalOp")?.Value ?? "",
            LogicalOp = relOpEl.Attribute("LogicalOp")?.Value ?? "",
            EstimatedTotalSubtreeCost = ParseDouble(relOpEl.Attribute("EstimatedTotalSubtreeCost")?.Value),
            EstimateRows = ParseDouble(relOpEl.Attribute("EstimateRows")?.Value),
            EstimateIO = ParseDouble(relOpEl.Attribute("EstimateIO")?.Value),
            EstimateCPU = ParseDouble(relOpEl.Attribute("EstimateCPU")?.Value),
            EstimateRebinds = ParseDouble(relOpEl.Attribute("EstimateRebinds")?.Value),
            EstimateRewinds = ParseDouble(relOpEl.Attribute("EstimateRewinds")?.Value),
            EstimatedRowSize = (int)ParseDouble(relOpEl.Attribute("AvgRowSize")?.Value),
            Parallel = relOpEl.Attribute("Parallel")?.Value == "true" || relOpEl.Attribute("Parallel")?.Value == "1",
            ExecutionMode = relOpEl.Attribute("EstimatedExecutionMode")?.Value
        };

        // Map to icon
        node.IconName = PlanIconMapper.GetIconName(node.PhysicalOp);

        // Handle special icon cases
        var physicalOpEl = GetOperatorElement(relOpEl);
        if (physicalOpEl != null)
        {
            // Object reference (table/index name) — scoped to stop at child RelOps
            var objEl = ScopedDescendants(physicalOpEl, Ns + "Object").FirstOrDefault();
            if (objEl != null)
            {
                var db = objEl.Attribute("Database")?.Value?.Replace("[", "").Replace("]", "");
                var schema = objEl.Attribute("Schema")?.Value?.Replace("[", "").Replace("]", "");
                var table = objEl.Attribute("Table")?.Value?.Replace("[", "").Replace("]", "");
                var index = objEl.Attribute("Index")?.Value?.Replace("[", "").Replace("]", "");

                node.DatabaseName = db;
                node.IndexName = index;

                // Short name for node display: Schema.Table
                var shortParts = new List<string>();
                if (!string.IsNullOrEmpty(schema)) shortParts.Add(schema);
                if (!string.IsNullOrEmpty(table)) shortParts.Add(table);
                node.ObjectName = shortParts.Count > 0 ? string.Join(".", shortParts) : null;

                // Full qualified name: Database.Schema.Table (Index)
                var fullParts = new List<string>();
                if (!string.IsNullOrEmpty(db)) fullParts.Add(db);
                if (!string.IsNullOrEmpty(schema)) fullParts.Add(schema);
                if (!string.IsNullOrEmpty(table)) fullParts.Add(table);
                var fullName = string.Join(".", fullParts);
                if (!string.IsNullOrEmpty(index))
                    fullName += $".{index}";
                node.FullObjectName = !string.IsNullOrEmpty(fullName) ? fullName : null;

                // Storage type (Heap, Clustered, etc.)
                node.StorageType = objEl.Attribute("Storage")?.Value;
            }

            // Hash keys for hash match operators
            var hashKeysProbeEl = physicalOpEl.Element(Ns + "HashKeysProbe");
            if (hashKeysProbeEl != null)
            {
                var cols = hashKeysProbeEl.Elements(Ns + "ColumnReference")
                    .Select(c => FormatColumnRef(c))
                    .Where(s => !string.IsNullOrEmpty(s));
                node.HashKeysProbe = string.Join(", ", cols);
            }
            var hashKeysBuildEl = physicalOpEl.Element(Ns + "HashKeysBuild");
            if (hashKeysBuildEl != null)
            {
                var cols = hashKeysBuildEl.Elements(Ns + "ColumnReference")
                    .Select(c => FormatColumnRef(c))
                    .Where(s => !string.IsNullOrEmpty(s));
                node.HashKeysBuild = string.Join(", ", cols);
            }

            // Ordered attribute
            node.Ordered = physicalOpEl.Attribute("Ordered")?.Value == "true" || physicalOpEl.Attribute("Ordered")?.Value == "1";

            // Seek predicates — scoped to stop at child RelOps
            var seekPreds = ScopedDescendants(physicalOpEl, Ns + "SeekPredicateNew")
                .Concat(ScopedDescendants(physicalOpEl, Ns + "SeekPredicate"));
            var seekParts = new List<string>();
            foreach (var sp in seekPreds)
            {
                var scalarOps = sp.Descendants(Ns + "ScalarOperator");
                foreach (var so in scalarOps)
                {
                    var val = so.Attribute("ScalarString")?.Value;
                    if (!string.IsNullOrEmpty(val))
                        seekParts.Add(val);
                }
            }
            if (seekParts.Count > 0)
                node.SeekPredicates = string.Join(" AND ", seekParts);

            // Residual predicate
            var predEl = physicalOpEl.Elements(Ns + "Predicate").FirstOrDefault();
            if (predEl != null)
            {
                var scalarOp = predEl.Descendants(Ns + "ScalarOperator").FirstOrDefault();
                node.Predicate = scalarOp?.Attribute("ScalarString")?.Value;
            }

            // Partitioning type (for parallelism operators)
            node.PartitioningType = physicalOpEl.Attribute("PartitioningType")?.Value;

            // Build/Probe residuals (Hash Match)
            var buildResEl = physicalOpEl.Element(Ns + "BuildResidual");
            if (buildResEl != null)
            {
                var so = buildResEl.Descendants(Ns + "ScalarOperator").FirstOrDefault();
                node.BuildResidual = so?.Attribute("ScalarString")?.Value;
            }
            var probeResEl = physicalOpEl.Element(Ns + "ProbeResidual");
            if (probeResEl != null)
            {
                var so = probeResEl.Descendants(Ns + "ScalarOperator").FirstOrDefault();
                node.ProbeResidual = so?.Attribute("ScalarString")?.Value;
            }

            // OrderBy columns (Sort operator)
            var orderByEl = physicalOpEl.Element(Ns + "OrderBy");
            if (orderByEl != null)
            {
                var obParts = orderByEl.Elements(Ns + "OrderByColumn")
                    .Select(obc =>
                    {
                        var ascending = obc.Attribute("Ascending")?.Value != "false";
                        var colRef = obc.Element(Ns + "ColumnReference");
                        var name = colRef != null ? FormatColumnRef(colRef) : "";
                        return string.IsNullOrEmpty(name) ? "" : $"{name} {(ascending ? "ASC" : "DESC")}";
                    })
                    .Where(s => !string.IsNullOrEmpty(s));
                var obStr = string.Join(", ", obParts);
                if (!string.IsNullOrEmpty(obStr))
                    node.OrderBy = obStr;
            }

            // OuterReferences (Nested Loops)
            var outerRefsEl = physicalOpEl.Element(Ns + "OuterReferences");
            if (outerRefsEl != null)
            {
                var refs = outerRefsEl.Elements(Ns + "ColumnReference")
                    .Select(c => FormatColumnRef(c))
                    .Where(s => !string.IsNullOrEmpty(s));
                var refsStr = string.Join(", ", refs);
                if (!string.IsNullOrEmpty(refsStr))
                    node.OuterReferences = refsStr;
            }

            // Inner/Outer side join columns (Merge Join)
            node.InnerSideJoinColumns = ParseColumnList(physicalOpEl, "InnerSideJoinColumns");
            node.OuterSideJoinColumns = ParseColumnList(physicalOpEl, "OuterSideJoinColumns");

            // GroupBy columns (Hash/Stream Aggregate)
            node.GroupBy = ParseColumnList(physicalOpEl, "GroupBy");

            // Partition columns (Parallelism)
            node.PartitionColumns = ParseColumnList(physicalOpEl, "PartitionColumns");

            // Segment column
            var segColEl = physicalOpEl.Element(Ns + "SegmentColumn")?.Element(Ns + "ColumnReference");
            if (segColEl != null)
                node.SegmentColumn = FormatColumnRef(segColEl);

            // Defined values (Compute Scalar)
            var definedValsEl = physicalOpEl.Element(Ns + "DefinedValues");
            if (definedValsEl != null)
            {
                var dvParts = new List<string>();
                foreach (var dvEl in definedValsEl.Elements(Ns + "DefinedValue"))
                {
                    var colRef = dvEl.Element(Ns + "ColumnReference");
                    var scalarOp = dvEl.Element(Ns + "ScalarOperator");
                    var colName = colRef != null ? FormatColumnRef(colRef) : "";
                    var expr = scalarOp?.Attribute("ScalarString")?.Value ?? "";
                    if (!string.IsNullOrEmpty(colName) && !string.IsNullOrEmpty(expr))
                        dvParts.Add($"{colName} = {expr}");
                    else if (!string.IsNullOrEmpty(expr))
                        dvParts.Add(expr);
                    else if (!string.IsNullOrEmpty(colName))
                        dvParts.Add(colName);
                }
                if (dvParts.Count > 0)
                    node.DefinedValues = string.Join("; ", dvParts);
            }

            // Scan direction
            node.ScanDirection = physicalOpEl.Attribute("ScanDirection")?.Value;

            // Forced index / scan / seek hints
            node.ForcedIndex = physicalOpEl.Attribute("ForcedIndex")?.Value is "true" or "1";
            node.ForceScan = physicalOpEl.Attribute("ForceScan")?.Value is "true" or "1";
            node.ForceSeek = physicalOpEl.Attribute("ForceSeek")?.Value is "true" or "1";
            node.NoExpandHint = physicalOpEl.Attribute("NoExpandHint")?.Value is "true" or "1";

            // Table cardinality and rows to be read (these are on <RelOp>, not the physical op element)
            node.TableCardinality = ParseDouble(relOpEl.Attribute("TableCardinality")?.Value);
            node.EstimatedRowsRead = ParseDouble(relOpEl.Attribute("EstimatedRowsRead")?.Value);
            if (node.EstimatedRowsRead == 0)
                node.EstimatedRowsRead = ParseDouble(relOpEl.Attribute("EstimateRowsWithoutRowGoal")?.Value);

            // TOP operator properties
            var topExprEl = physicalOpEl.Element(Ns + "TopExpression")?.Descendants(Ns + "ScalarOperator").FirstOrDefault();
            if (topExprEl != null)
                node.TopExpression = topExprEl.Attribute("ScalarString")?.Value;
            node.IsPercent = physicalOpEl.Attribute("IsPercent")?.Value is "true" or "1";

            // SET predicate (UPDATE operator)
            var setPredicateEl = physicalOpEl.Element(Ns + "SetPredicate");
            if (setPredicateEl != null)
            {
                var so = setPredicateEl.Descendants(Ns + "ScalarOperator").FirstOrDefault();
                node.SetPredicate = so?.Attribute("ScalarString")?.Value;
            }

            // Hash Match: ManyToMany
            node.ManyToMany = physicalOpEl.Attribute("ManyToMany")?.Value is "true" or "1";

            // Adaptive join properties
            node.IsAdaptive = physicalOpEl.Attribute("IsAdaptive")?.Value is "true" or "1";
            node.AdaptiveThresholdRows = ParseDouble(physicalOpEl.Attribute("AdaptiveThresholdRows")?.Value);
            node.EstimatedJoinType = physicalOpEl.Attribute("EstimatedJoinType")?.Value;
            node.ActualJoinType = physicalOpEl.Attribute("ActualJoinType")?.Value;
        }

        // Output columns
        var outputList = relOpEl.Element(Ns + "OutputList");
        if (outputList != null)
        {
            var cols = outputList.Elements(Ns + "ColumnReference")
                .Select(c =>
                {
                    var col = c.Attribute("Column")?.Value ?? "";
                    var tbl = c.Attribute("Table")?.Value ?? "";
                    return string.IsNullOrEmpty(tbl) ? col : $"{tbl}.{col}";
                })
                .Where(s => !string.IsNullOrEmpty(s));
            var colList = string.Join(", ", cols);
            if (!string.IsNullOrEmpty(colList))
                node.OutputColumns = colList.Replace("[", "").Replace("]", "");
        }

        // Warnings
        node.Warnings = ParseWarnings(relOpEl);

        // Runtime information (actual plan)
        var runtimeEl = relOpEl.Element(Ns + "RunTimeInformation");
        if (runtimeEl != null)
        {
            node.HasActualStats = true;
            long totalRows = 0, totalExecutions = 0, totalRowsRead = 0;
            long totalRebinds = 0, totalRewinds = 0;
            long maxElapsed = 0, totalCpu = 0;
            long totalLogicalReads = 0, totalPhysicalReads = 0;
            long totalScans = 0, totalReadAheads = 0;
            long totalLobLogicalReads = 0, totalLobPhysicalReads = 0, totalLobReadAheads = 0;
            string? actualExecMode = null;

            foreach (var thread in runtimeEl.Elements(Ns + "RunTimeCountersPerThread"))
            {
                totalRows += ParseLong(thread.Attribute("ActualRows")?.Value);
                totalExecutions += ParseLong(thread.Attribute("ActualExecutions")?.Value);
                totalRowsRead += ParseLong(thread.Attribute("ActualRowsRead")?.Value);
                totalRebinds += ParseLong(thread.Attribute("ActualRebinds")?.Value);
                totalRewinds += ParseLong(thread.Attribute("ActualRewinds")?.Value);
                totalCpu += ParseLong(thread.Attribute("ActualCPUms")?.Value);
                totalLogicalReads += ParseLong(thread.Attribute("ActualLogicalReads")?.Value);
                totalPhysicalReads += ParseLong(thread.Attribute("ActualPhysicalReads")?.Value);
                totalScans += ParseLong(thread.Attribute("ActualScans")?.Value);
                totalReadAheads += ParseLong(thread.Attribute("ActualReadAheads")?.Value);
                totalLobLogicalReads += ParseLong(thread.Attribute("ActualLobLogicalReads")?.Value);
                totalLobPhysicalReads += ParseLong(thread.Attribute("ActualLobPhysicalReads")?.Value);
                totalLobReadAheads += ParseLong(thread.Attribute("ActualLobReadAheads")?.Value);

                actualExecMode ??= thread.Attribute("ActualExecutionMode")?.Value;

                var elapsed = ParseLong(thread.Attribute("ActualElapsedms")?.Value);
                if (elapsed > maxElapsed) maxElapsed = elapsed;
            }

            node.ActualRows = totalRows;
            node.ActualExecutions = totalExecutions;
            node.ActualRowsRead = totalRowsRead;
            node.ActualRebinds = totalRebinds;
            node.ActualRewinds = totalRewinds;
            node.ActualElapsedMs = maxElapsed;
            node.ActualCPUMs = totalCpu;
            node.ActualLogicalReads = totalLogicalReads;
            node.ActualPhysicalReads = totalPhysicalReads;
            node.ActualScans = totalScans;
            node.ActualReadAheads = totalReadAheads;
            node.ActualLobLogicalReads = totalLobLogicalReads;
            node.ActualLobPhysicalReads = totalLobPhysicalReads;
            node.ActualLobReadAheads = totalLobReadAheads;
            node.ActualExecutionMode = actualExecMode;
        }

        // Recurse into child RelOps
        foreach (var childRelOp in FindChildRelOps(relOpEl))
        {
            var childNode = ParseRelOp(childRelOp);
            childNode.Parent = node;
            node.Children.Add(childNode);
        }

        return node;
    }

    private static XElement? GetOperatorElement(XElement relOpEl)
    {
        // The operator-specific element is the first child that isn't OutputList, RunTimeInformation, etc.
        foreach (var child in relOpEl.Elements())
        {
            var name = child.Name.LocalName;
            if (name != "OutputList" && name != "RunTimeInformation" && name != "Warnings"
                && name != "MemoryFractions" && name != "RunTimePartitionSummary"
                && name != "InternalInfo")
            {
                return child;
            }
        }
        return null;
    }

    private static IEnumerable<XElement> FindChildRelOps(XElement relOpEl)
    {
        // Child RelOps are nested inside the operator-specific element
        var operatorEl = GetOperatorElement(relOpEl);
        if (operatorEl == null) yield break;

        // Direct RelOp children of the operator element
        foreach (var child in operatorEl.Elements(Ns + "RelOp"))
            yield return child;

        // Some operators nest RelOps deeper (e.g., Hash has BuildResidual/ProbeResidual)
        // Walk one level of non-RelOp children to find nested RelOps
        foreach (var child in operatorEl.Elements())
        {
            if (child.Name.LocalName == "RelOp") continue; // Already yielded
            foreach (var nestedRelOp in child.Elements(Ns + "RelOp"))
                yield return nestedRelOp;
        }
    }

    private static List<MissingIndex> ParseMissingIndexes(XElement queryPlanEl)
    {
        var result = new List<MissingIndex>();
        var missingIndexesEl = queryPlanEl.Element(Ns + "MissingIndexes");
        if (missingIndexesEl == null) return result;

        foreach (var groupEl in missingIndexesEl.Elements(Ns + "MissingIndexGroup"))
        {
            var impact = ParseDouble(groupEl.Attribute("Impact")?.Value);
            foreach (var indexEl in groupEl.Elements(Ns + "MissingIndex"))
            {
                var mi = new MissingIndex
                {
                    Database = indexEl.Attribute("Database")?.Value?.Replace("[", "").Replace("]", "") ?? "",
                    Schema = indexEl.Attribute("Schema")?.Value?.Replace("[", "").Replace("]", "") ?? "",
                    Table = indexEl.Attribute("Table")?.Value?.Replace("[", "").Replace("]", "") ?? "",
                    Impact = impact
                };

                foreach (var colGroup in indexEl.Elements(Ns + "ColumnGroup"))
                {
                    var usage = colGroup.Attribute("Usage")?.Value ?? "";
                    var cols = colGroup.Elements(Ns + "Column")
                        .Select(c => c.Attribute("Name")?.Value?.Replace("[", "").Replace("]", "") ?? "")
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();

                    switch (usage)
                    {
                        case "EQUALITY": mi.EqualityColumns = cols; break;
                        case "INEQUALITY": mi.InequalityColumns = cols; break;
                        case "INCLUDE": mi.IncludeColumns = cols; break;
                    }
                }

                // Generate CREATE INDEX statement
                var keyCols = mi.EqualityColumns.Concat(mi.InequalityColumns).ToList();
                if (keyCols.Count > 0)
                {
                    var create = $"CREATE NONCLUSTERED INDEX [IX_{mi.Table}_{string.Join("_", keyCols.Take(3))}]\nON {mi.Schema}.{mi.Table} ({string.Join(", ", keyCols)})";
                    if (mi.IncludeColumns.Count > 0)
                        create += $"\nINCLUDE ({string.Join(", ", mi.IncludeColumns)})";
                    mi.CreateStatement = create;
                }

                result.Add(mi);
            }
        }
        return result;
    }

    private static List<PlanWarning> ParseWarnings(XElement relOpEl)
    {
        var result = new List<PlanWarning>();
        var warningsEl = relOpEl.Element(Ns + "Warnings");
        if (warningsEl == null) return result;

        // No join predicate
        if (warningsEl.Attribute("NoJoinPredicate")?.Value is "true" or "1")
        {
            result.Add(new PlanWarning
            {
                WarningType = "No Join Predicate",
                Message = "This join has no join predicate (possible cross join)",
                Severity = PlanWarningSeverity.Critical
            });
        }

        // Spill to TempDb
        foreach (var spillEl in warningsEl.Elements(Ns + "SpillToTempDb"))
        {
            var spillLevel = spillEl.Attribute("SpillLevel")?.Value ?? "?";
            var threadCount = spillEl.Attribute("SpilledThreadCount")?.Value ?? "?";
            result.Add(new PlanWarning
            {
                WarningType = "Spill to TempDb",
                Message = $"Spill level {spillLevel}, {threadCount} thread(s)",
                Severity = PlanWarningSeverity.Warning
            });
        }

        // Memory grant warning
        var memWarnEl = warningsEl.Element(Ns + "MemoryGrantWarning");
        if (memWarnEl != null)
        {
            var kind = memWarnEl.Attribute("GrantWarningKind")?.Value ?? "Unknown";
            var requested = ParseLong(memWarnEl.Attribute("RequestedMemory")?.Value);
            var granted = ParseLong(memWarnEl.Attribute("GrantedMemory")?.Value);
            var maxUsed = ParseLong(memWarnEl.Attribute("MaxUsedMemory")?.Value);
            result.Add(new PlanWarning
            {
                WarningType = "Memory Grant",
                Message = $"{kind}: Requested {requested:N0} KB, Granted {granted:N0} KB, Used {maxUsed:N0} KB",
                Severity = PlanWarningSeverity.Warning
            });
        }

        // Implicit conversions
        foreach (var convertEl in warningsEl.Elements(Ns + "PlanAffectingConvert"))
        {
            var issue = convertEl.Attribute("ConvertIssue")?.Value ?? "Unknown";
            var expr = convertEl.Attribute("Expression")?.Value ?? "";
            result.Add(new PlanWarning
            {
                WarningType = "Implicit Conversion",
                Message = $"{issue}: {expr}",
                Severity = issue.Contains("Cardinality") ? PlanWarningSeverity.Warning : PlanWarningSeverity.Critical
            });
        }

        // Columns with no statistics
        var noStatsEl = warningsEl.Element(Ns + "ColumnsWithNoStatistics");
        if (noStatsEl != null)
        {
            var cols = noStatsEl.Elements(Ns + "ColumnReference")
                .Select(c => c.Attribute("Column")?.Value ?? "")
                .Where(s => !string.IsNullOrEmpty(s));
            result.Add(new PlanWarning
            {
                WarningType = "Missing Statistics",
                Message = $"No statistics on: {string.Join(", ", cols)}",
                Severity = PlanWarningSeverity.Warning
            });
        }

        // Wait warnings
        foreach (var waitEl in warningsEl.Elements(Ns + "Wait"))
        {
            result.Add(new PlanWarning
            {
                WarningType = "Wait",
                Message = $"{waitEl.Attribute("WaitType")?.Value}: {waitEl.Attribute("WaitTime")?.Value}ms",
                Severity = PlanWarningSeverity.Info
            });
        }

        return result;
    }

    private static void ComputeOperatorCosts(ParsedPlan plan)
    {
        foreach (var batch in plan.Batches)
        {
            foreach (var stmt in batch.Statements)
            {
                if (stmt.RootNode == null) continue;
                var totalCost = stmt.StatementSubTreeCost > 0
                    ? stmt.StatementSubTreeCost
                    : stmt.RootNode.EstimatedTotalSubtreeCost;
                if (totalCost <= 0) totalCost = 1; // Avoid division by zero
                ComputeNodeCosts(stmt.RootNode, totalCost);
            }
        }
    }

    private static void ComputeNodeCosts(PlanNode node, double totalStatementCost)
    {
        // Operator cost = subtree cost - sum of children's subtree costs
        var childrenSubtreeCost = node.Children.Sum(c => c.EstimatedTotalSubtreeCost);
        node.EstimatedOperatorCost = Math.Max(0, node.EstimatedTotalSubtreeCost - childrenSubtreeCost);
        node.CostPercent = (int)Math.Round((node.EstimatedOperatorCost / totalStatementCost) * 100);
        node.CostPercent = Math.Min(100, Math.Max(0, node.CostPercent));

        foreach (var child in node.Children)
            ComputeNodeCosts(child, totalStatementCost);
    }

    /// <summary>
    /// Like Descendants() but stops at RelOp boundaries to prevent
    /// picking up properties from child operators.
    /// </summary>
    private static IEnumerable<XElement> ScopedDescendants(XElement element, XName name)
    {
        foreach (var child in element.Elements())
        {
            if (child.Name == Ns + "RelOp") continue;
            if (child.Name == name) yield return child;
            foreach (var desc in ScopedDescendants(child, name))
                yield return desc;
        }
    }

    private static string? ParseColumnList(XElement parent, string elementName)
    {
        var el = parent.Element(Ns + elementName);
        if (el == null) return null;
        var cols = el.Elements(Ns + "ColumnReference")
            .Select(c => FormatColumnRef(c))
            .Where(s => !string.IsNullOrEmpty(s));
        var result = string.Join(", ", cols);
        return string.IsNullOrEmpty(result) ? null : result;
    }

    private static string FormatColumnRef(XElement colRef)
    {
        var col = colRef.Attribute("Column")?.Value ?? "";
        var tbl = colRef.Attribute("Table")?.Value ?? "";
        var result = string.IsNullOrEmpty(tbl) ? col : $"{tbl}.{col}";
        return result.Replace("[", "").Replace("]", "");
    }

    private static double ParseDouble(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        return double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private static long ParseLong(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        return long.TryParse(value, out var result) ? result : 0;
    }
}

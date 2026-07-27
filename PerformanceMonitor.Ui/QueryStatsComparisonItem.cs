/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Ui;

public class QueryStatsComparisonItem : ComparisonItemBase
{
    /// <summary>Gates "Get Actual Plan (re-run)" in Lite's query-grid plan menu — the re-run executes this row's
    /// query text. The sibling ProcedureStatsComparisonItem deliberately has no such property: the re-run handler
    /// has no case for it, so its menu binding falls back to Collapsed and the verb is hidden there.</summary>
    public bool CanGetActualPlan => !string.IsNullOrEmpty(QueryText);

    public string QueryHash { get; set; } = "";
    public string? ObjectName { get; set; }
    public string? SchemaName { get; set; }
    public string? ObjectType { get; set; }
    public string QueryText { get; set; } = "";
}

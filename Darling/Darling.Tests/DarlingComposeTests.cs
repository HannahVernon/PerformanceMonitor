/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The ungated (no-live-store) contract for the Custom Views v2 backend (#1563): the hand-authored measure
/// catalog pinned to the real collector columns (drift = Erik's #1 pain), the compose spec validator (the
/// write-time + compile authority), the SQL compiler's safety invariants (catalog-only identifiers,
/// schema-qualified collect.*, every value bound, archetype-gated aggs, window-bounded #1568 join, DoS ceiling),
/// the ValidateDefinition v1/v2 dispatch, the /api/catalog v2 body, and the statement_timeout provisioning +
/// loopback-comment scrub. The live compile-and-run round-trip is exercised against a real store elsewhere; this
/// pins the pure, no-DB contract.
/// </summary>
public sealed class DarlingComposeTests
{
    /* ─────────────────────────── catalog pins (drift-proofing) ─────────────────────────── */

    private static Dictionary<string, HashSet<string>> PayloadColumnsByTable() =>
        CollectorCatalog.All.ToDictionary(
            c => c.TargetTable,
            c => c.PayloadColumns.Select(p => p.Name).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

    [Fact]
    public void EveryMeasureSource_IsARealCollectorTable()
    {
        var tables = CollectorCatalog.All.Select(c => c.TargetTable).ToHashSet(StringComparer.Ordinal);
        foreach (var measure in MeasureCatalog.Measures)
        {
            Assert.True(tables.Contains(measure.SourceTable),
                $"measure '{measure.Key}' names source '{measure.SourceTable}', which is not a collector table.");
        }
    }

    [Fact]
    public void NoMeasureSource_IsAConfigTable()
    {
        /* The structural config-exclusion guarantee (design §3): a composed query can only ever name a collect
           collector table, so it can never read a config control-plane table (hostnames, SMTP recipients, …).
           Every collector TargetTable is a collect-schema table; none begins with the config_ prefix. */
        foreach (var measure in MeasureCatalog.Measures)
        {
            Assert.False(measure.SourceTable.StartsWith("config", StringComparison.Ordinal),
                $"measure '{measure.Key}' resolves to a config table '{measure.SourceTable}'.");
        }
    }

    [Fact]
    public void EveryScalarMeasureColumn_IsARealPayloadColumn()
    {
        var payload = PayloadColumnsByTable();
        foreach (var measure in MeasureCatalog.Measures.Where(m => m.Kind == MeasureKind.Scalar))
        {
            var columns = payload[measure.SourceTable];
            Assert.True(measure.Column is not null && columns.Contains(measure.Column),
                $"measure '{measure.Key}' column '{measure.Column}' is not a payload column of '{measure.SourceTable}'.");

            /* A cumulative counter's delta column (what the aggregate actually operates on) must be real too. */
            if (measure.Archetype == MeasureArchetype.Cumulative)
            {
                Assert.True(measure.DeltaColumn is not null && columns.Contains(measure.DeltaColumn),
                    $"cumulative measure '{measure.Key}' delta column '{measure.DeltaColumn}' is not a payload column of '{measure.SourceTable}'.");
            }
        }
    }

    [Fact]
    public void EveryRatioMeasure_ReferencesRealSameSourceScalars()
    {
        foreach (var ratio in MeasureCatalog.Measures.Where(m => m.Kind == MeasureKind.Ratio))
        {
            var numerator = MeasureCatalog.Measure(ratio.NumeratorKey);
            var denominator = MeasureCatalog.Measure(ratio.DenominatorKey);
            Assert.True(numerator is not null, $"ratio '{ratio.Key}' numerator '{ratio.NumeratorKey}' is not a measure.");
            Assert.True(denominator is not null, $"ratio '{ratio.Key}' denominator '{ratio.DenominatorKey}' is not a measure.");
            Assert.Equal(MeasureKind.Scalar, numerator!.Kind);
            Assert.Equal(MeasureKind.Scalar, denominator!.Kind);
            Assert.Equal(ratio.SourceTable, numerator.SourceTable);
            Assert.Equal(ratio.SourceTable, denominator.SourceTable);
        }
    }

    [Fact]
    public void EveryDimensionColumn_IsARealPayloadColumn_ExceptTheModuleJoin()
    {
        var payload = PayloadColumnsByTable();
        foreach (var dimension in MeasureCatalog.Dimensions)
        {
            /* The #1568 object_name is stitched from procedure_stats, not a column of the fact source. */
            var table = dimension.ViaModuleJoin ? "procedure_stats" : dimension.SourceTable;
            Assert.True(payload[table].Contains(dimension.Column),
                $"dimension '{dimension.SourceTable}.{dimension.Name}' column '{dimension.Column}' is not a payload column of '{table}'.");
        }
    }

    [Fact]
    public void EveryMeasureAllowedDimension_IsADeclaredDimensionOfItsSource()
    {
        foreach (var measure in MeasureCatalog.Measures)
        {
            foreach (var dimensionName in measure.AllowedDimensions)
            {
                Assert.True(MeasureCatalog.Dimension(measure.SourceTable, dimensionName) is not null,
                    $"measure '{measure.Key}' allows dimension '{dimensionName}', which is not declared for source '{measure.SourceTable}'.");
            }
        }
    }

    [Fact]
    public void Gauges_AreNeverSummable()
    {
        /* The grain-trap guard: a gauge has no aggregation delta column and never offers SUM. */
        foreach (var gauge in MeasureCatalog.Measures.Where(m => m.Archetype == MeasureArchetype.Gauge))
        {
            Assert.Null(gauge.AggregationColumn);
            Assert.DoesNotContain(ComposeAggregate.Sum, gauge.ValidAggs);
        }
    }

    [Fact]
    public void PercentileCont_IsOfferedOnlyByPerEventMeasures()
    {
        foreach (var measure in MeasureCatalog.Measures)
        {
            if (measure.ValidAggs.Contains(ComposeAggregate.PercentileCont))
            {
                Assert.Equal(MeasureArchetype.PerEvent, measure.Archetype);
            }
        }
    }

    [Fact]
    public void TheSlice_ProvesEveryArchetype()
    {
        var archetypes = MeasureCatalog.Measures.Where(m => m.Kind == MeasureKind.Scalar).Select(m => m.Archetype).ToHashSet();
        Assert.Contains(MeasureArchetype.Cumulative, archetypes);
        Assert.Contains(MeasureArchetype.Delta, archetypes);
        Assert.Contains(MeasureArchetype.Gauge, archetypes);
        Assert.Contains(MeasureArchetype.PerEvent, archetypes);
        Assert.Contains(MeasureCatalog.Measures, m => m.Kind == MeasureKind.Ratio);
    }

    /* ─────────────────────────── the compose spec validator ─────────────────────────── */

    private static JsonObject PanelJson(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static PanelPlan ValidPlan(string json, params string[] declaredVariables)
    {
        var (plan, error) = ComposeSpec.TryParsePanel(PanelJson(json), declaredVariables);
        Assert.True(error is null, error);
        Assert.NotNull(plan);
        return plan!;
    }

    private static string RejectReason(string json, params string[] declaredVariables)
    {
        var (plan, error) = ComposeSpec.TryParsePanel(PanelJson(json), declaredVariables);
        Assert.Null(plan);
        Assert.NotNull(error);
        return error!;
    }

    [Fact]
    public void TryParsePanel_AcceptsAWellFormedScalarTimeSeries()
    {
        var plan = ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"minute\",\"viz\":\"line\",\"groupBy\":[\"wait_type\"]}");
        Assert.Equal(PanelMode.TimeSeries, plan.Mode);
        Assert.Equal(ComposeAggregate.Sum, plan.Aggregate);
        Assert.Single(plan.GroupBy);
    }

    [Fact]
    public void TryParsePanel_AcceptsARankedBar()
    {
        var plan = ValidPlan("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"table\"}");
        Assert.Equal(PanelMode.Ranked, plan.Mode);
        Assert.Equal(10, plan.TopN);
    }

    [Fact]
    public void TryParsePanel_AcceptsARatio()
    {
        var plan = ValidPlan("{\"source\":\"wait_stats\",\"ratio\":\"signal_wait_pct\",\"timeBucket\":\"minute\",\"viz\":\"line\"}");
        Assert.Equal(MeasureKind.Ratio, plan.Measure.Kind);
        Assert.Equal("percent", plan.Unit);
    }

    [Fact]
    public void TryParsePanel_AcceptsAModuleNameLikeFilter_OnAVariable()
    {
        var plan = ValidPlan(
            "{\"source\":\"procedure_stats\",\"measure\":\"proc_elapsed_us\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\"," +
            "\"filters\":[{\"dimension\":\"object_name\",\"op\":\"like\",\"value\":\"dbo.usp_Payment%\"},{\"dimension\":\"database_name\",\"op\":\"eq\",\"value\":\"$db\"}]}",
            "db");
        Assert.Equal(2, plan.Filters.Count);
        Assert.True(plan.Filters[1].Value.IsVariable);
    }

    [Theory]
    [InlineData("{\"source\":\"nope\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"table\"}", "unknown source")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"nope\",\"aggregate\":\"sum\",\"viz\":\"table\"}", "unknown measure")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"viz\":\"table\"}", "is not on source")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"signal_wait_pct\",\"aggregate\":\"sum\",\"viz\":\"table\"}", "reference it as 'ratio'")]
    [InlineData("{\"source\":\"wait_stats\",\"ratio\":\"wait_time_ms\",\"viz\":\"table\"}", "reference it as 'measure'")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"viz\":\"table\"}", "missing 'aggregate'")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"nope\",\"viz\":\"table\"}", "unknown aggregate")]
    /* SUM on a gauge — the grain-trap the archetype gate blocks. */
    [InlineData("{\"source\":\"cpu_utilization_stats\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"sum\",\"viz\":\"table\"}", "not valid for measure")]
    /* percentile on a non-per-event measure. */
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"percentile_cont\",\"viz\":\"table\"}", "not valid for measure")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"unit\":\"gb\",\"viz\":\"table\"}", "not valid for measure")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"nope\"}", "unknown or missing viz")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"nope\",\"viz\":\"line\"}", "unknown timeBucket")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"minute\",\"topN\":5,\"viz\":\"line\"}", "cannot set both")]
    /* like on a non-likeable dimension (query_hash). */
    [InlineData("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"viz\":\"table\",\"filters\":[{\"dimension\":\"query_hash\",\"op\":\"like\",\"value\":\"x\"}]}", "not allowed on dimension")]
    /* a dimension the measure does not allow. */
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"table\",\"groupBy\":[\"database_name\"]}", "not allowed for measure")]
    public void TryParsePanel_RejectsOffCatalog_NamingTheReason(string json, string expectedFragment)
    {
        var reason = RejectReason(json);
        Assert.Contains(expectedFragment, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParsePanel_RejectsAnUndeclaredVariable()
    {
        var reason = RejectReason(
            "{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"table\",\"filters\":[{\"dimension\":\"wait_type\",\"op\":\"eq\",\"value\":\"$undeclared\"}]}");
        Assert.Contains("undeclared variable", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParsePanel_RejectsTooManyFilters()
    {
        var filters = string.Join(",", Enumerable.Repeat("{\"dimension\":\"wait_type\",\"op\":\"eq\",\"value\":\"x\"}", ComposeLimits.MaxFilters + 1));
        var reason = RejectReason("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"table\",\"filters\":[" + filters + "]}");
        Assert.Contains("maximum", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseVariables_RejectsUnknownDimension_AndDuplicates()
    {
        Assert.NotNull(ComposeSpec.ParseVariables(JsonNode.Parse("[{\"name\":\"x\",\"dimension\":\"nope\"}]")).Error);
        Assert.NotNull(ComposeSpec.ParseVariables(JsonNode.Parse("[{\"name\":\"x\",\"dimension\":\"server\"},{\"name\":\"x\",\"dimension\":\"database_name\"}]")).Error);
        Assert.Null(ComposeSpec.ParseVariables(JsonNode.Parse("[{\"name\":\"srv\",\"dimension\":\"server\"}]")).Error);
    }

    [Theory]
    [InlineData("{\"hours\":24}", true)]
    [InlineData("{\"hours\":0}", false)]
    [InlineData("{\"hours\":100000}", false)]
    public void ParseRange_BoundsHours(string json, bool valid)
    {
        var (_, error) = ComposeSpec.ParseRange(JsonNode.Parse(json));
        Assert.Equal(valid, error is null);
    }

    /* ─────────────────────────── the SQL compiler (safety invariants) ─────────────────────────── */

    private static readonly DateTime WindowStart = new(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);

    private static string Compile(PanelPlan plan, IReadOnlyList<string>? servers = null, IReadOnlyDictionary<string, string?>? variables = null)
    {
        var (compiled, error) = ComposeCompiler.Compile(
            plan, new ComposeRunContext(servers, WindowStart, WindowEnd, variables ?? ComposeRunContext.NoVariables));
        Assert.True(error is null, error);
        Assert.NotNull(compiled);
        return compiled!.Sql;
    }

    [Fact]
    public void Compile_QualifiesCollect_AndNeverConfig()
    {
        var sql = Compile(ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("collect.wait_stats", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("config.", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_BindsTheWindowAndServerScope_NoRawValues()
    {
        var sql = Compile(ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"), new[] { "PROD-01" });
        Assert.Contains("$1", sql, StringComparison.Ordinal);   /* window start */
        Assert.Contains("$2", sql, StringComparison.Ordinal);   /* window end */
        Assert.Contains("server_name = ANY($3)", sql, StringComparison.Ordinal);
        /* The server name is a bound parameter, never interpolated. */
        Assert.DoesNotContain("PROD-01", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_BindsFilterValues_NeverInterpolatesThem()
    {
        var sql = Compile(ValidPlan(
            "{\"source\":\"procedure_stats\",\"measure\":\"proc_elapsed_us\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\"," +
            "\"filters\":[{\"dimension\":\"object_name\",\"op\":\"like\",\"value\":\"dbo.usp_Payment%\"}]}"));
        Assert.Contains("LIKE $", sql, StringComparison.Ordinal);
        /* The LIKE pattern is a bound parameter, never in the SQL text. */
        Assert.DoesNotContain("usp_Payment", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_EqFilter_IsABoundArray()
    {
        var sql = Compile(ValidPlan(
            "{\"source\":\"file_io_stats\",\"measure\":\"file_read_bytes\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"," +
            "\"filters\":[{\"dimension\":\"database_name\",\"op\":\"eq\",\"value\":[\"Sales\",\"HR\"]}]}"));
        Assert.Contains("= ANY($", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Sales", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_Ratio_IsSumOverNullifSum()
    {
        var sql = Compile(ValidPlan("{\"source\":\"wait_stats\",\"ratio\":\"signal_wait_pct\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("NULLIF(SUM(", sql, StringComparison.Ordinal);
        Assert.Contains("delta_signal_wait_time_ms", sql, StringComparison.Ordinal);
        Assert.Contains("delta_wait_time_ms", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_Percentile_OnlyRendersForPerEvent()
    {
        var sql = Compile(ValidPlan("{\"source\":\"long_query_completions\",\"measure\":\"lqc_duration_us\",\"aggregate\":\"percentile_cont\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("percentile_cont(0.95)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_UnitConversion_ScalesMicrosecondsToMilliseconds()
    {
        /* proc_elapsed_us: native µs, default ms — the aggregate is divided by the 1000 family factor. */
        var sql = Compile(ValidPlan("{\"source\":\"procedure_stats\",\"measure\":\"proc_elapsed_us\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("/ 1000.0", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_ModuleJoin_IsEmittedAndWindowBounded_ForTheDerivedObject()
    {
        var sql = Compile(ValidPlan(
            "{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"object_name\"],\"viz\":\"bar\"}"), new[] { "PROD-01" });
        Assert.Contains("procedure_stats", sql, StringComparison.Ordinal);
        Assert.Contains("ROW_NUMBER()", sql, StringComparison.Ordinal);
        /* Per-server attribution — a handle reused across servers attributes per server, not globally. */
        Assert.Contains("PARTITION BY server_name, sql_handle", sql, StringComparison.Ordinal);
        /* The #1568 stitch is bounded by the SAME window ($1/$2) and scoped to the same server set ($3). */
        Assert.Contains("collection_time >= $1", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time <= $2", sql, StringComparison.Ordinal);
        Assert.Contains("server_name = ANY($3)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_NoModuleJoin_WhenNoDerivedObjectUsed()
    {
        var sql = Compile(ValidPlan("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"table\"}"));
        Assert.DoesNotContain("ROW_NUMBER()", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_RankedShape_OrdersByValueDescWithBoundLimit()
    {
        var sql = Compile(ValidPlan("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"table\"}"));
        Assert.Contains("ORDER BY value DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_RejectsTooFineABucketOverTooWideAWindow()
    {
        var plan = ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"minute\",\"viz\":\"line\"}");
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(60); /* 60 days of minutes >> MaxBuckets */
        var (compiled, error) = ComposeCompiler.Compile(plan, new ComposeRunContext(null, start, end, ComposeRunContext.NoVariables));
        Assert.Null(compiled);
        Assert.Contains("points", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ResolvesAVariableToABoundValue()
    {
        var plan = ValidPlan(
            "{\"source\":\"file_io_stats\",\"measure\":\"file_read_bytes\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"," +
            "\"filters\":[{\"dimension\":\"database_name\",\"op\":\"eq\",\"value\":\"$db\"}]}",
            "db");
        var sql = Compile(plan, variables: new Dictionary<string, string?>(StringComparer.Ordinal) { ["db"] = "Sales" });
        Assert.Contains("= ANY($", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Sales", sql, StringComparison.Ordinal);
    }

    /* ─────────────────────────── ValidateDefinition: v1/v2 dispatch ─────────────────────────── */

    [Fact]
    public void ValidateDefinition_AcceptsAComposedPanel()
    {
        var ok = DarlingWebEndpoints.ValidateDefinition(
            "{\"panels\":[{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}]}");
        Assert.True(ok.IsValid, ok.Error);
    }

    [Fact]
    public void ValidateDefinition_AcceptsAMixedV1AndV2Definition()
    {
        var ok = DarlingWebEndpoints.ValidateDefinition(
            "{\"variables\":[{\"name\":\"db\",\"dimension\":\"database_name\"}]," +
            "\"panels\":[{\"read\":\"get_wait_stats\",\"viz\":\"table\"}," +
            "{\"source\":\"procedure_stats\",\"measure\":\"proc_elapsed_us\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\"," +
            "\"filters\":[{\"dimension\":\"database_name\",\"op\":\"eq\",\"value\":\"$db\"}]}]}");
        Assert.True(ok.IsValid, ok.Error);
    }

    [Fact]
    public void ValidateDefinition_RejectsABadComposedPanel_NamingThePanel()
    {
        var result = DarlingWebEndpoints.ValidateDefinition(
            "{\"panels\":[{\"source\":\"wait_stats\",\"measure\":\"nope\",\"aggregate\":\"sum\",\"viz\":\"table\"}]}");
        Assert.False(result.IsValid);
        Assert.Contains("panel 0", result.Error!, StringComparison.Ordinal);
        Assert.Contains("unknown measure", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDefinition_RejectsAComposedPanelWithAnUndeclaredVariable()
    {
        var result = DarlingWebEndpoints.ValidateDefinition(
            "{\"panels\":[{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"table\"," +
            "\"filters\":[{\"dimension\":\"wait_type\",\"op\":\"eq\",\"value\":\"$nope\"}]}]}");
        Assert.False(result.IsValid);
        Assert.Contains("undeclared variable", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDefinition_RejectsBadViewVariables()
    {
        var result = DarlingWebEndpoints.ValidateDefinition(
            "{\"variables\":[{\"name\":\"x\",\"dimension\":\"nope\"}],\"panels\":[{\"read\":\"get_wait_stats\",\"viz\":\"table\"}]}");
        Assert.False(result.IsValid);
        Assert.Contains("dimension", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /* ─────────────────────────── /api/catalog v2 ─────────────────────────── */

    [Fact]
    public void Catalog_CarriesTheComposeSection()
    {
        var catalog = DarlingWebEndpoints.BuildCatalogNode();

        /* v1 keys stay intact. */
        Assert.NotNull(catalog["reads"]);
        Assert.NotNull(catalog["viz"]);

        var compose = Assert.IsType<JsonObject>(catalog["compose"]);
        var measures = Assert.IsType<JsonArray>(compose["measures"]);
        Assert.Equal(MeasureCatalog.Measures.Count, measures.Count);
        Assert.NotNull(compose["dimensions"]);
        Assert.NotNull(compose["unitFamilies"]);
        Assert.NotNull(compose["aggregates"]);
        Assert.NotNull(compose["timeBuckets"]);
        Assert.NotNull(compose["filterOps"]);
    }

    /* ─────────────────────────── DoS backstop + loopback scrub (provisioning) ─────────────────────────── */

    [Fact]
    public void Provisioning_SetsStatementTimeout_OnViewerAndMcp_NotAdmin()
    {
        var sql = DarlingManagedRoles.BuildProvisioningSql("AdminPassword01", "ViewerPassword02", "McpPassword03");
        Assert.Contains($"ALTER ROLE viewer SET statement_timeout = '{ComposeLimits.StatementTimeout}';", sql, StringComparison.Ordinal);
        Assert.Contains($"ALTER ROLE mcp    SET statement_timeout = '{ComposeLimits.StatementTimeout}';", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER ROLE admin  SET statement_timeout", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Provisioning_HasNoStaleLoopbackComment()
    {
        var sql = DarlingManagedRoles.BuildProvisioningSql("AdminPassword01", "ViewerPassword02", "McpPassword03");
        Assert.DoesNotContain("LOOPBACK-ONLY", sql, StringComparison.Ordinal);
    }

    /* ─────────────────────────── fleet / multi-server (§2b + Flow B) ─────────────────────────── */

    [Fact]
    public void Compile_Fleet_HasNoServerPredicate_WhenNoServerScope()
    {
        var sql = Compile(ValidPlan("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"bar\"}"));
        Assert.DoesNotContain("server_name = ANY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_MultiServer_BindsServerNameArray_NeverInterpolated()
    {
        var sql = Compile(
            ValidPlan("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"bar\"}"),
            new[] { "PROD-01", "PROD-02" });
        Assert.Contains("server_name = ANY($", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("PROD-01", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("PROD-02", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_GroupByServer_SelectsServerName_OnAServerGrainMeasure()
    {
        /* Flow B's grain-trap alternative: "top servers by CPU" — group a server-grain gauge by the universal
           server dimension across the fleet. */
        var sql = Compile(ValidPlan("{\"source\":\"cpu_utilization_stats\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"avg\",\"topN\":5,\"groupBy\":[\"server\"],\"viz\":\"bar\"}"));
        Assert.Contains("server_name AS server", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParsePanel_AllowsServer_AsFilterAndGroupBy_OnEveryMeasure()
    {
        /* server is universal even on a measure with NO declared dimensions (cpu is server-grain). */
        var plan = ValidPlan(
            "{\"source\":\"cpu_utilization_stats\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"avg\",\"topN\":5,\"groupBy\":[\"server\"],\"viz\":\"bar\"," +
            "\"filters\":[{\"dimension\":\"server\",\"op\":\"eq\",\"value\":[\"A\",\"B\"]}]}");
        Assert.Single(plan.GroupBy);
        Assert.Single(plan.Filters);
    }

    /* ─────────────────────────── viz ↔ mode coherence (§4) ─────────────────────────── */

    [Theory]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"pie\"}", "not a time series")]
    [InlineData("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"line\"}", "ranked")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"stacked\"}", "needs a group-by")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"line\"}", "single value")]
    [InlineData("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"viz\":\"bar\"}", "needs a group-by")]
    public void TryParsePanel_RejectsIncoherentVizForMode(string json, string expectedFragment)
    {
        Assert.Contains(expectedFragment, RejectReason(json), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"area\"}")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"groupBy\":[\"wait_type\"],\"viz\":\"stacked\"}")]
    [InlineData("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"pie\"}")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"stat\"}")]
    public void TryParsePanel_AcceptsCoherentVizForMode(string json)
    {
        var (plan, error) = ComposeSpec.TryParsePanel(PanelJson(json), Array.Empty<string>());
        Assert.True(error is null, error);
        Assert.NotNull(plan);
    }

    /* ─────────────────────────── acceptance flows compile end-to-end ─────────────────────────── */

    [Fact]
    public void FlowA_AvgProcedureElapsed_LikePattern_OnServer_ValidatesAndCompiles()
    {
        /* Acceptance Flow A: "avg procedure elapsed LIKE 'dbo.usp_Payment%' on $server, 24h → line." The panel
           validates against the catalog + declared $server, then compiles to a schema-qualified, fully-bound,
           time-bucketed, server-scoped query. */
        var panel =
            "{\"source\":\"procedure_stats\",\"measure\":\"proc_elapsed_us\",\"aggregate\":\"avg\"," +
            "\"timeBucket\":\"hour\",\"viz\":\"line\"," +
            "\"filters\":[{\"dimension\":\"object_name\",\"op\":\"like\",\"value\":\"dbo.usp_Payment%\"}]}";

        var (plan, error) = ComposeSpec.TryParsePanel(PanelJson(panel), new[] { "srv" });
        Assert.True(error is null, error);

        var sql = Compile(plan!, new[] { "PROD-01" }); /* $server resolved to one server */
        Assert.Contains("collect.procedure_stats", sql, StringComparison.Ordinal);
        Assert.Contains("date_trunc('hour'", sql, StringComparison.Ordinal);
        Assert.Contains("LIKE $", sql, StringComparison.Ordinal);
        Assert.Contains("server_name = ANY($", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("usp_Payment", sql, StringComparison.Ordinal); /* the pattern is bound, not interpolated */
        Assert.DoesNotContain("config.", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void FlowB_TopDatabasesByCpu_AcrossTheFleet_ValidatesAndCompiles()
    {
        /* Acceptance Flow B: "top-10 databases by CPU across the fleet → bar." No server scope = whole fleet
           (no server predicate), grouped by database, ranked, bounded by a LIMIT. */
        var panel =
            "{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\"," +
            "\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"bar\"}";

        var plan = ValidPlan(panel);
        var sql = Compile(plan); /* no server scope => whole fleet */
        Assert.Contains("collect.query_stats", sql, StringComparison.Ordinal);
        Assert.Contains("f.database_name", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY value DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("server_name = ANY", sql, StringComparison.Ordinal); /* fleet-wide, no server filter */
        Assert.DoesNotContain("config.", sql, StringComparison.Ordinal);
    }

    /* ─────────────────────────── B3 full catalog fill ─────────────────────────── */

    [Fact]
    public void B3_Catalog_CoversTheExpectedSources()
    {
        /* The B3 fill: every remaining collector table with composable measures is in the catalog (config
           snapshots + query_store are deliberately excluded — see the source comments). */
        var sources = MeasureCatalog.Measures.Select(m => m.SourceTable).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
        {
            "latch_stats", "spinlock_stats", "cpu_scheduler_stats", "plan_cache_stats", "memory_stats",
            "memory_clerks", "memory_grant_stats", "tempdb_stats", "database_size_stats", "index_object_stats",
            "session_stats", "session_summary_stats", "waiting_tasks", "query_snapshots",
            "blocked_process_reports", "dmv_blocking_snapshots", "deadlocks", "system_health_events",
            "default_trace_events", "running_jobs", "job_history", "perfmon_stats", "query_store_stats",
        })
        {
            Assert.True(sources.Contains(expected), $"catalog is missing a measure for '{expected}'.");
        }
    }

    [Fact]
    public void B3_CountOnlyEventMeasure_CompilesToCountStar()
    {
        var sql = Compile(ValidPlan("{\"source\":\"deadlocks\",\"measure\":\"deadlock_count\",\"aggregate\":\"count\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("collect.deadlocks", sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(*)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("config.", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void B3_ServerGrainGauge_CompilesAndGroupsByServer()
    {
        /* A server-grain gauge (memory) grouped by the universal server dimension = "top servers by memory". */
        var sql = Compile(ValidPlan("{\"source\":\"memory_stats\",\"measure\":\"mem_total_server_mb\",\"aggregate\":\"avg\",\"topN\":5,\"groupBy\":[\"server\"],\"viz\":\"bar\"}"));
        Assert.Contains("collect.memory_stats", sql, StringComparison.Ordinal);
        Assert.Contains("AVG(", sql, StringComparison.Ordinal);
        Assert.Contains("server_name AS server", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void B3_PerEventMeasure_SupportsCountAndPercentile()
    {
        /* blocked_process_reports: COUNT of reports, and a true p95 block duration (per-event rows exist). */
        var count = Compile(ValidPlan("{\"source\":\"blocked_process_reports\",\"measure\":\"bpr_wait_time_ms\",\"aggregate\":\"count\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("COUNT(*)", count, StringComparison.Ordinal);

        var p95 = Compile(ValidPlan("{\"source\":\"blocked_process_reports\",\"measure\":\"bpr_wait_time_ms\",\"aggregate\":\"percentile_cont\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("percentile_cont(0.95)", p95, StringComparison.Ordinal);
    }

    [Fact]
    public void B3_AvgPerExecutionRatio_CompilesToWeightedSumOverSum()
    {
        /* "avg procedure duration per execution" — the execution-WEIGHTED ratio SUM(elapsed)/SUM(execs), NOT an
           avg-of-avgs. Proves the bread-and-butter same-source ratio (design §2c). */
        var sql = Compile(ValidPlan("{\"source\":\"procedure_stats\",\"ratio\":\"proc_avg_elapsed_us\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("collect.procedure_stats", sql, StringComparison.Ordinal);
        Assert.Contains("NULLIF(SUM(", sql, StringComparison.Ordinal);
        Assert.Contains("delta_elapsed_time", sql, StringComparison.Ordinal);
        Assert.Contains("delta_execution_count", sql, StringComparison.Ordinal);
    }
}

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

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// The abuse bounds a composed panel must stay within — the compile-time half of the DoS controls
/// (the DB-side half is the <c>statement_timeout</c> on the viewer/mcp roles). Because the compiler only
/// ever emits aggregated queries (GROUP BY bucket/dims, or a single scalar) — never raw rows — output is
/// bounded by construction; these caps bound the WORK: the window, the bucket resolution, and the number
/// of filter/group-by terms.
/// </summary>
public static class ComposeLimits
{
    /// <summary>The widest window a panel may query (90 days) — a hard ceiling on the scan.</summary>
    public const int MaxWindowHours = 24 * 90;

    /// <summary>The most buckets a time series may resolve to (window ÷ bucket) — bounds a 1-minute
    /// bucket over a long window from fanning out to hundreds of thousands of buckets.</summary>
    public const int MaxBuckets = 5_000;

    /// <summary>The ceiling on a ranked panel's <c>topN</c> (mirrors the read surface's row clamp).</summary>
    public const int MaxTopN = 1_000;

    /// <summary>The final safety <c>LIMIT</c> on a time-series query (a grouped series × buckets backstop).</summary>
    public const int HardRowCap = 10_000;

    public const int MaxFilters = 12;
    public const int MaxGroupBy = 4;

    /// <summary>
    /// The per-session <c>statement_timeout</c> applied to the <c>viewer</c>/<c>mcp</c> roles (the compose
    /// surface's DB identities) — the hard backstop a composed query can never exceed, whatever the compile-time
    /// window/bucket caps reason around (a <c>LIMIT</c> bounds OUTPUT, a group-by scans+sorts before it). Applied
    /// in the role PROVISIONING DDL (<see cref="DarlingManagedRoles"/> + <c>tools/provision-roles.sql</c>), NOT a
    /// versioned migration: a role's statement_timeout has no probeable schema footprint, so tying it to
    /// <see cref="!:StorageVersion.SchemaVersion"/> would break the viewer's connect-time version gate. A Postgres
    /// interval literal.
    /// </summary>
    public const string StatementTimeout = "15s";

    /// <summary>The fixed percentile for <c>percentile_cont</c> (p95) — a hardcoded default per the
    /// defaults-over-speculative-config rule; a per-panel percentile knob is a clean later add.</summary>
    public const double DefaultPercentile = 0.95;
}

/// <summary>A view-level variable: a named, dimension-typed slot a panel filter can reference as
/// <c>"$name"</c>. A variable whose dimension is <see cref="ServerDimension"/> scopes the run's server
/// (the compile+run endpoint resolves it to the serverId); the rest resolve to a bound filter value.</summary>
public sealed record ComposeVariable(string Name, string Dimension, string? Default)
{
    /// <summary>The reserved dimension marking the ambient server scope (resolved to serverId, not a filter).</summary>
    public const string ServerDimension = "server";
}

/// <summary>A panel filter's value — exactly one of a literal set (one value, or many for a multi-select
/// <c>eq</c>/<c>neq</c>) or a reference to a declared <see cref="ComposeVariable"/>. Either way it is
/// resolved to bound parameters at compile time; a value is NEVER interpolated into SQL.</summary>
public sealed record ComposeFilterValue(IReadOnlyList<string>? Literals, string? VariableRef)
{
    public bool IsVariable => VariableRef is not null;
}

/// <summary>One parsed, validated filter: a real dimension for the source, a legal operator, and a value
/// that resolves to bound parameters.</summary>
public sealed record ComposeFilter(ComposeDimension Dimension, ComposeFilterOp Op, ComposeFilterValue Value);

/// <summary>Which shape the compiler emits — chosen by the presence of a time bucket vs. a topN.</summary>
public enum PanelMode
{
    /// <summary>Bucketed over time: <c>date_trunc</c> + GROUP BY bucket [+ dims], ordered by bucket.</summary>
    TimeSeries,

    /// <summary>Ranked: GROUP BY dims, ordered by the aggregate descending, LIMIT topN.</summary>
    Ranked,

    /// <summary>A single aggregate over the whole window (one row) — the stat tile.</summary>
    Scalar,
}

/// <summary>
/// A fully-parsed, fully-validated panel — everything the compiler needs, with every identifier already
/// resolved to a catalog object (never a raw string). Produced by <see cref="ComposeSpec.TryParsePanel"/>
/// (the write-time authority and the compiler's input alike), so a <see cref="PanelPlan"/> in hand is by
/// construction safe to compile.
/// </summary>
public sealed record PanelPlan
{
    public required ComposeMeasure Measure { get; init; }
    public ComposeAggregate Aggregate { get; init; }
    public required string Unit { get; init; }
    public PanelMode Mode { get; init; }
    public ComposeTimeBucket TimeBucket { get; init; }
    public int TopN { get; init; }
    public required IReadOnlyList<ComposeFilter> Filters { get; init; }
    public required IReadOnlyList<ComposeDimension> GroupBy { get; init; }
    public required string Viz { get; init; }

    /// <summary>True when any filter/groupBy dimension is stitched from the #1568 module join, so the
    /// compiler must emit (and window-bound) the module CTE.</summary>
    public bool UsesModuleJoin =>
        Filters.Any(f => f.Dimension.ViaModuleJoin) || GroupBy.Any(d => d.ViaModuleJoin);
}

/// <summary>
/// Parses + validates the Custom Views v2 spec JSON into typed, catalog-resolved plans — the SINGLE
/// authority both the write path (<c>DarlingWebEndpoints.ValidateDefinition</c>) and the run path
/// (<c>/api/compose/run</c>) route through, so a definition that stored can always compile and vice
/// versa. Cross-checks EVERY identifier-bearing field against <see cref="MeasureCatalog"/> and rejects
/// anything off-catalog; the compiler then trusts the plan completely.
/// </summary>
public static class ComposeSpec
{
    /// <summary>Whether a panel object is a v2 (composed) panel — it names a <c>source</c>. A v1 panel
    /// names a <c>read</c> instead; the two coexist in one definition, dispatched per panel.</summary>
    public static bool IsComposedPanel(JsonObject panel) => panel["source"] is not null;

    /// <summary>The v2 composed-panel viz vocabulary (design §4) — distinct from v1 read panels' KnownViz
    /// (table/line/stat/bandlist). The composer's viz picker serves this; every stored composed panel's viz
    /// must be in it AND coherent with the panel's mode (<see cref="ValidateVizMode"/>).</summary>
    public static readonly IReadOnlyList<string> ComposeVizList = new[] { "line", "area", "bar", "stacked", "pie", "table", "stat" };

    /// <summary>Set form of <see cref="ComposeVizList"/> for O(1) membership.</summary>
    public static readonly IReadOnlySet<string> KnownComposeViz = new HashSet<string>(ComposeVizList, StringComparer.Ordinal);

    /* ─────────────────────────── variables ─────────────────────────── */

    /// <summary>The set of dimension names a variable may be typed as: every catalog dimension name plus
    /// the reserved <c>server</c> scope.</summary>
    private static readonly HashSet<string> s_variableDimensionNames = BuildVariableDimensionNames();

    private static HashSet<string> BuildVariableDimensionNames()
    {
        var names = MeasureCatalog.Dimensions.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        names.Add(ComposeVariable.ServerDimension);
        return names;
    }

    /// <summary>Parses + validates a definition's <c>variables</c> array (absent = none). Each entry must be
    /// an object with a non-empty <c>name</c> (unique) and a <c>dimension</c> that is a known dimension or
    /// <c>server</c>; <c>default</c> is optional text.</summary>
    public static (IReadOnlyList<ComposeVariable>? Variables, string? Error) ParseVariables(JsonNode? node)
    {
        if (node is null)
        {
            return (Array.Empty<ComposeVariable>(), null);
        }

        if (node is not JsonArray array)
        {
            return (null, "definition.variables must be an array.");
        }

        var variables = new List<ComposeVariable>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject obj)
            {
                return (null, $"variable {i} must be an object.");
            }

            var name = Str(obj, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return (null, $"variable {i} is missing 'name'.");
            }

            if (!seen.Add(name))
            {
                return (null, $"variable '{name}' is declared more than once.");
            }

            var dimension = Str(obj, "dimension");
            if (string.IsNullOrEmpty(dimension) || !s_variableDimensionNames.Contains(dimension))
            {
                return (null, $"variable '{name}' has an unknown dimension '{dimension}'.");
            }

            variables.Add(new ComposeVariable(name, dimension, Str(obj, "default")));
        }

        return (variables, null);
    }

    /* ─────────────────────────── range ─────────────────────────── */

    /// <summary>Validates a definition/run <c>range</c> object (absent = the caller's default). Only
    /// <c>{hours}</c> is understood today; hours must be a positive int within the window ceiling.</summary>
    public static (int? Hours, string? Error) ParseRange(JsonNode? node)
    {
        if (node is null)
        {
            return (null, null);
        }

        if (node is not JsonObject obj)
        {
            return (null, "range must be an object.");
        }

        if (obj["hours"] is not JsonValue hoursValue || !hoursValue.TryGetValue<int>(out var hours))
        {
            return (null, "range.hours must be an integer.");
        }

        if (hours < 1 || hours > ComposeLimits.MaxWindowHours)
        {
            return (null, $"range.hours must be between 1 and {ComposeLimits.MaxWindowHours}.");
        }

        return (hours, null);
    }

    /* ─────────────────────────── panels ─────────────────────────── */

    /// <summary>
    /// Parses + validates one v2 (<c>source</c>) panel against the catalog and the view's declared
    /// <paramref name="declaredVariables"/>, returning a compiler-ready <see cref="PanelPlan"/> or a
    /// caller-facing error. The full identifier cross-check: source ∈ catalog; measure|ratio ∈ catalog and
    /// on that source; aggregate ∈ the measure's ValidAggs ∧ legal-for-archetype; unit ∈ the measure's
    /// family; each filter/groupBy dimension ∈ the measure's AllowedDimensions; LIKE only on a LIKE-able
    /// dimension; each variable-ref ∈ the declared variables; and the per-panel caps.
    /// </summary>
    public static (PanelPlan? Plan, string? Error) TryParsePanel(JsonObject panel, IReadOnlyCollection<string> declaredVariables)
    {
        var source = Str(panel, "source");
        if (string.IsNullOrEmpty(source) || !MeasureCatalog.IsKnownSource(source))
        {
            return (null, $"panel references unknown source '{source}'.");
        }

        /* measure XOR ratio. */
        var measureKey = Str(panel, "measure");
        var ratioKey = Str(panel, "ratio");
        if (measureKey is not null && ratioKey is not null)
        {
            return (null, "panel must set exactly one of 'measure' or 'ratio', not both.");
        }

        var key = measureKey ?? ratioKey;
        var measure = MeasureCatalog.Measure(key);
        if (measure is null)
        {
            return (null, $"panel references unknown measure '{key}'.");
        }

        if (!string.Equals(measure.SourceTable, source, StringComparison.Ordinal))
        {
            return (null, $"measure '{measure.Key}' is not on source '{source}' (it is on '{measure.SourceTable}').");
        }

        var isRatio = measure.Kind == MeasureKind.Ratio;
        if (isRatio && measureKey is not null)
        {
            return (null, $"'{measure.Key}' is a ratio; reference it as 'ratio', not 'measure'.");
        }

        if (!isRatio && ratioKey is not null)
        {
            return (null, $"'{measure.Key}' is not a ratio; reference it as 'measure', not 'ratio'.");
        }

        /* aggregate (scalar only; a ratio's aggregation is fixed). */
        var aggregate = measure.DefaultTimeAgg;
        if (!isRatio)
        {
            var aggWire = Str(panel, "aggregate");
            if (aggWire is null)
            {
                return (null, $"panel is missing 'aggregate' (one of {string.Join(", ", MeasureCatalog.AggregateWireNames)}).");
            }

            if (!MeasureCatalog.TryParseAggregate(aggWire, out aggregate))
            {
                return (null, $"panel has unknown aggregate '{aggWire}'.");
            }

            if (!measure.ValidAggs.Contains(aggregate))
            {
                return (null, $"aggregate '{aggWire}' is not valid for measure '{measure.Key}'.");
            }

            /* Defense in depth over ValidAggs: percentile_cont is legal ONLY on a per-event measure. */
            if (aggregate == ComposeAggregate.PercentileCont && measure.Archetype != MeasureArchetype.PerEvent)
            {
                return (null, "percentile_cont is only valid on per-event measures.");
            }
        }

        /* unit ∈ the measure's family — except COUNT, which is a plain row count (unitless) regardless
           of the measure's family, so its only legal unit is 'count'. */
        string unit;
        if (!isRatio && aggregate == ComposeAggregate.Count)
        {
            unit = Str(panel, "unit") ?? MeasureCatalog.FamilyCount;
            if (!string.Equals(unit, MeasureCatalog.FamilyCount, StringComparison.Ordinal))
            {
                return (null, "a count aggregate is unitless; its unit must be 'count'.");
            }
        }
        else
        {
            unit = Str(panel, "unit") ?? measure.DefaultUnit;
            var family = MeasureCatalog.Family(measure.UnitFamily);
            if (family is null || !family.Has(unit))
            {
                return (null, $"unit '{unit}' is not valid for measure '{measure.Key}' (family '{measure.UnitFamily}').");
            }
        }

        /* mode: a real timeBucket => time series; else topN => ranked; else scalar. */
        var bucket = ComposeTimeBucket.None;
        var bucketWire = Str(panel, "timeBucket");
        if (bucketWire is not null && !MeasureCatalog.TryParseTimeBucket(bucketWire, out bucket))
        {
            return (null, $"panel has unknown timeBucket '{bucketWire}'.");
        }

        var hasBucket = bucket != ComposeTimeBucket.None;
        var hasTopN = panel["topN"] is JsonValue;
        var topN = 0;
        if (hasTopN)
        {
            if (panel["topN"] is not JsonValue topNValue || !topNValue.TryGetValue<int>(out topN) || topN < 1)
            {
                return (null, "panel.topN must be a positive integer.");
            }

            topN = Math.Min(topN, ComposeLimits.MaxTopN);
        }

        if (hasBucket && hasTopN)
        {
            return (null, "panel cannot set both 'timeBucket' and 'topN' (a ranked panel is not a time series).");
        }

        var mode = hasBucket ? PanelMode.TimeSeries : hasTopN ? PanelMode.Ranked : PanelMode.Scalar;

        /* viz — the v2 composed-panel vocabulary (distinct from v1 read panels' KnownViz); coherence with the
           panel's mode is checked below once groupBy is known (design §4). */
        var viz = ParseViz(panel["viz"]);
        if (viz is null || !KnownComposeViz.Contains(viz))
        {
            return (null, $"panel has an unknown or missing viz type (one of {string.Join(", ", ComposeVizList)}).");
        }

        /* filters. */
        var (filters, filterError) = ParseFilters(panel["filters"], source, measure, declaredVariables);
        if (filterError is not null)
        {
            return (null, filterError);
        }

        /* groupBy. */
        var (groupBy, groupError) = ParseGroupBy(panel["groupBy"], source, measure);
        if (groupError is not null)
        {
            return (null, groupError);
        }

        /* viz ↔ mode coherence, so a stored def can never be un-renderable (design §4): line/area/stacked are
           time series, bar/pie are ranked, stat is a single scalar (table works in any mode). */
        var vizModeError = ValidateVizMode(viz, mode, groupBy!.Count);
        if (vizModeError is not null)
        {
            return (null, vizModeError);
        }

        var plan = new PanelPlan
        {
            Measure = measure,
            Aggregate = aggregate,
            Unit = unit,
            Mode = mode,
            TimeBucket = bucket,
            TopN = topN,
            Filters = filters!,
            GroupBy = groupBy!,
            Viz = viz,
        };

        return (plan, null);
    }

    private static (IReadOnlyList<ComposeFilter>? Filters, string? Error) ParseFilters(
        JsonNode? node, string source, ComposeMeasure measure, IReadOnlyCollection<string> declaredVariables)
    {
        if (node is null)
        {
            return (Array.Empty<ComposeFilter>(), null);
        }

        if (node is not JsonArray array)
        {
            return (null, "panel.filters must be an array.");
        }

        if (array.Count > ComposeLimits.MaxFilters)
        {
            return (null, $"panel has {array.Count} filters; the maximum is {ComposeLimits.MaxFilters}.");
        }

        var filters = new List<ComposeFilter>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject obj)
            {
                return (null, $"filter {i} must be an object.");
            }

            var dimName = Str(obj, "dimension");
            var dimension = dimName is null ? null : MeasureCatalog.Dimension(source, dimName);
            if (dimension is null || !IsDimensionAllowed(dimName!, measure))
            {
                return (null, $"filter {i} references dimension '{dimName}', which is not allowed for measure '{measure.Key}'.");
            }

            var opWire = Str(obj, "op");
            if (opWire is null || !MeasureCatalog.TryParseFilterOp(opWire, out var op))
            {
                return (null, $"filter {i} has unknown op '{opWire}'.");
            }

            if (op == ComposeFilterOp.Like && !dimension.Likeable)
            {
                return (null, $"filter {i}: op 'like' is not allowed on dimension '{dimName}'.");
            }

            var (value, valueError) = ParseFilterValue(obj["value"], op, i, declaredVariables);
            if (valueError is not null)
            {
                return (null, valueError);
            }

            filters.Add(new ComposeFilter(dimension, op, value!));
        }

        return (filters, null);
    }

    private static (ComposeFilterValue? Value, string? Error) ParseFilterValue(
        JsonNode? node, ComposeFilterOp op, int index, IReadOnlyCollection<string> declaredVariables)
    {
        if (node is null)
        {
            return (null, $"filter {index} is missing 'value'.");
        }

        var allowsMany = op is ComposeFilterOp.Eq or ComposeFilterOp.Neq;

        if (node is JsonValue value)
        {
            if (!value.TryGetValue<string>(out var text) || text is null)
            {
                /* A numeric/boolean literal is coerced to its text form (the columns are text). */
                text = value.ToString();
            }

            if (text.StartsWith("$", StringComparison.Ordinal))
            {
                var varName = text.Substring(1);
                if (varName.Length == 0 || !declaredVariables.Contains(varName))
                {
                    return (null, $"filter {index} references undeclared variable '{text}'.");
                }

                return (new ComposeFilterValue(null, varName), null);
            }

            return (new ComposeFilterValue(new[] { text }, null), null);
        }

        if (node is JsonArray array)
        {
            if (!allowsMany)
            {
                return (null, $"filter {index}: op '{MeasureCatalog.WireName(op)}' takes a single value, not a list.");
            }

            if (array.Count == 0)
            {
                return (null, $"filter {index} has an empty value list.");
            }

            var literals = new List<string>(array.Count);
            foreach (var item in array)
            {
                if (item is not JsonValue itemValue)
                {
                    return (null, $"filter {index} value list must contain only strings.");
                }

                literals.Add(itemValue.TryGetValue<string>(out var s) && s is not null ? s : itemValue.ToString());
            }

            return (new ComposeFilterValue(literals, null), null);
        }

        return (null, $"filter {index} has an invalid 'value'.");
    }

    private static (IReadOnlyList<ComposeDimension>? GroupBy, string? Error) ParseGroupBy(
        JsonNode? node, string source, ComposeMeasure measure)
    {
        if (node is null)
        {
            return (Array.Empty<ComposeDimension>(), null);
        }

        if (node is not JsonArray array)
        {
            return (null, "panel.groupBy must be an array.");
        }

        if (array.Count > ComposeLimits.MaxGroupBy)
        {
            return (null, $"panel has {array.Count} groupBy dimensions; the maximum is {ComposeLimits.MaxGroupBy}.");
        }

        var dims = new List<ComposeDimension>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < array.Count; i++)
        {
            var name = (array[i] as JsonValue)?.GetValue<string>();
            var dimension = name is null ? null : MeasureCatalog.Dimension(source, name);
            if (dimension is null || !IsDimensionAllowed(name!, measure))
            {
                return (null, $"groupBy references dimension '{name}', which is not allowed for measure '{measure.Key}'.");
            }

            if (!seen.Add(name!))
            {
                return (null, $"groupBy lists dimension '{name}' more than once.");
            }

            dims.Add(dimension);
        }

        return (dims, null);
    }

    private static string? ParseViz(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonObject obj => Str(obj, "type"),
        _ => null,
    };

    /// <summary>The universal <c>server</c> axis (fleet / multi-server) is usable by EVERY measure; any other
    /// dimension must be in the measure's <see cref="ComposeMeasure.AllowedDimensions"/>.</summary>
    private static bool IsDimensionAllowed(string dimensionName, ComposeMeasure measure) =>
        string.Equals(dimensionName, MeasureCatalog.ServerDimensionName, StringComparison.Ordinal)
        || measure.AllowedDimensions.Contains(dimensionName);

    /// <summary>Rejects a viz that cannot render the panel's shape (design §4 shape-steering, enforced so a
    /// stored def is never un-renderable): line/area/stacked are time series; bar/pie are ranked (topN) with a
    /// categorical group; stat is a single scalar; table renders any shape. A stacked chart also needs a
    /// group-by (the parts that stack), and a single-value panel cannot group.</summary>
    private static string? ValidateVizMode(string viz, PanelMode mode, int groupByCount)
    {
        if (string.Equals(viz, "table", StringComparison.Ordinal))
        {
            return null;
        }

        switch (mode)
        {
            case PanelMode.TimeSeries:
                if (viz is not ("line" or "area" or "stacked"))
                {
                    return $"a '{viz}' chart is not a time series; use line/area/stacked (or drop the timeBucket).";
                }

                if (string.Equals(viz, "stacked", StringComparison.Ordinal) && groupByCount == 0)
                {
                    return "a stacked chart needs a group-by dimension (the parts that stack).";
                }

                return null;

            case PanelMode.Ranked:
                if (viz is not ("bar" or "pie"))
                {
                    return $"a '{viz}' chart cannot render a ranked (topN) panel; use bar or pie.";
                }

                if (groupByCount == 0)
                {
                    return $"a '{viz}' chart needs a group-by dimension (the categories to rank).";
                }

                return null;

            case PanelMode.Scalar:
                if (groupByCount > 0)
                {
                    return "a single-value panel cannot group by a dimension; add a timeBucket (time series) or a topN (ranked).";
                }

                return string.Equals(viz, "stat", StringComparison.Ordinal)
                    ? null
                    : $"a '{viz}' chart needs a group or time bucket; a single value uses stat (or table).";

            default:
                return null;
        }
    }

    private static string? Str(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}

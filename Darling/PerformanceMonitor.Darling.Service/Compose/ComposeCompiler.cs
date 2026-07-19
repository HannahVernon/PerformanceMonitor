/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Storage;

namespace PerformanceMonitor.Darling.Service;

/// <summary>The server + window + variable bindings a <see cref="PanelPlan"/> compiles against.
/// The window is naive UTC (Kind=Unspecified is stamped when bound, matching every other Darling read).</summary>
public sealed record ComposeRunContext(
    int ServerId,
    DateTime StartUtc,
    DateTime EndUtc,
    IReadOnlyDictionary<string, string?> Variables)
{
    public static readonly IReadOnlyDictionary<string, string?> NoVariables =
        new Dictionary<string, string?>(StringComparer.Ordinal);
}

/// <summary>The compiled SQL + its bound parameters (in <c>$1..$n</c> order).</summary>
public sealed record ComposeCompiled(string Sql, IReadOnlyList<NpgsqlParameter> Parameters);

/// <summary>
/// Compiles a validated <see cref="PanelPlan"/> into a parameterized Postgres query. IRON RULES, all
/// upheld by construction because every identifier comes from <see cref="MeasureCatalog"/> (never the
/// caller):
/// <list type="bullet">
/// <item>Table/column/aggregate/time-column identifiers are catalog constants, emitted schema-qualified
/// <c>collect.&lt;table&gt;</c> — the composed query can NEVER name a <c>config</c> table or an
/// off-catalog column, and never relies on search_path.</item>
/// <item>Every VALUE is a bound parameter: <c>$1</c> serverId, <c>$2</c>/<c>$3</c> the naive-UTC window,
/// then filter values as <c>= ANY($n)</c> (with the array bound), <c>LIKE $n</c>, threshold <c>$n</c>,
/// and <c>LIMIT $n</c> for topN.</item>
/// <item>Aggregation is archetype-gated (SUM on the delta of a cumulative, on the column of a delta; AVG/
/// MIN/MAX on the gauge/per-event column; <c>percentile_cont</c> only on per-event); a ratio is
/// <c>SUM(a)::float / NULLIF(SUM(b), 0)</c>.</item>
/// <item>The #1568 <c>object_name</c> module join is bounded by the same window (the DoS fix over the
/// viewer's currently-unbounded stitch).</item>
/// </list>
/// The compiler assumes its input is a <see cref="PanelPlan"/> that <see cref="ComposeSpec.TryParsePanel"/>
/// already validated; the only failure it can still surface is the window×resolution ceiling (which needs
/// the run window, so it cannot be checked at write time).
/// </summary>
public static class ComposeCompiler
{
    /// <summary>The prefix time column per source (the collector's <c>PrefixTimeColumnName</c> — usually
    /// <c>collection_time</c>), resolved from the catalog so the compiler buckets/windows on the SAME
    /// column the pin test asserts each measure's time column is.</summary>
    private static readonly Dictionary<string, string> s_timeColumnByTable =
        CollectorCatalog.All.ToDictionary(c => c.TargetTable, c => c.PrefixTimeColumnName, StringComparer.Ordinal);

    /// <summary>The fact-table alias every composed query uses, so fact columns qualify unambiguously
    /// against the <c>m</c> module-join alias.</summary>
    private const string FactAlias = "f";

    private const string ModuleAlias = "m";

    /// <summary>
    /// Compiles <paramref name="plan"/> against <paramref name="context"/>. Returns the parameterized SQL,
    /// or a caller-facing error for the one runtime-only check (the window×resolution bucket ceiling).
    /// </summary>
    public static (ComposeCompiled? Compiled, string? Error) Compile(PanelPlan plan, ComposeRunContext context)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (plan.Mode == PanelMode.TimeSeries)
        {
            var windowSeconds = (context.EndUtc - context.StartUtc).TotalSeconds;
            var bucketSeconds = MeasureCatalog.BucketSeconds(plan.TimeBucket);
            if (bucketSeconds > 0)
            {
                var buckets = Math.Ceiling(windowSeconds / bucketSeconds);
                if (buckets > ComposeLimits.MaxBuckets)
                {
                    return (null,
                        $"the window and '{MeasureCatalog.WireName(plan.TimeBucket)}' bucket would produce {buckets:0} points " +
                        $"(max {ComposeLimits.MaxBuckets}); choose a coarser bucket or a shorter window.");
                }
            }
        }

        var p = new ParamList();
        /* $1 serverId, $2 start, $3 end — the fixed window prelude every Darling read shares. */
        var serverParam = p.AddInt(context.ServerId);
        var startParam = p.AddTimestamp(context.StartUtc);
        var endParam = p.AddTimestamp(context.EndUtc);

        var timeColumn = s_timeColumnByTable[plan.Measure.SourceTable];
        var sql = new StringBuilder();

        /* The #1568 module CTE (window-bounded) — only when a dimension is stitched from it. */
        if (plan.UsesModuleJoin)
        {
            sql.Append("WITH ").Append(ModuleAlias).Append(" AS (\n");
            sql.Append("    SELECT sql_handle, object_name, schema_name, database_name\n");
            sql.Append("    FROM (\n");
            sql.Append("        SELECT sql_handle, object_name, schema_name, database_name,\n");
            sql.Append("               ROW_NUMBER() OVER (PARTITION BY sql_handle ORDER BY collection_time DESC) AS rn\n");
            sql.Append("        FROM ").Append(PgSchemaGenerator.CollectSchema).Append(".procedure_stats\n");
            sql.Append("        WHERE server_id = ").Append(serverParam).Append('\n');
            sql.Append("          AND collection_time >= ").Append(startParam).Append('\n');
            sql.Append("          AND collection_time <= ").Append(endParam).Append('\n');
            sql.Append("          AND sql_handle IS NOT NULL\n");
            sql.Append("          AND sql_handle <> ''\n");
            sql.Append("    ) ranked_modules\n");
            sql.Append("    WHERE rn = 1\n");
            sql.Append(")\n");
        }

        /* SELECT list + the matching GROUP BY expressions. */
        var selectExprs = new List<string>();
        var groupExprs = new List<string>();

        if (plan.Mode == PanelMode.TimeSeries)
        {
            var bucketExpr = $"date_trunc('{MeasureCatalog.DateTruncField(plan.TimeBucket)}', {FactAlias}.{timeColumn})";
            selectExprs.Add(bucketExpr + " AS bucket");
            groupExprs.Add(bucketExpr);
        }

        foreach (var dim in plan.GroupBy)
        {
            var expr = ColumnRef(dim);
            selectExprs.Add(expr + " AS " + dim.Name);
            groupExprs.Add(expr);
        }

        selectExprs.Add(BuildValueExpr(plan) + " AS value");

        sql.Append("SELECT ").Append(string.Join(", ", selectExprs)).Append('\n');
        sql.Append("FROM ").Append(PgSchemaGenerator.CollectSchema).Append('.').Append(plan.Measure.SourceTable)
            .Append(" AS ").Append(FactAlias).Append('\n');

        if (plan.UsesModuleJoin)
        {
            sql.Append("LEFT JOIN ").Append(ModuleAlias).Append(" ON ").Append(ModuleAlias)
                .Append(".sql_handle = ").Append(FactAlias).Append(".sql_handle\n");
        }

        sql.Append("WHERE ").Append(FactAlias).Append(".server_id = ").Append(serverParam).Append('\n');
        sql.Append("  AND ").Append(FactAlias).Append('.').Append(timeColumn).Append(" >= ").Append(startParam).Append('\n');
        sql.Append("  AND ").Append(FactAlias).Append('.').Append(timeColumn).Append(" <= ").Append(endParam).Append('\n');

        foreach (var filter in plan.Filters)
        {
            sql.Append("  AND ").Append(BuildFilterClause(filter, context, p)).Append('\n');
        }

        if (groupExprs.Count > 0)
        {
            sql.Append("GROUP BY ").Append(string.Join(", ", groupExprs)).Append('\n');
        }

        switch (plan.Mode)
        {
            case PanelMode.TimeSeries:
                sql.Append("ORDER BY bucket\n");
                sql.Append("LIMIT ").Append(ComposeLimits.HardRowCap);
                break;
            case PanelMode.Ranked:
                sql.Append("ORDER BY value DESC\n");
                sql.Append("LIMIT ").Append(p.AddInt(plan.TopN));
                break;
            default: /* Scalar — a single aggregate row. */
                sql.Append("LIMIT 1");
                break;
        }

        return (new ComposeCompiled(sql.ToString(), p.Parameters), null);
    }

    /// <summary>The qualified reference for a dimension column: <c>m.</c> for a module-join dimension,
    /// <c>f.</c> for a fact column.</summary>
    private static string ColumnRef(ComposeDimension dimension) =>
        (dimension.ViaModuleJoin ? ModuleAlias : FactAlias) + "." + dimension.Column;

    /// <summary>Builds the <c>value</c> expression: the archetype/kind-gated aggregate, cast to double, then
    /// scaled by the requested unit's conversion factor (a compile-time family constant).</summary>
    private static string BuildValueExpr(PanelPlan plan)
    {
        var measure = plan.Measure;

        if (measure.Kind == MeasureKind.Ratio)
        {
            var numerator = MeasureCatalog.Measure(measure.NumeratorKey)!;
            var denominator = MeasureCatalog.Measure(measure.DenominatorKey)!;
            var native =
                $"(CAST(SUM({FactAlias}.{numerator.AggregationColumn}) AS double precision) " +
                $"/ NULLIF(SUM({FactAlias}.{denominator.AggregationColumn}), 0))";
            return ApplyUnitConversion(native, measure.UnitFamily, measure.NativeUnit, plan.Unit);
        }

        /* COUNT is a plain, unitless row count. */
        if (plan.Aggregate == ComposeAggregate.Count)
        {
            return "CAST(COUNT(*) AS double precision)";
        }

        /* The aggregated column: the delta for a cumulative counter, the column itself otherwise. */
        var aggColumn = measure.Archetype == MeasureArchetype.Cumulative ? measure.DeltaColumn! : measure.Column!;
        var qualified = FactAlias + "." + aggColumn;

        var nativeExpr = plan.Aggregate switch
        {
            ComposeAggregate.Sum => $"CAST(SUM({qualified}) AS double precision)",
            ComposeAggregate.Avg => $"CAST(AVG({qualified}) AS double precision)",
            ComposeAggregate.Min => $"CAST(MIN({qualified}) AS double precision)",
            ComposeAggregate.Max => $"CAST(MAX({qualified}) AS double precision)",
            ComposeAggregate.PercentileCont =>
                $"percentile_cont({FormatDouble(ComposeLimits.DefaultPercentile)}) WITHIN GROUP (ORDER BY {qualified})",
            _ => throw new InvalidOperationException($"Unhandled aggregate {plan.Aggregate}"),
        };

        return ApplyUnitConversion(nativeExpr, measure.UnitFamily, measure.NativeUnit, plan.Unit);
    }

    /// <summary>Scales <paramref name="expr"/> (already a double) from <paramref name="nativeUnit"/> to
    /// <paramref name="requestedUnit"/> within <paramref name="familyName"/>: <c>value * factorNative /
    /// factorRequested</c>. A no-op when the units match. Factors are compile-time family constants.</summary>
    private static string ApplyUnitConversion(string expr, string familyName, string nativeUnit, string requestedUnit)
    {
        var family = MeasureCatalog.Family(familyName);
        var from = family?.Unit(nativeUnit);
        var to = family?.Unit(requestedUnit);
        if (from is null || to is null || from.BaseFactor == to.BaseFactor)
        {
            return expr;
        }

        return $"({expr}) * {FormatDouble(from.BaseFactor)} / {FormatDouble(to.BaseFactor)}";
    }

    /// <summary>Builds one filter's SQL predicate, binding every value as a parameter.</summary>
    private static string BuildFilterClause(ComposeFilter filter, ComposeRunContext context, ParamList p)
    {
        var column = ColumnRef(filter.Dimension);
        var values = ResolveValues(filter.Value, context);

        switch (filter.Op)
        {
            case ComposeFilterOp.Eq:
                return $"{column} = ANY({p.AddTextArray(values)})";
            case ComposeFilterOp.Neq:
                return $"{column} <> ALL({p.AddTextArray(values)})";
            case ComposeFilterOp.Like:
                return $"{column} LIKE {p.AddText(First(values))}";
            case ComposeFilterOp.Gt:
                return $"{column} > {p.AddText(First(values))}";
            case ComposeFilterOp.Gte:
                return $"{column} >= {p.AddText(First(values))}";
            case ComposeFilterOp.Lt:
                return $"{column} < {p.AddText(First(values))}";
            case ComposeFilterOp.Lte:
                return $"{column} <= {p.AddText(First(values))}";
            default:
                throw new InvalidOperationException($"Unhandled filter op {filter.Op}");
        }
    }

    /// <summary>Resolves a filter value to concrete strings: a literal set as-is, or a declared variable's
    /// run value (its request binding or default; a missing binding resolves to an empty string).</summary>
    private static IReadOnlyList<string> ResolveValues(ComposeFilterValue value, ComposeRunContext context)
    {
        if (value.VariableRef is not null)
        {
            var resolved = context.Variables.TryGetValue(value.VariableRef, out var v) ? v : null;
            return new[] { resolved ?? "" };
        }

        return value.Literals ?? Array.Empty<string>();
    }

    private static string First(IReadOnlyList<string> values) => values.Count > 0 ? values[0] : "";

    /// <summary>Formats an (always integral) factor / the percentile as a double literal so Postgres never
    /// does integer division (e.g. <c>1048576.0</c>, <c>0.95</c>).</summary>
    private static string FormatDouble(double value)
    {
        if (value == Math.Floor(value) && !double.IsInfinity(value))
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture) + ".0";
        }

        return value.ToString("0.0###############", CultureInfo.InvariantCulture);
    }

    /// <summary>Accumulates bound parameters and hands back their <c>$n</c> placeholders in order.</summary>
    private sealed class ParamList
    {
        private readonly List<NpgsqlParameter> _parameters = new();

        public IReadOnlyList<NpgsqlParameter> Parameters => _parameters;

        public string AddInt(int value)
        {
            _parameters.Add(new NpgsqlParameter<int> { TypedValue = value });
            return Placeholder();
        }

        public string AddTimestamp(DateTime value)
        {
            _parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.SpecifyKind(value, DateTimeKind.Unspecified) });
            return Placeholder();
        }

        public string AddText(string value)
        {
            _parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = value ?? "" });
            return Placeholder();
        }

        public string AddTextArray(IReadOnlyList<string> values)
        {
            _parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = values.ToArray() });
            return Placeholder();
        }

        private string Placeholder() => "$" + _parameters.Count.ToString(CultureInfo.InvariantCulture);
    }
}

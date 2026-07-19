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

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// How a measure's underlying column behaves over time — the single fact that decides which
/// aggregations are legal and which physical column the compiler aggregates. The collector schema
/// (<see cref="PerformanceMonitor.Collectors.CollectorColumn"/>) carries ONLY {name, type,
/// precision, scale}; the archetype (and unit, dimension roles) are this HAND-AUTHORED layer on top,
/// pinned to the collector's real columns by <c>DarlingComposeTests</c>.
/// </summary>
public enum MeasureArchetype
{
    /// <summary>An ever-increasing counter (e.g. <c>wait_time_ms</c>) that ships with a paired
    /// <c>delta_*</c> column. Aggregation operates on the DELTA (summing a raw counter is meaningless);
    /// SUM(delta) over a bucket is throughput, AVG/MIN/MAX(delta) the per-sample spread.</summary>
    Cumulative,

    /// <summary>A column that is ALREADY a per-interval delta (e.g. <c>delta_wait_time_ms</c> read as a
    /// first-class measure). Aggregation operates on the column itself.</summary>
    Delta,

    /// <summary>A point-in-time reading (e.g. <c>sqlserver_cpu_utilization</c> percent). AVG/MIN/MAX only —
    /// summing a gauge over time is a category error (the grain-trap the cpu measure exists to prove).</summary>
    Gauge,

    /// <summary>One row per event (e.g. <c>long_query_completions</c>). COUNT(*) for the event rate, plus
    /// AVG/MIN/MAX and <c>percentile_cont</c> over an event column. The only archetype percentile is legal on.</summary>
    PerEvent,
}

/// <summary>Whether a measure is a single column-backed metric or a numerator/denominator ratio.</summary>
public enum MeasureKind
{
    /// <summary>A single column (or its delta) aggregated by one function.</summary>
    Scalar,

    /// <summary>A ratio of two scalar measures on the SAME source — compiled as
    /// <c>SUM(num)::float / NULLIF(SUM(den), 0)</c>.</summary>
    Ratio,
}

/// <summary>The fixed aggregation vocabulary. <c>percentile_cont</c> is legal ONLY on
/// <see cref="MeasureArchetype.PerEvent"/> measures (the compiler enforces it regardless of the
/// measure's declared <see cref="ComposeMeasure.ValidAggs"/>).</summary>
public enum ComposeAggregate
{
    Sum,
    Avg,
    Min,
    Max,
    Count,
    PercentileCont,
}

/// <summary>The fixed time-bucket vocabulary. <see cref="None"/> is the ranked / scalar mode (no
/// bucketing); the rest map 1:1 to a Postgres <c>date_trunc</c> unit.</summary>
public enum ComposeTimeBucket
{
    None,
    Minute,
    Hour,
    Day,
}

/// <summary>The fixed filter-operator vocabulary — each maps to a compile-time SQL operator string
/// (never a user string), and the value is ALWAYS a bound parameter.</summary>
public enum ComposeFilterOp
{
    /// <summary><c>col = ANY($n)</c> — value is one literal or a list (multi-select).</summary>
    Eq,

    /// <summary><c>col &lt;&gt; ALL($n)</c>.</summary>
    Neq,

    /// <summary><c>col LIKE $n</c> — only on a <see cref="ComposeDimension.Likeable"/> dimension.</summary>
    Like,

    Gt,
    Gte,
    Lt,
    Lte,
}

/// <summary>One unit inside a <see cref="ComposeUnitFamily"/>. <see cref="BaseFactor"/> is how many of
/// the family's smallest unit this unit equals, so converting a value from unit A to unit B is
/// <c>value * A.BaseFactor / B.BaseFactor</c> (both in the same family).</summary>
public sealed record ComposeUnit(string Name, double BaseFactor);

/// <summary>A convertible unit family — the compiler validates a spec's <c>unit</c> is one of these and
/// scales the aggregate by the from→to factor; the catalog serves the list so the composer's unit
/// picker matches.</summary>
public sealed record ComposeUnitFamily(string Name, IReadOnlyList<ComposeUnit> Units)
{
    public ComposeUnit? Unit(string name) =>
        Units.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.Ordinal));

    public bool Has(string name) => Unit(name) is not null;
}

/// <summary>
/// One dimension a source can be filtered or grouped by. <see cref="Column"/> is the physical column
/// (usually == <see cref="Name"/>); <see cref="Likeable"/> gates the <c>LIKE</c> operator;
/// <see cref="ViaModuleJoin"/> marks the query_stats <c>object_name</c>, which is not a query_stats
/// column at all but is stitched read-time from <c>procedure_stats</c> via the #1568 sql_handle join
/// (window-bounded by the compiler).
/// </summary>
public sealed record ComposeDimension(string SourceTable, string Name, string Column, bool Likeable, bool ViaModuleJoin = false);

/// <summary>
/// One measure — a named, composable metric. Everything identifier-bearing (<see cref="SourceTable"/>,
/// <see cref="Column"/>, <see cref="DeltaColumn"/>, <see cref="AllowedDimensions"/>) is validated against
/// the collector catalog by test, so the compiler can trust every field is a real column and emit it
/// schema-qualified without ever touching a user string.
/// </summary>
public sealed record ComposeMeasure
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public required string SourceTable { get; init; }
    public MeasureKind Kind { get; init; } = MeasureKind.Scalar;

    /// <summary>Scalar only — decides the summable column + legal aggregations.</summary>
    public MeasureArchetype Archetype { get; init; }

    /// <summary>Scalar base column (the raw counter / gauge / delta / event column). Null for a ratio.</summary>
    public string? Column { get; init; }

    /// <summary>For a <see cref="MeasureArchetype.Cumulative"/> measure, its paired <c>delta_*</c> column —
    /// the column aggregation actually operates on. Null otherwise.</summary>
    public string? DeltaColumn { get; init; }

    /// <summary>Ratio numerator/denominator — both must be Scalar measures on the SAME source.</summary>
    public string? NumeratorKey { get; init; }
    public string? DenominatorKey { get; init; }

    public required string NativeUnit { get; init; }
    public required string DefaultUnit { get; init; }
    public required string UnitFamily { get; init; }

    public ComposeAggregate DefaultTimeAgg { get; init; }
    public required IReadOnlyList<ComposeAggregate> ValidAggs { get; init; }
    public required IReadOnlyList<string> AllowedDimensions { get; init; }

    /// <summary>
    /// The column a SUM/AVG/MIN/MAX operates on for a scalar measure: the delta for a Cumulative counter,
    /// the column itself for a Delta/PerEvent measure, and null for a Gauge (never summable — the
    /// grain-trap) or a Ratio (handled through its numerator/denominator).
    /// </summary>
    public string? AggregationColumn => Archetype switch
    {
        MeasureArchetype.Cumulative => DeltaColumn,
        MeasureArchetype.Delta => Column,
        MeasureArchetype.PerEvent => Column,
        _ => null,
    };
}

/// <summary>
/// The hand-authored catalog of named measures + dimensions + unit families that Custom Views v2 composes
/// from — the v2 twin of <c>DarlingWebEndpoints.CatalogDescriptors</c> (the v1 read allowlist). It is the
/// SOLE source of identifiers the compiler will emit: a measure names a real collector table + column, so
/// the compiler never accepts a table/column from the caller. <c>DarlingComposeTests</c> pins every
/// (table, column) against the owning collector's <c>PayloadColumns</c>, every measure's time column ==
/// the collector's <c>PrefixTimeColumnName</c>, gauges as non-summable, and ratios as referencing real
/// same-source scalars — so the catalog can never drift from the collectors it reads.
///
/// <para>B1 authors a vertical SLICE (six tables) proving every archetype end-to-end; B3 fills the
/// remaining ~30 collector tables against this same model.</para>
/// </summary>
public static class MeasureCatalog
{
    /* ─────────────────────────── unit families ─────────────────────────── */

    public const string FamilyDuration = "duration";
    public const string FamilyBytes = "bytes";
    public const string FamilyPercent = "percent";
    public const string FamilyCount = "count";
    public const string FamilyFraction = "fraction";

    /// <summary>The convertible unit families. Duration base = microsecond, bytes base = byte, fraction
    /// base = percent (so ratio = 100 percent). percent/count are single-unit (no conversion).</summary>
    public static readonly IReadOnlyList<ComposeUnitFamily> UnitFamilies = new[]
    {
        new ComposeUnitFamily(FamilyDuration, new[]
        {
            new ComposeUnit("us", 1),
            new ComposeUnit("ms", 1_000),
            new ComposeUnit("s", 1_000_000),
            new ComposeUnit("min", 60_000_000),
        }),
        new ComposeUnitFamily(FamilyBytes, new[]
        {
            new ComposeUnit("bytes", 1),
            new ComposeUnit("kb", 1024),
            new ComposeUnit("mb", 1024L * 1024),
            new ComposeUnit("gb", 1024L * 1024 * 1024),
            /* 8-KB data pages, for page-count columns (none in the B1 slice; here for B3). */
            new ComposeUnit("pages", 8192),
        }),
        new ComposeUnitFamily(FamilyPercent, new[] { new ComposeUnit("percent", 1) }),
        new ComposeUnitFamily(FamilyCount, new[] { new ComposeUnit("count", 1) }),
        new ComposeUnitFamily(FamilyFraction, new[]
        {
            new ComposeUnit("percent", 1),
            new ComposeUnit("ratio", 100),
        }),
    };

    private static readonly Dictionary<string, ComposeUnitFamily> s_familyByName =
        UnitFamilies.ToDictionary(f => f.Name, StringComparer.Ordinal);

    public static ComposeUnitFamily? Family(string name) =>
        s_familyByName.TryGetValue(name, out var f) ? f : null;

    /* ─────────────────────────── dimensions ─────────────────────────── */

    /// <summary>Every dimension a slice source can be filtered / grouped by. Keyed by (source, name).
    /// <c>object_name</c> on query_stats is the ONLY <see cref="ComposeDimension.ViaModuleJoin"/> entry —
    /// it is not a query_stats column; the compiler stitches it from procedure_stats (#1568).</summary>
    public static readonly IReadOnlyList<ComposeDimension> Dimensions = new[]
    {
        new ComposeDimension("wait_stats", "wait_type", "wait_type", Likeable: true),

        new ComposeDimension("procedure_stats", "database_name", "database_name", Likeable: true),
        new ComposeDimension("procedure_stats", "schema_name", "schema_name", Likeable: true),
        new ComposeDimension("procedure_stats", "object_name", "object_name", Likeable: true),

        new ComposeDimension("query_stats", "database_name", "database_name", Likeable: true),
        new ComposeDimension("query_stats", "query_hash", "query_hash", Likeable: false),
        /* #1568: not a query_stats column — stitched from procedure_stats.object_name via sql_handle. */
        new ComposeDimension("query_stats", "object_name", "object_name", Likeable: true, ViaModuleJoin: true),

        new ComposeDimension("file_io_stats", "database_name", "database_name", Likeable: true),
        new ComposeDimension("file_io_stats", "file_name", "file_name", Likeable: true),

        new ComposeDimension("long_query_completions", "database_name", "database_name", Likeable: true),
        new ComposeDimension("long_query_completions", "object_name", "object_name", Likeable: true),
        new ComposeDimension("long_query_completions", "result", "result", Likeable: false),
    };

    private static readonly Dictionary<(string Source, string Name), ComposeDimension> s_dimByKey =
        Dimensions.ToDictionary(d => (d.SourceTable, d.Name));

    /// <summary>The dimension for (<paramref name="source"/>, <paramref name="name"/>), or null.</summary>
    public static ComposeDimension? Dimension(string source, string name) =>
        s_dimByKey.TryGetValue((source, name), out var d) ? d : null;

    /* ─────────────────────────── measures ─────────────────────────── */

    private static readonly ComposeAggregate[] CumulativeAggs = { ComposeAggregate.Sum, ComposeAggregate.Avg, ComposeAggregate.Min, ComposeAggregate.Max };
    private static readonly ComposeAggregate[] GaugeAggs = { ComposeAggregate.Avg, ComposeAggregate.Min, ComposeAggregate.Max };
    private static readonly ComposeAggregate[] PerEventAggs = { ComposeAggregate.Count, ComposeAggregate.Sum, ComposeAggregate.Avg, ComposeAggregate.Min, ComposeAggregate.Max, ComposeAggregate.PercentileCont };
    private static readonly ComposeAggregate[] NoAggs = Array.Empty<ComposeAggregate>();

    private const string CatWaits = "Waits";
    private const string CatCpu = "CPU";
    private const string CatProcedures = "Procedures";
    private const string CatQueries = "Queries";
    private const string CatFileIo = "File I/O";
    private const string CatLongQueries = "Long Queries";

    private static readonly string[] WaitDims = { "wait_type" };
    private static readonly string[] ProcDims = { "database_name", "schema_name", "object_name" };
    private static readonly string[] QueryDims = { "database_name", "query_hash", "object_name" };
    private static readonly string[] FileIoDims = { "database_name", "file_name" };
    private static readonly string[] LqcDims = { "database_name", "object_name", "result" };
    private static readonly string[] NoDims = Array.Empty<string>();

    /// <summary>The catalog. Every measure's SourceTable is a real collector; every Column/DeltaColumn is a
    /// real payload column of that collector (pinned by test).</summary>
    public static readonly IReadOnlyList<ComposeMeasure> Measures = new[]
    {
        /* ── wait_stats (server-grain; dimension wait_type) — Cumulative + Delta + Ratio ── */
        new ComposeMeasure
        {
            Key = "wait_time_ms", DisplayName = "Wait time", Category = CatWaits, SourceTable = "wait_stats",
            Archetype = MeasureArchetype.Cumulative, Column = "wait_time_ms", DeltaColumn = "delta_wait_time_ms",
            NativeUnit = "ms", DefaultUnit = "ms", UnitFamily = FamilyDuration,
            DefaultTimeAgg = ComposeAggregate.Sum, ValidAggs = CumulativeAggs, AllowedDimensions = WaitDims,
        },
        new ComposeMeasure
        {
            Key = "wait_time_delta_ms", DisplayName = "Wait time (per-sample delta)", Category = CatWaits, SourceTable = "wait_stats",
            Archetype = MeasureArchetype.Delta, Column = "delta_wait_time_ms",
            NativeUnit = "ms", DefaultUnit = "ms", UnitFamily = FamilyDuration,
            DefaultTimeAgg = ComposeAggregate.Sum, ValidAggs = CumulativeAggs, AllowedDimensions = WaitDims,
        },
        new ComposeMeasure
        {
            Key = "signal_wait_time_ms", DisplayName = "Signal wait time", Category = CatWaits, SourceTable = "wait_stats",
            Archetype = MeasureArchetype.Cumulative, Column = "signal_wait_time_ms", DeltaColumn = "delta_signal_wait_time_ms",
            NativeUnit = "ms", DefaultUnit = "ms", UnitFamily = FamilyDuration,
            DefaultTimeAgg = ComposeAggregate.Sum, ValidAggs = CumulativeAggs, AllowedDimensions = WaitDims,
        },
        new ComposeMeasure
        {
            Key = "waiting_tasks", DisplayName = "Waiting tasks", Category = CatWaits, SourceTable = "wait_stats",
            Archetype = MeasureArchetype.Cumulative, Column = "waiting_tasks_count", DeltaColumn = "delta_waiting_tasks",
            NativeUnit = "count", DefaultUnit = "count", UnitFamily = FamilyCount,
            DefaultTimeAgg = ComposeAggregate.Sum, ValidAggs = CumulativeAggs, AllowedDimensions = WaitDims,
        },
        new ComposeMeasure
        {
            Key = "signal_wait_pct", DisplayName = "Signal wait %", Category = CatWaits, SourceTable = "wait_stats",
            Kind = MeasureKind.Ratio, NumeratorKey = "signal_wait_time_ms", DenominatorKey = "wait_time_ms",
            NativeUnit = "ratio", DefaultUnit = "percent", UnitFamily = FamilyFraction,
            ValidAggs = NoAggs, AllowedDimensions = WaitDims,
        },

        /* ── cpu_utilization_stats (server-grain; NO dimensions) — Gauge (the grain-trap) ── */
        new ComposeMeasure
        {
            Key = "sqlserver_cpu_utilization", DisplayName = "SQL Server CPU %", Category = CatCpu, SourceTable = "cpu_utilization_stats",
            Archetype = MeasureArchetype.Gauge, Column = "sqlserver_cpu_utilization",
            NativeUnit = "percent", DefaultUnit = "percent", UnitFamily = FamilyPercent,
            DefaultTimeAgg = ComposeAggregate.Avg, ValidAggs = GaugeAggs, AllowedDimensions = NoDims,
        },
        new ComposeMeasure
        {
            Key = "other_process_cpu_utilization", DisplayName = "Other-process CPU %", Category = CatCpu, SourceTable = "cpu_utilization_stats",
            Archetype = MeasureArchetype.Gauge, Column = "other_process_cpu_utilization",
            NativeUnit = "percent", DefaultUnit = "percent", UnitFamily = FamilyPercent,
            DefaultTimeAgg = ComposeAggregate.Avg, ValidAggs = GaugeAggs, AllowedDimensions = NoDims,
        },

        /* ── procedure_stats (database + object grain) — Cumulative (µs) ── */
        new ComposeMeasure
        {
            Key = "proc_elapsed_us", DisplayName = "Procedure elapsed time", Category = CatProcedures, SourceTable = "procedure_stats",
            Archetype = MeasureArchetype.Cumulative, Column = "total_elapsed_time", DeltaColumn = "delta_elapsed_time",
            NativeUnit = "us", DefaultUnit = "ms", UnitFamily = FamilyDuration,
            DefaultTimeAgg = ComposeAggregate.Sum, ValidAggs = CumulativeAggs, AllowedDimensions = ProcDims,
        },
        new ComposeMeasure
        {
            Key = "proc_worker_us", DisplayName = "Procedure CPU (worker) time", Category = CatProcedures, SourceTable = "procedure_stats",
            Archetype = MeasureArchetype.Cumulative, Column = "total_worker_time", DeltaColumn = "delta_worker_time",
            NativeUnit = "us", DefaultUnit = "ms", UnitFamily = FamilyDuration,
            DefaultTimeAgg = ComposeAggregate.Sum, ValidAggs = CumulativeAggs, AllowedDimensions = ProcDims,
        },
        new ComposeMeasure
        {
            Key = "proc_executions", DisplayName = "Procedure executions", Category = CatProcedures, SourceTable = "procedure_stats",
            Archetype = MeasureArchetype.Cumulative, Column = "execution_count", DeltaColumn = "delta_execution_count",
            NativeUnit = "count", DefaultUnit = "count", UnitFamily = FamilyCount,
            DefaultTimeAgg = ComposeAggregate.Sum, ValidAggs = CumulativeAggs, AllowedDimensions = ProcDims,
        },

        /* ── query_stats (database + query_hash grain; object_name via #1568 join) — Cumulative (µs) ── */
        new ComposeMeasure
        {
            Key = "query_elapsed_us", DisplayName = "Query elapsed time", Category = CatQueries, SourceTable = "query_stats",
            Archetype = MeasureArchetype.Cumulative, Column = "total_elapsed_time", DeltaColumn = "delta_elapsed_time",
            NativeUnit = "us", DefaultUnit = "ms", UnitFamily = FamilyDuration,
            DefaultTimeAgg = ComposeAggregate.Sum, ValidAggs = CumulativeAggs, AllowedDimensions = QueryDims,
        },
        new ComposeMeasure
        {
            Key = "query_worker_us", DisplayName = "Query CPU (worker) time", Category = CatQueries, SourceTable = "query_stats",
            Archetype = MeasureArchetype.Cumulative, Column = "total_worker_time", DeltaColumn = "delta_worker_time",
            NativeUnit = "us", DefaultUnit = "ms", UnitFamily = FamilyDuration,
            DefaultTimeAgg = ComposeAggregate.Sum, ValidAggs = CumulativeAggs, AllowedDimensions = QueryDims,
        },

        /* ── file_io_stats (database + file grain) — Cumulative (bytes + ms) ── */
        new ComposeMeasure
        {
            Key = "file_read_bytes", DisplayName = "File bytes read", Category = CatFileIo, SourceTable = "file_io_stats",
            Archetype = MeasureArchetype.Cumulative, Column = "read_bytes", DeltaColumn = "delta_read_bytes",
            NativeUnit = "bytes", DefaultUnit = "mb", UnitFamily = FamilyBytes,
            DefaultTimeAgg = ComposeAggregate.Sum, ValidAggs = CumulativeAggs, AllowedDimensions = FileIoDims,
        },
        new ComposeMeasure
        {
            Key = "file_write_bytes", DisplayName = "File bytes written", Category = CatFileIo, SourceTable = "file_io_stats",
            Archetype = MeasureArchetype.Cumulative, Column = "write_bytes", DeltaColumn = "delta_write_bytes",
            NativeUnit = "bytes", DefaultUnit = "mb", UnitFamily = FamilyBytes,
            DefaultTimeAgg = ComposeAggregate.Sum, ValidAggs = CumulativeAggs, AllowedDimensions = FileIoDims,
        },
        new ComposeMeasure
        {
            Key = "file_io_stall_read_ms", DisplayName = "File read stall", Category = CatFileIo, SourceTable = "file_io_stats",
            Archetype = MeasureArchetype.Cumulative, Column = "io_stall_read_ms", DeltaColumn = "delta_stall_read_ms",
            NativeUnit = "ms", DefaultUnit = "ms", UnitFamily = FamilyDuration,
            DefaultTimeAgg = ComposeAggregate.Sum, ValidAggs = CumulativeAggs, AllowedDimensions = FileIoDims,
        },
        new ComposeMeasure
        {
            Key = "file_io_stall_write_ms", DisplayName = "File write stall", Category = CatFileIo, SourceTable = "file_io_stats",
            Archetype = MeasureArchetype.Cumulative, Column = "io_stall_write_ms", DeltaColumn = "delta_stall_write_ms",
            NativeUnit = "ms", DefaultUnit = "ms", UnitFamily = FamilyDuration,
            DefaultTimeAgg = ComposeAggregate.Sum, ValidAggs = CumulativeAggs, AllowedDimensions = FileIoDims,
        },

        /* ── long_query_completions (per-event) — PerEvent (proves count + percentile_cont) ── */
        new ComposeMeasure
        {
            Key = "lqc_duration_us", DisplayName = "Completion duration", Category = CatLongQueries, SourceTable = "long_query_completions",
            Archetype = MeasureArchetype.PerEvent, Column = "duration_microseconds",
            NativeUnit = "us", DefaultUnit = "ms", UnitFamily = FamilyDuration,
            DefaultTimeAgg = ComposeAggregate.Avg, ValidAggs = PerEventAggs, AllowedDimensions = LqcDims,
        },
        new ComposeMeasure
        {
            Key = "lqc_cpu_time_us", DisplayName = "Completion CPU time", Category = CatLongQueries, SourceTable = "long_query_completions",
            Archetype = MeasureArchetype.PerEvent, Column = "cpu_time_microseconds",
            NativeUnit = "us", DefaultUnit = "ms", UnitFamily = FamilyDuration,
            DefaultTimeAgg = ComposeAggregate.Avg, ValidAggs = PerEventAggs, AllowedDimensions = LqcDims,
        },
    };

    private static readonly Dictionary<string, ComposeMeasure> s_measureByKey =
        Measures.ToDictionary(m => m.Key, StringComparer.Ordinal);

    /// <summary>The measure with this key, or null.</summary>
    public static ComposeMeasure? Measure(string? key) =>
        key is not null && s_measureByKey.TryGetValue(key, out var m) ? m : null;

    /// <summary>Whether <paramref name="source"/> is a real measure source (a collector table the catalog serves).</summary>
    public static bool IsKnownSource(string? source) =>
        source is not null && Measures.Any(m => string.Equals(m.SourceTable, source, StringComparison.Ordinal));

    /* ─────────────────────────── wire-name maps for the enums ─────────────────────────── */

    private static readonly (ComposeAggregate Value, string Wire)[] s_aggWire =
    {
        (ComposeAggregate.Sum, "sum"), (ComposeAggregate.Avg, "avg"), (ComposeAggregate.Min, "min"),
        (ComposeAggregate.Max, "max"), (ComposeAggregate.Count, "count"), (ComposeAggregate.PercentileCont, "percentile_cont"),
    };

    private static readonly (ComposeTimeBucket Value, string Wire, int Seconds)[] s_bucketWire =
    {
        (ComposeTimeBucket.None, "none", 0), (ComposeTimeBucket.Minute, "minute", 60),
        (ComposeTimeBucket.Hour, "hour", 3600), (ComposeTimeBucket.Day, "day", 86_400),
    };

    private static readonly (ComposeFilterOp Value, string Wire)[] s_opWire =
    {
        (ComposeFilterOp.Eq, "eq"), (ComposeFilterOp.Neq, "neq"), (ComposeFilterOp.Like, "like"),
        (ComposeFilterOp.Gt, "gt"), (ComposeFilterOp.Gte, "gte"), (ComposeFilterOp.Lt, "lt"), (ComposeFilterOp.Lte, "lte"),
    };

    public static IReadOnlyList<string> AggregateWireNames { get; } = s_aggWire.Select(x => x.Wire).ToArray();
    public static IReadOnlyList<string> TimeBucketWireNames { get; } = s_bucketWire.Select(x => x.Wire).ToArray();
    public static IReadOnlyList<string> FilterOpWireNames { get; } = s_opWire.Select(x => x.Wire).ToArray();

    public static bool TryParseAggregate(string? wire, out ComposeAggregate value)
    {
        foreach (var (v, w) in s_aggWire)
        {
            if (string.Equals(w, wire, StringComparison.Ordinal)) { value = v; return true; }
        }
        value = default;
        return false;
    }

    public static string WireName(ComposeAggregate value) => s_aggWire.First(x => x.Value == value).Wire;

    public static bool TryParseTimeBucket(string? wire, out ComposeTimeBucket value)
    {
        foreach (var (v, w, _) in s_bucketWire)
        {
            if (string.Equals(w, wire, StringComparison.Ordinal)) { value = v; return true; }
        }
        value = default;
        return false;
    }

    public static string WireName(ComposeTimeBucket value) => s_bucketWire.First(x => x.Value == value).Wire;

    /// <summary>Seconds per bucket (0 for <see cref="ComposeTimeBucket.None"/>) — drives the window×resolution ceiling.</summary>
    public static int BucketSeconds(ComposeTimeBucket value) => s_bucketWire.First(x => x.Value == value).Seconds;

    /// <summary>The Postgres <c>date_trunc</c> field for a real bucket (a compile-time constant, never a user string).</summary>
    public static string DateTruncField(ComposeTimeBucket value) => value switch
    {
        ComposeTimeBucket.Minute => "minute",
        ComposeTimeBucket.Hour => "hour",
        ComposeTimeBucket.Day => "day",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "None has no date_trunc field"),
    };

    public static bool TryParseFilterOp(string? wire, out ComposeFilterOp value)
    {
        foreach (var (v, w) in s_opWire)
        {
            if (string.Equals(w, wire, StringComparison.Ordinal)) { value = v; return true; }
        }
        value = default;
        return false;
    }

    public static string WireName(ComposeFilterOp value) => s_opWire.First(x => x.Value == value).Wire;
}

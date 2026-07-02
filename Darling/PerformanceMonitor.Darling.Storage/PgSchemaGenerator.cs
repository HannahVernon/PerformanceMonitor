/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Text;
using PerformanceMonitor.Collectors;

namespace PerformanceMonitor.Darling.Storage;

/// <summary>
/// Generates Darling's Postgres schema from the collector definitions' engine-neutral column
/// metadata (<see cref="ICollectorSchemaInfo"/>) — the same metadata Lite's DuckDB schema was
/// hand-written against, so a collector change lands in exactly one place and both stores stay
/// column-for-column identical. Names and types mirror Lite's DuckDB schema (collection_id /
/// deadlock_id / config_id prefixes, capture_time on the config snapshots, per-column
/// numeric(p,s)) so analysis SQL can twin without translation. Two deliberate physical
/// deviations from Lite: (1) DuckDB's PRIMARY KEY on the prefix id column is NOT carried over —
/// TimescaleDB hypertables require the partition column in any unique constraint, and bulk COPY
/// ingest doesn't want one; the id stays a NOT NULL bigint. (2) Index names are uniform
/// (idx_&lt;table&gt;_time) where Lite's handwritten names are irregular (idx_cpu_time) — index
/// names are physical-only and never referenced by analysis SQL.
/// </summary>
public static class PgSchemaGenerator
{
    /// <summary>Maps an engine-neutral collector column to its Postgres type.</summary>
    public static string TypeFor(CollectorColumn column)
    {
        if (column is null)
        {
            throw new ArgumentNullException(nameof(column));
        }

        switch (column.Type)
        {
            case CollectorColumnType.BigInt: return "bigint";
            case CollectorColumnType.Integer: return "integer";
            case CollectorColumnType.SmallInt: return "smallint";
            case CollectorColumnType.Varchar: return "text";
            /* UTC by convention across the product; matches DuckDB TIMESTAMP (no tz). */
            case CollectorColumnType.Timestamp: return "timestamp";
            case CollectorColumnType.Double: return "double precision";
            case CollectorColumnType.Decimal:
                if (column.Precision <= 0)
                {
                    throw new InvalidOperationException(
                        $"Decimal column '{column.Name}' has no declared precision/scale — mirror the DuckDB DECIMAL(p,s) from Lite's schema.");
                }
                return $"numeric({column.Precision},{column.Scale})";
            case CollectorColumnType.Boolean: return "boolean";
            default:
                throw new ArgumentOutOfRangeException(nameof(column), column.Type, "Unmapped collector column type");
        }
    }

    /// <summary>Emits CREATE TABLE IF NOT EXISTS for one collector's destination table.</summary>
    public static string CreateTable(ICollectorSchemaInfo schema)
    {
        if (schema is null)
        {
            throw new ArgumentNullException(nameof(schema));
        }

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE IF NOT EXISTS ").Append(schema.TargetTable).Append(" (\n");

        if (schema.IncludesCollectionId)
        {
            sb.Append("    ").Append(schema.PrefixIdColumnName).Append(" bigint NOT NULL,\n");
        }

        sb.Append("    ").Append(schema.PrefixTimeColumnName).Append(" timestamp NOT NULL,\n");
        sb.Append("    server_id integer NOT NULL,\n");
        sb.Append("    server_name text NOT NULL");

        foreach (var column in schema.PayloadColumns)
        {
            sb.Append(",\n    ").Append(column.Name).Append(' ').Append(TypeFor(column));
        }

        sb.Append("\n);");
        return sb.ToString();
    }

    /// <summary>
    /// Emits the table's retrieval index, mirroring Lite's index COLUMNS exactly: the default is
    /// (server_id, &lt;prefix time&gt;); memory_pressure_events indexes its payload sample_time,
    /// index_object_stats has its composite object drill index, and server_config /
    /// database_config have no index in Lite. Returns null when the table has none.
    /// </summary>
    public static string? CreateIndex(ICollectorSchemaInfo schema)
    {
        if (schema is null)
        {
            throw new ArgumentNullException(nameof(schema));
        }

        switch (schema.TargetTable)
        {
            case "server_config":
            case "database_config":
                return null;
            case "memory_pressure_events":
                return "CREATE INDEX IF NOT EXISTS idx_memory_pressure_events_time ON memory_pressure_events(server_id, sample_time);";
            case "index_object_stats":
                return "CREATE INDEX IF NOT EXISTS idx_index_object_stats_object ON index_object_stats(server_id, database_name, object_id, index_id, collection_time);";
            default:
                return $"CREATE INDEX IF NOT EXISTS idx_{schema.TargetTable}_time ON {schema.TargetTable}(server_id, {schema.PrefixTimeColumnName});";
        }
    }

    /// <summary>
    /// The full collector-table schema script in catalog order — the body of Darling's first
    /// versioned migration. TimescaleDB hypertable conversion is deliberately NOT emitted here;
    /// it is applied by the migration runner only when the extension is present (validated
    /// against a live Postgres before it ships).
    /// </summary>
    public static string GenerateFullSchema()
    {
        var sb = new StringBuilder();
        sb.Append("/* Darling collector tables — generated from PerformanceMonitor.Collectors definitions.\n");
        sb.Append("   Column names and types mirror Lite's DuckDB schema (see PgSchemaGenerator remarks). */\n");

        foreach (var schema in CollectorCatalog.All)
        {
            sb.Append('\n').Append(CreateTable(schema)).Append('\n');

            var index = CreateIndex(schema);
            if (index is not null)
            {
                sb.Append(index).Append('\n');
            }
        }

        return sb.ToString();
    }
}

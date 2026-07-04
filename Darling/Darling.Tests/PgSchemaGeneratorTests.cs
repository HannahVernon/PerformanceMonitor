/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins Darling's generated Postgres schema against the collector definitions: the catalog covers
/// all 26 collectors, the type map mirrors Lite's DuckDB types (per-column numeric(p,s) included),
/// the prefix names vary exactly where Lite's schema varies (deadlock_id / blocked_report_id /
/// config_id+capture_time / running_jobs' no-id), and the index shapes mirror Lite's columns.
/// </summary>
public sealed class PgSchemaGeneratorTests
{
    [Fact]
    public void Catalog_CoversAll26Collectors_WithUniqueTablesAndNames()
    {
        Assert.Equal(26, CollectorCatalog.All.Count);
        Assert.Equal(26, CollectorCatalog.All.Select(s => s.TargetTable).Distinct().Count());
        Assert.Equal(26, CollectorCatalog.All.Select(s => s.Name).Distinct().Count());
    }

    [Fact]
    public void Catalog_PrefixNames_MirrorLiteSchemaExceptions()
    {
        var byTable = CollectorCatalog.All.ToDictionary(s => s.TargetTable);

        Assert.Equal("deadlock_id", byTable["deadlocks"].PrefixIdColumnName);
        Assert.Equal("blocked_report_id", byTable["blocked_process_reports"].PrefixIdColumnName);
        foreach (var config in new[] { "server_config", "database_config", "database_scoped_config", "trace_flags" })
        {
            Assert.Equal("config_id", byTable[config].PrefixIdColumnName);
            Assert.Equal("capture_time", byTable[config].PrefixTimeColumnName);
        }
        Assert.False(byTable["running_jobs"].IncludesCollectionId);
        Assert.Equal("collection_id", byTable["wait_stats"].PrefixIdColumnName);
        Assert.Equal("collection_time", byTable["wait_stats"].PrefixTimeColumnName);
    }

    [Fact]
    public void Catalog_EveryDecimalColumn_DeclaresPrecision()
    {
        var undeclared = CollectorCatalog.All
            .SelectMany(s => s.PayloadColumns.Select(c => (s.TargetTable, Column: c)))
            .Where(x => x.Column.Type == CollectorColumnType.Decimal && x.Column.Precision <= 0)
            .Select(x => $"{x.TargetTable}.{x.Column.Name}")
            .ToArray();

        Assert.Empty(undeclared);
    }

    [Fact]
    public void TypeFor_MapsEveryColumnType()
    {
        Assert.Equal("bigint", PgSchemaGenerator.TypeFor(new CollectorColumn("c", CollectorColumnType.BigInt)));
        Assert.Equal("integer", PgSchemaGenerator.TypeFor(new CollectorColumn("c", CollectorColumnType.Integer)));
        Assert.Equal("smallint", PgSchemaGenerator.TypeFor(new CollectorColumn("c", CollectorColumnType.SmallInt)));
        Assert.Equal("text", PgSchemaGenerator.TypeFor(new CollectorColumn("c", CollectorColumnType.Varchar)));
        Assert.Equal("timestamp", PgSchemaGenerator.TypeFor(new CollectorColumn("c", CollectorColumnType.Timestamp)));
        Assert.Equal("double precision", PgSchemaGenerator.TypeFor(new CollectorColumn("c", CollectorColumnType.Double)));
        Assert.Equal("numeric(18,2)", PgSchemaGenerator.TypeFor(new CollectorColumn("c", CollectorColumnType.Decimal, 18, 2)));
        Assert.Equal("numeric(5,2)", PgSchemaGenerator.TypeFor(new CollectorColumn("c", CollectorColumnType.Decimal, 5, 2)));
        Assert.Equal("boolean", PgSchemaGenerator.TypeFor(new CollectorColumn("c", CollectorColumnType.Boolean)));
        Assert.Throws<InvalidOperationException>(
            () => PgSchemaGenerator.TypeFor(new CollectorColumn("c", CollectorColumnType.Decimal)));
    }

    [Fact]
    public void CreateTable_Deadlocks_FullDdlPinned()
    {
        var ddl = PgSchemaGenerator.CreateTable(DeadlocksCollector.Instance);

        Assert.Equal(
            "CREATE TABLE IF NOT EXISTS deadlocks (\n" +
            "    deadlock_id bigint NOT NULL,\n" +
            "    collection_time timestamp NOT NULL,\n" +
            "    server_id integer NOT NULL,\n" +
            "    server_name text NOT NULL,\n" +
            "    deadlock_time timestamp,\n" +
            "    victim_process_id text,\n" +
            "    victim_sql_text text,\n" +
            "    deadlock_graph_xml text,\n" +
            "    victim_query_plan_xml text\n" +
            ");",
            ddl);
    }

    [Fact]
    public void CreateTable_RunningJobs_HasNoIdColumn()
    {
        var ddl = PgSchemaGenerator.CreateTable(RunningJobsCollector.Instance);

        Assert.StartsWith("CREATE TABLE IF NOT EXISTS running_jobs (\n    collection_time timestamp NOT NULL,", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("collection_id", ddl, StringComparison.Ordinal);
        Assert.Contains("percent_of_average numeric(10,1)", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateTable_ServerConfig_UsesConfigIdAndCaptureTime()
    {
        var ddl = PgSchemaGenerator.CreateTable(ServerConfigCollector.Instance);

        Assert.StartsWith(
            "CREATE TABLE IF NOT EXISTS server_config (\n" +
            "    config_id bigint NOT NULL,\n" +
            "    capture_time timestamp NOT NULL,",
            ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateTable_QuerySnapshots_MirrorsPerColumnNumericPrecision()
    {
        var ddl = PgSchemaGenerator.CreateTable(QuerySnapshotsCollector.Instance);

        Assert.Contains("granted_query_memory_gb numeric(18,2)", ddl, StringComparison.Ordinal);
        Assert.Contains("percent_complete numeric(5,2)", ddl, StringComparison.Ordinal);
        Assert.Contains("is_cdc_capture boolean", ddl, StringComparison.Ordinal);
        Assert.Contains("requested_memory_mb double precision", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateIndex_MirrorsLiteIndexColumns()
    {
        Assert.Equal(
            "CREATE INDEX IF NOT EXISTS idx_wait_stats_time ON wait_stats(server_id, collection_time);",
            PgSchemaGenerator.CreateIndex(WaitStatsCollector.Instance));
        Assert.Equal(
            "CREATE INDEX IF NOT EXISTS idx_trace_flags_time ON trace_flags(server_id, capture_time);",
            PgSchemaGenerator.CreateIndex(TraceFlagsCollector.Instance));
        Assert.Equal(
            "CREATE INDEX IF NOT EXISTS idx_memory_pressure_events_time ON memory_pressure_events(server_id, sample_time);",
            PgSchemaGenerator.CreateIndex(MemoryPressureEventsCollector.Instance));
        Assert.Equal(
            "CREATE INDEX IF NOT EXISTS idx_index_object_stats_object ON index_object_stats(server_id, database_name, object_id, index_id, collection_time);",
            PgSchemaGenerator.CreateIndex(IndexObjectStatsCollector.Instance));
        Assert.Null(PgSchemaGenerator.CreateIndex(ServerConfigCollector.Instance));
        Assert.Null(PgSchemaGenerator.CreateIndex(DatabaseConfigCollector.Instance));
    }

    [Fact]
    public void GenerateFullSchema_EmitsEveryTableAndIndex()
    {
        var script = PgSchemaGenerator.GenerateFullSchema();

        var tableCount = CollectorCatalog.All.Count(s => script.Contains($"CREATE TABLE IF NOT EXISTS {s.TargetTable} (", StringComparison.Ordinal));
        Assert.Equal(26, tableCount);

        /* 26 tables minus the two index-less config tables = 24 indexes. */
        var indexCount = script.Split("CREATE INDEX IF NOT EXISTS").Length - 1;
        Assert.Equal(24, indexCount);

        /* The precision guard can never regress silently. */
        Assert.DoesNotContain("numeric(0,0)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateV8Move_CreatesSchemas_AndMovesEveryCatalogTableToCollect()
    {
        var v8 = PgSchemaGenerator.GenerateV8Move();

        Assert.Contains("CREATE SCHEMA IF NOT EXISTS collect AUTHORIZATION darling;", v8, StringComparison.Ordinal);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS config AUTHORIZATION darling;", v8, StringComparison.Ordinal);

        /* Every collector table moves to collect — catalog-driven, so a new collector moves for free. */
        foreach (var schema in CollectorCatalog.All)
        {
            Assert.Contains($"ALTER TABLE IF EXISTS public.{schema.TargetTable} SET SCHEMA collect;", v8, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GenerateV8Move_MovesMetadataAndViewsToCollect_AndConfigTablesToConfig()
    {
        var v8 = PgSchemaGenerator.GenerateV8Move();

        foreach (var table in PgSchemaGenerator.CollectMetadataTables)
        {
            Assert.Contains($"ALTER TABLE IF EXISTS public.{table} SET SCHEMA collect;", v8, StringComparison.Ordinal);
        }

        Assert.Equal(24, PgSchemaGenerator.CollectViews.Count);
        foreach (var view in PgSchemaGenerator.CollectViews)
        {
            Assert.Contains($"ALTER VIEW IF EXISTS public.{view} SET SCHEMA collect;", v8, StringComparison.Ordinal);
        }

        /* Exactly the operator-writable coordination tables go to config — the admin write surface. */
        Assert.Equal(
            new[] { "config_alert_log", "config_edge_trigger_watermarks", "config_mute_rules", "analysis_muted" },
            PgSchemaGenerator.ConfigTables);
        foreach (var table in PgSchemaGenerator.ConfigTables)
        {
            Assert.Contains($"ALTER TABLE IF EXISTS public.{table} SET SCHEMA config;", v8, StringComparison.Ordinal);
        }

        /* analysis_findings is service-written / viewer-read-only -> collect, not config (fork #1). */
        Assert.Contains("ALTER TABLE IF EXISTS public.analysis_findings SET SCHEMA collect;", v8, StringComparison.Ordinal);
        Assert.DoesNotContain("public.analysis_findings SET SCHEMA config;", v8, StringComparison.Ordinal);

        /* The ALTER DATABASE search_path default is NOT baked into V8 — it is best-effort in MigrateAsync. */
        Assert.DoesNotContain("ALTER DATABASE", v8, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchPath_ListsCollectConfigPublicInOrder()
    {
        Assert.Equal("collect, config, public", PgSchemaGenerator.SearchPath);
    }
}

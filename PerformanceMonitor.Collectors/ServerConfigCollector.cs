/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// Server configuration from sys.configurations (on-load only, not scheduled). Extracted
/// verbatim from Lite's RemoteCollectorService.ServerConfig.cs.
/// </summary>
public sealed class ServerConfigCollector : CollectorDefinitionBase<ServerConfigCollector.Row>
{
    public static ServerConfigCollector Instance { get; } = new();

    private ServerConfigCollector()
    {
    }

    public readonly record struct Row(string Name, long ValueConfigured, long ValueInUse, bool IsDynamic, bool IsAdvanced);

    private const string QueryText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    configuration_name = c.name,
    value_configured = CONVERT(bigint, c.value),
    value_in_use = CONVERT(bigint, c.value_in_use),
    is_dynamic = c.is_dynamic,
    is_advanced = c.is_advanced
FROM sys.configurations AS c
ORDER BY c.name
OPTION(RECOMPILE);";

    public override string Name => "server_config";

    public override string TargetTable => "server_config";

    /// <summary>The config snapshots' prefix is config_id/capture_time in Lite's schema; Darling mirrors it.</summary>
    public override string PrefixIdColumnName => "config_id";

    public override string PrefixTimeColumnName => "capture_time";

    public override CollectorQuery BuildQuery(CollectorContext context) => new(QueryText);

    public override IReadOnlyList<CollectorColumn> PayloadColumns { get; } = new[]
    {
        new CollectorColumn("configuration_name", CollectorColumnType.Varchar),
        new CollectorColumn("value_configured", CollectorColumnType.BigInt),
        new CollectorColumn("value_in_use", CollectorColumnType.BigInt),
        new CollectorColumn("is_dynamic", CollectorColumnType.Boolean),
        new CollectorColumn("is_advanced", CollectorColumnType.Boolean),
    };

    public override async ValueTask<List<Row>> ReadAsync(DbDataReader reader, CollectorContext context, CancellationToken cancellationToken)
    {
        var rows = new List<Row>();

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new Row(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }

        return rows;
    }

    public override void WritePayload(Row row, ICollectorRowWriter writer, CollectorContext context)
    {
        writer
            .Value(row.Name)               /* configuration_name VARCHAR */
            .Value(row.ValueConfigured)    /* value_configured BIGINT */
            .Value(row.ValueInUse)         /* value_in_use BIGINT */
            .Value(row.IsDynamic)          /* is_dynamic BOOLEAN */
            .Value(row.IsAdvanced);        /* is_advanced BOOLEAN */
    }
}

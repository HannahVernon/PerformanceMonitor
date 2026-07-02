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
/// Top memory clerks from sys.dm_os_memory_clerks (TOP 25, above 1 MB). Extracted verbatim
/// from Lite's RemoteCollectorService.Memory.cs.
/// </summary>
public sealed class MemoryClerksCollector : ICollectorDefinition<MemoryClerksCollector.Row>
{
    public static MemoryClerksCollector Instance { get; } = new();

    private MemoryClerksCollector()
    {
    }

    public readonly record struct Row(string ClerkType, decimal MemoryMb);

    private const string QueryText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP (25)
    clerk_type = mc.type,
    memory_mb = CONVERT(decimal(18,2), SUM(mc.pages_kb) / 1024.0)
FROM sys.dm_os_memory_clerks AS mc
GROUP BY
    mc.type
HAVING
    SUM(mc.pages_kb) > 1024
ORDER BY
    SUM(mc.pages_kb) DESC
OPTION(RECOMPILE);";

    public string Name => "memory_clerks";

    public string TargetTable => "memory_clerks";

    public string? WatermarkColumn => null;

    public bool AppliesTo(CollectorTargetInfo target) => true;

    public bool RunsPerDatabase(CollectorTargetInfo target) => false;

    public CollectorQuery BuildQuery(CollectorContext context) => new(QueryText);

    public IReadOnlyList<CollectorColumn> PayloadColumns { get; } = new[]
    {
        new CollectorColumn("clerk_type", CollectorColumnType.Varchar),
        new CollectorColumn("memory_mb", CollectorColumnType.Decimal),
    };

    public async ValueTask<List<Row>> ReadAsync(DbDataReader reader, CollectorContext context, CancellationToken cancellationToken)
    {
        var rows = new List<Row>();

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new Row(reader.GetString(0), reader.GetDecimal(1)));
        }

        return rows;
    }

    public void WritePayload(Row row, ICollectorRowWriter writer, CollectorContext context)
    {
        writer
            .Value(row.ClerkType)   /* clerk_type VARCHAR */
            .Value(row.MemoryMb);   /* memory_mb DECIMAL */
    }
}

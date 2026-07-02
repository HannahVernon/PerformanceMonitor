/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// Deadlock events from the app-managed PerformanceMonitor_Deadlock XE ring-buffer session.
/// Extracted verbatim from Lite's RemoteCollectorService.Deadlocks.cs: the server-scoped read
/// (on-prem/MI/RDS, event xml_deadlock_report) vs the database-scoped read (Azure SQL DB, event
/// database_xml_deadlock_report), the deadlock_time watermark that keeps ring-buffer lingerers
/// from re-inserting (10-minute fallback window), and the victim-inputbuf extraction from the
/// graph XML (parsed in the SQL/read phase — XElement.Parse is expensive and was previously
/// misattributed as storage time). Session lifecycle (create/start/ensure) stays host-side; the
/// session name lives here so the reader and the lifecycle can never disagree on it.
/// </summary>
public sealed class DeadlocksCollector : CollectorDefinitionBase<DeadlocksCollector.Row>
{
    public static DeadlocksCollector Instance { get; } = new();

    private DeadlocksCollector()
    {
    }

    /* We create and manage our own XE session to avoid conflicts with user's existing sessions */
    public const string XeSessionName = "PerformanceMonitor_Deadlock";

    public sealed class Row
    {
        public DateTime? DeadlockTime { get; set; }
        public string? VictimProcessId { get; set; }
        public string? VictimSqlText { get; set; }
        public string? GraphXml { get; set; }
    }

    /* Azure SQL DB: read from ring_buffer (database-scoped session)
       Azure SQL DB uses database_xml_deadlock_report event instead of xml_deadlock_report.
       Use .query() to get XML with structure intact, then CONVERT to nvarchar(max) */
    private const string AzureQueryText = $@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE
    @PerformanceMonitor_Deadlock TABLE
(
    ring_buffer xml NOT NULL
);

INSERT
    @PerformanceMonitor_Deadlock
(
    ring_buffer
)
SELECT /* PerformanceMonitorLite */
    ring_xml = TRY_CAST(xet.target_data AS xml)
FROM sys.dm_xe_database_session_targets AS xet
JOIN sys.dm_xe_database_sessions AS xes
  ON xes.address = xet.event_session_address
WHERE xes.name = N'{XeSessionName}'
AND   xet.target_name = N'ring_buffer'
OPTION(RECOMPILE);

SELECT
    deadlock_time = evt.value('(@timestamp)[1]', 'datetime2'),
    victim_process_id = evt.value('(data[@name=""xml_report""]/value/deadlock/victim-list/victimProcess/@id)[1]', 'varchar(50)'),
    deadlock_graph_xml = CONVERT(nvarchar(max), evt.query('data[@name=""xml_report""]/value/deadlock'))
FROM
(
    SELECT
        pmd.ring_buffer
    FROM @PerformanceMonitor_Deadlock AS pmd
) AS rb
CROSS APPLY rb.ring_buffer.nodes('RingBufferTarget/event[@name=""database_xml_deadlock_report""]') AS q(evt)
WHERE evt.value('(@timestamp)[1]', 'datetime2') > @cutoff_time
OPTION(RECOMPILE);";

    /* On-prem / Azure MI / AWS RDS: read from ring_buffer (server-scoped session)
       Use .query() to get XML with structure intact, then CONVERT to nvarchar(max) */
    private const string ServerScopedQueryText = $@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE
    @PerformanceMonitor_Deadlock TABLE
(
    ring_buffer xml NOT NULL
);

INSERT
    @PerformanceMonitor_Deadlock
(
    ring_buffer
)
SELECT /* PerformanceMonitorLite */
    ring_xml = TRY_CAST(xet.target_data AS xml)
FROM sys.dm_xe_session_targets AS xet
JOIN sys.dm_xe_sessions AS xes
  ON xes.address = xet.event_session_address
WHERE xes.name = N'{XeSessionName}'
AND   xet.target_name = N'ring_buffer'
OPTION(RECOMPILE);

SELECT
    deadlock_time = evt.value('(@timestamp)[1]', 'datetime2'),
    victim_process_id = evt.value('(data[@name=""xml_report""]/value/deadlock/victim-list/victimProcess/@id)[1]', 'varchar(50)'),
    deadlock_graph_xml = CONVERT(nvarchar(max), evt.query('data[@name=""xml_report""]/value/deadlock'))
FROM
(
    SELECT
        pmd.ring_buffer
    FROM @PerformanceMonitor_Deadlock AS pmd
) AS rb
CROSS APPLY rb.ring_buffer.nodes('RingBufferTarget/event[@name=""xml_deadlock_report""]') AS q(evt)
WHERE evt.value('(@timestamp)[1]', 'datetime2') > @cutoff_time
OPTION(RECOMPILE);";

    public override string Name => "deadlocks";

    public override string TargetTable => "deadlocks";

    /// <summary>Lite schema names this table's prefix id "deadlock_id"; Darling mirrors it.</summary>
    public override string PrefixIdColumnName => "deadlock_id";

    /// <summary>
    /// Only events newer than the newest already-collected deadlock are fetched, so an event
    /// lingering in the ring buffer across cycles is never inserted twice.
    /// </summary>
    public override string? WatermarkColumn => "deadlock_time";

    public override CollectorQuery BuildQuery(CollectorContext context)
    {
        var text = context.Target.IsAzureSqlDb ? AzureQueryText : ServerScopedQueryText;

        /* Use the most recent timestamp from the host store as the cutoff, or fall back to a
           10-minute window on first run. */
        var cutoffTime = context.Watermark ?? context.CollectionTime.AddMinutes(-10);

        return new CollectorQuery(text, new List<CollectorParameter>
        {
            new("@cutoff_time", cutoffTime, CollectorParameterType.DateTime2),
        });
    }

    public override IReadOnlyList<CollectorColumn> PayloadColumns { get; } = new[]
    {
        new CollectorColumn("deadlock_time", CollectorColumnType.Timestamp),
        new CollectorColumn("victim_process_id", CollectorColumnType.Varchar),
        new CollectorColumn("victim_sql_text", CollectorColumnType.Varchar),
        new CollectorColumn("deadlock_graph_xml", CollectorColumnType.Varchar),
    };

    public override async ValueTask<List<Row>> ReadAsync(DbDataReader reader, CollectorContext context, CancellationToken cancellationToken)
    {
        /* Parse the graph XML here in the read (SQL) phase — ExtractVictimSqlText does
           XElement.Parse, which is expensive and was previously misattributed as storage time. */
        var rows = new List<Row>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var victimProcessId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var graphXml = reader.IsDBNull(2) ? null : reader.GetString(2);

            rows.Add(new Row
            {
                DeadlockTime = reader.IsDBNull(0) ? null : reader.GetDateTime(0),
                VictimProcessId = victimProcessId,
                VictimSqlText = ExtractVictimSqlText(graphXml, victimProcessId),
                GraphXml = graphXml,
            });
        }

        return rows;
    }

    public override void WritePayload(Row row, ICollectorRowWriter writer, CollectorContext context)
    {
        writer
            .Value(row.DeadlockTime)
            .Value(row.VictimProcessId)
            .Value(row.VictimSqlText)
            .Value(row.GraphXml);
    }

    /// <summary>
    /// Extracts victim SQL text from a deadlock graph XML fragment.
    /// </summary>
    public static string? ExtractVictimSqlText(string? graphXml, string? victimProcessId)
    {
        if (string.IsNullOrEmpty(graphXml))
        {
            return null;
        }

        try
        {
            var doc = XElement.Parse(graphXml);

            /* Find all process nodes in the deadlock graph */
            var processes = doc.Descendants("process").ToList();

            /* If we have a victim ID, find that specific process */
            if (!string.IsNullOrEmpty(victimProcessId))
            {
                var victim = processes.FirstOrDefault(p =>
                    string.Equals(p.Attribute("id")?.Value, victimProcessId, StringComparison.OrdinalIgnoreCase));

                if (victim != null)
                {
                    return victim.Element("inputbuf")?.Value?.Trim();
                }
            }

            /* Fallback: return the first process inputbuf */
            return processes.FirstOrDefault()?.Element("inputbuf")?.Value?.Trim();
        }
        catch
        {
            return null;
        }
    }
}

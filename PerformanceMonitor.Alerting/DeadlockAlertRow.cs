/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace PerformanceMonitor.Alerting;

/// <summary>
/// One deadlock event as the alert engine consumes it (Phase-5 slice B) — the superset of fields
/// the shared <see cref="AlertContextBuilders.BuildDeadlockContext"/> builder, the
/// <c>DeadlockIncidentGrouper</c>/<c>DeadlockObjectExtractor</c> projections, and the
/// excluded-database check (<see cref="AlertContextBuilders.IsDeadlockExcluded"/>) actually touch.
/// Lite's grid row (<c>DeadlockRow</c>) derives from this so its store reads flow into the shared
/// builders without any mapping copy; the Darling adapter materializes this type directly.
/// </summary>
public class DeadlockAlertRow
{
    public string VictimProcessId { get; set; } = "";
    public string VictimSqlText { get; set; } = "";
    public string DeadlockGraphXml { get; set; } = "";
    public bool HasDeadlockXml => !string.IsNullOrEmpty(DeadlockGraphXml);

    /// <summary>
    /// Parses the deadlock graph XML and returns a summary of all processes involved
    /// ("SPID 55 (victim) vs SPID 60"). Computed on access, exactly like the pre-extraction
    /// Lite property — see <see cref="DeadlockGraphSummary.Summarize"/>.
    /// </summary>
    public string ProcessSummary => DeadlockGraphSummary.Summarize(DeadlockGraphXml, VictimProcessId);
}

/// <summary>
/// The deadlock-graph process-summary parse — moved verbatim from Lite's
/// <c>DeadlockRow.ProcessSummary</c> getter (Phase-5 slice B) so the row shape is store-free and
/// the rendered "Processes" field cannot drift between hosts. Returns "" for empty or unparseable
/// XML (the builder omits the field then).
/// </summary>
public static class DeadlockGraphSummary
{
    public static string Summarize(string? deadlockGraphXml, string? victimProcessId)
    {
        if (string.IsNullOrEmpty(deadlockGraphXml))
        {
            return "";
        }

        try
        {
            var doc = XElement.Parse(deadlockGraphXml);
            var processes = doc.Descendants("process");
            var summaries = new List<string>();

            foreach (var proc in processes)
            {
                var id = proc.Attribute("id")?.Value ?? "?";
                var spid = proc.Attribute("spid")?.Value ?? "?";
                var isVictim = string.Equals(id, victimProcessId, StringComparison.OrdinalIgnoreCase);
                summaries.Add($"SPID {spid}{(isVictim ? " (victim)" : "")}");
            }

            return string.Join(" vs ", summaries);
        }
        catch
        {
            return "";
        }
    }
}

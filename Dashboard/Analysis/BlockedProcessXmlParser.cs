/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Xml.Linq;

namespace PerformanceMonitorDashboard.Analysis;

/// <summary>
/// Parses one <c>blocked_process_report_xml</c> document into a single
/// <see cref="BlockingPairRow"/> for the reconstructor. Returns <c>null</c> when the
/// document is malformed or missing the canonical blocked-process node.
///
/// <para>
/// The collector treats a null return as "skip this row, keep going" — high-volume,
/// no per-row log noise.
/// </para>
/// </summary>
internal static class BlockedProcessXmlParser
{
    /// <summary>
    /// Parses a single blocked-process-report XML fragment. The reconstructor's
    /// <see cref="BlockingChainReconstructor.MakeKey"/> normalizes the
    /// 1900-01-01 "no transaction" sentinel — no special handling needed here.
    /// </summary>
    /// <param name="xml">The raw <c>blocked_process_report_xml</c> value.</param>
    /// <param name="eventTime">The row's <c>event_time</c> — when the XE fired.</param>
    /// <param name="databaseName">The row's <c>database_name</c> (blocked-side fallback).</param>
    /// <param name="waitTimeMs">The row's <c>wait_time_ms</c> (blocked-side wait).</param>
    /// <param name="lockMode">The row's <c>lock_mode</c> (blocked-side lock mode).</param>
    /// <param name="lastTransactionStarted">The row's <c>last_transaction_started</c> (blocked-side tran start).</param>
    /// <param name="blockedSpidFromRow">The row's <c>spid</c> — for <c>activity='blocked'</c> rows this is the blocked SPID.</param>
    public static BlockingPairRow? Parse(
        string xml,
        DateTime eventTime,
        string databaseName,
        long waitTimeMs,
        string lockMode,
        DateTime? lastTransactionStarted,
        int blockedSpidFromRow)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        try
        {
            var doc = XElement.Parse(xml);
            var blockedProcess = doc.Element("blocked-process")?.Element("process");
            var blockingProcess = doc.Element("blocking-process")?.Element("process");

            if (blockedProcess == null)
                return null;

            // SPID and last_transaction_started for the BLOCKED side come from the row;
            // fall back to the XML attributes if the row was missing them.
            var blockedSpid = blockedSpidFromRow > 0
                ? blockedSpidFromRow
                : (int.TryParse(blockedProcess.Attribute("spid")?.Value, out var bs) ? bs : 0);
            var blockingSpid = int.TryParse(blockingProcess?.Attribute("spid")?.Value, out var ks)
                ? ks
                : 0;

            var blockedTran = lastTransactionStarted
                ?? (DateTime.TryParse(blockedProcess.Attribute("lasttranstarted")?.Value, out var blts) ? blts : (DateTime?)null);
            // Blocking-side tran start lives only in the XML — the row carries blocked-side info.
            var blockingTran = DateTime.TryParse(blockingProcess?.Attribute("lasttranstarted")?.Value, out var bklts)
                ? bklts
                : (DateTime?)null;

            return new BlockingPairRow
            {
                EventTime = eventTime,
                DatabaseName = !string.IsNullOrWhiteSpace(databaseName)
                    ? databaseName
                    : blockedProcess.Attribute("currentdbname")?.Value ?? string.Empty,
                BlockedSpid = blockedSpid,
                BlockedTranStarted = blockedTran,
                BlockingSpid = blockingSpid,
                BlockingTranStarted = blockingTran,
                WaitTimeMs = waitTimeMs > 0
                    ? waitTimeMs
                    : (long.TryParse(blockedProcess.Attribute("waittime")?.Value, out var wt) ? wt : 0),
                LockMode = !string.IsNullOrEmpty(lockMode)
                    ? lockMode
                    : blockedProcess.Attribute("lockMode")?.Value ?? string.Empty,
                // The row's status is the BLOCKED side's status — the reconstructor needs the
                // BLOCKING side's status to detect sleeping-apex chains. Read it from the XML.
                BlockingStatus = blockingProcess?.Attribute("status")?.Value ?? string.Empty,
                BlockedSqlText = blockedProcess.Element("inputbuf")?.Value?.Trim() ?? string.Empty,
                BlockingSqlText = blockingProcess?.Element("inputbuf")?.Value?.Trim() ?? string.Empty
            };
        }
        catch
        {
            return null;
        }
    }
}

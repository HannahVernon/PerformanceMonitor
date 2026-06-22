using System;
using System.Data.Common;
using PerformanceMonitor.Analysis;

namespace PerformanceMonitorLite.Analysis;

/// <summary>
/// Shared pieces of the blocked-process-report pair-row query used to reconstruct blocking chains, so
/// Lite's three consumers — the drill-down collector, the BLOCKING_CHAIN fact collector, and the viewer's
/// data-service fetch — agree on the apex.
///
/// <para><see cref="SpidFilter"/> is the behavioral fix: Lite maps a missing blocker to spid 0 (a phantom
/// root); without filtering it out, the fact/drill-down/viewer would each invent a SPID-0 apex. This brings
/// Lite in line with Dashboard's long-standing <c>blocking_spid IS NOT NULL</c>. The SELECT list (and thus
/// the SQL-text truncation) deliberately stays per-site — only the filter and the reader mapping are shared,
/// because the column count/order is identical across all three.</para>
/// </summary>
internal static class BlockingPairRowQuery
{
    /// <summary>Append to the WHERE clause of every pair-row query (covers NULL and the 0 sentinel).</summary>
    public const string SpidFilter = @"AND blocking_spid IS NOT NULL
AND blocking_spid <> 0";

    public static BlockingPairRow Read(DbDataReader reader) => new()
    {
        EventTime = reader.IsDBNull(0) ? default : reader.GetDateTime(0),
        DatabaseName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
        BlockedSpid = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2)),
        BlockedTranStarted = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
        BlockingSpid = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4)),
        BlockingTranStarted = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
        WaitTimeMs = reader.IsDBNull(6) ? 0L : Convert.ToInt64(reader.GetValue(6)),
        LockMode = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
        BlockingStatus = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
        BlockedSqlText = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
        BlockingSqlText = reader.IsDBNull(10) ? string.Empty : reader.GetString(10)
    };
}

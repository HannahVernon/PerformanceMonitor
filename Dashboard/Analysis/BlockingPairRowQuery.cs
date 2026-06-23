using System;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using PerformanceMonitor.Analysis;

namespace PerformanceMonitorDashboard.Analysis;

/// <summary>
/// The single source of the blocked-process-report pair-row query used to reconstruct blocking chains.
/// Shared by the three Dashboard consumers — the drill-down collector, the BLOCKING_CHAIN fact collector,
/// and the viewer's data-service fetch — so the apex-determining <c>blocking_spid IS NOT NULL</c> filter
/// (and the SELECT/ordinals) stay in lockstep. <c>activity = 'blocked'</c> picks the canonical per-event
/// side; <c>blocking_spid IS NOT NULL</c> drops rows whose source XML had an empty
/// <c>&lt;blocking-process&gt;&lt;process/&gt;&lt;/blocking-process&gt;</c> (system task / torn-down session),
/// which cannot contribute to a chain.
/// </summary>
internal static class BlockingPairRowQuery
{
    public const string Sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP (5000)
    event_time,
    database_name,
    spid,
    last_transaction_started,
    blocking_spid,
    blocking_last_tran_started,
    wait_time_ms,
    lock_mode,
    blocking_status,
    blocked_sql_text,
    blocking_sql_text,
    login_name,
    host_name,
    client_app
FROM collect.blocking_BlockedProcessReport
WHERE collection_time >= @collectionWindow
AND   event_time >= @startTime
AND   event_time <= @endTime
AND   activity = 'blocked'
AND   blocking_spid IS NOT NULL
ORDER BY collection_time DESC";

    /// <summary>
    /// Adds the three parameters. The collection-time floor is a generous bound (window start minus an
    /// hour) so rows whose event_time is inside the window but whose collection_time lags slightly are
    /// still caught.
    /// </summary>
    public static void AddParameters(SqlCommand cmd, DateTime start, DateTime end)
    {
        cmd.Parameters.Add(new SqlParameter("@collectionWindow", start.AddHours(-1)));
        cmd.Parameters.Add(new SqlParameter("@startTime", start));
        cmd.Parameters.Add(new SqlParameter("@endTime", end));
    }

    public static BlockingPairRow Read(DbDataReader reader) => new()
    {
        EventTime = reader.IsDBNull(0) ? default : reader.GetDateTime(0),
        DatabaseName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
        BlockedSpid = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2)),
        BlockedTranStarted = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3),
        BlockingSpid = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4)),
        BlockingTranStarted = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5),
        WaitTimeMs = reader.IsDBNull(6) ? 0L : Convert.ToInt64(reader.GetValue(6)),
        LockMode = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
        BlockingStatus = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
        BlockedSqlText = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
        BlockingSqlText = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
        // Blocked-side identity (the 'blocked' row's own session). The Dashboard table does NOT parse the
        // blocker's login/host/app from the XML (only blocking_status/blocking_last_tran_started/
        // blocking_sql_text), so the blocking-side identity stays empty here — the pure apex shows SQL + db
        // but no login/host/app. See BlockingChainViewerProjection / the PR notes for the schema follow-up.
        BlockedLoginName = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
        BlockedHostName = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
        BlockedClientApp = reader.IsDBNull(13) ? string.Empty : reader.GetString(13)
    };
}

/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Models;

namespace PerformanceMonitorLite.Services;

public partial class RemoteCollectorService
{
    /// <summary>
    /// Collects currently running SQL Agent jobs via the shared <see cref="RunningJobsCollector"/>
    /// definition (the p95 duration comparison and the no-collection_id prefix live there — the
    /// cross-SKU parity contract). The failed-jobs alert query below stays host-side until the
    /// Phase-5 alert-engine extraction.
    /// </summary>
    private Task<int> CollectRunningJobsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(RunningJobsCollector.Instance, server, cancellationToken);

    /// <summary>
    /// Live query against the monitored server's msdb for SQL Agent job runs that FAILED within the
    /// lookback window (the step_id = 0 outcome row, run_status = 0), also correlating the actual
    /// failing step (step_id > 0, run_status = 0) from that run via instance_id so step_id/step_name/
    /// message describe the failing step rather than the generic "(Job outcome)" row.
    /// Runs at alert-check time — failure outcomes are not part of the collected running_jobs
    /// snapshot. run_date/run_time integers are converted to a server-local datetime and filtered
    /// to the last N minutes. Reuses the collector's connection path (MFA serialization, throttle,
    /// retry) and degrades gracefully: any error (a login without msdb / SQLAgentReaderRole access,
    /// a transient failure, etc.) returns an empty list rather than failing the alert cycle. The
    /// caller skips Azure SQL DB (no Agent) and no-msdb logins before calling.
    /// </summary>
    public async Task<List<FailedJobInfo>> GetRecentlyFailedJobsAsync(
        ServerConnection server,
        int lookbackMinutes,
        CancellationToken cancellationToken = default)
    {
        const string query = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP (50)
    job_name = j.name,
    job_id = CONVERT(varchar(36), j.job_id),
    run_datetime =
        DATEADD
        (
            SECOND,
            (jh.run_time / 10000) * 3600 +
            ((jh.run_time / 100) % 100) * 60 +
            (jh.run_time % 100),
            CONVERT(datetime, CONVERT(varchar(8), jh.run_date))
        ),
    step_id = ISNULL(fs.step_id, jh.step_id),
    step_name = ISNULL(fs.step_name, jh.step_name),
    message = ISNULL(fs.message, jh.message)
FROM msdb.dbo.sysjobhistory AS jh
JOIN msdb.dbo.sysjobs AS j
  ON j.job_id = jh.job_id
OUTER APPLY
(
    /* The actual failing step (step_id > 0, run_status = 0) from THIS run, correlated by
       instance_id and bounded to be after this job's previous outcome row, so the alert names
       the failing step instead of the generic job-outcome row. Falls back to the outcome row
       for a rare job-level failure with no failed step row. */
    SELECT TOP (1)
        s.step_id,
        s.step_name,
        s.message
    FROM msdb.dbo.sysjobhistory AS s
    WHERE s.job_id = jh.job_id
    AND   s.step_id > 0
    AND   s.run_status = 0
    AND   s.instance_id < jh.instance_id
    AND   s.instance_id >
          (
              SELECT ISNULL(MAX(p.instance_id), 0)
              FROM msdb.dbo.sysjobhistory AS p
              WHERE p.job_id = jh.job_id
              AND   p.step_id = 0
              AND   p.instance_id < jh.instance_id
          )
    ORDER BY s.instance_id DESC
) AS fs
WHERE jh.step_id = 0
AND   jh.run_status = 0
AND   jh.run_date >= CONVERT(integer, CONVERT(varchar(8), DATEADD(MINUTE, -@lookback_minutes, GETDATE()), 112))
AND   DATEADD
      (
          SECOND,
          (jh.run_time / 10000) * 3600 +
          ((jh.run_time / 100) % 100) * 60 +
          (jh.run_time % 100),
          CONVERT(datetime, CONVERT(varchar(8), jh.run_date))
      ) >= DATEADD(MINUTE, -@lookback_minutes, GETDATE())
ORDER BY
    run_datetime DESC
OPTION(RECOMPILE);";

        var items = new List<FailedJobInfo>();

        try
        {
            using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);
            using var command = new SqlCommand(query, sqlConnection);
            command.CommandTimeout = CommandTimeoutSeconds;
            command.Parameters.AddWithValue("@lookback_minutes", lookbackMinutes);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new FailedJobInfo
                {
                    JobName = reader.GetString(0),
                    JobId = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    RunDateTime = reader.GetDateTime(2),
                    StepId = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3)),
                    StepName = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    Message = reader.IsDBNull(5) ? "" : reader.GetString(5)
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 229 or 297 or 300 or 916)
        {
            /* Login lacks msdb / SQLAgentReaderRole access — expected for read-only monitoring
               accounts; skip quietly so a permission gap doesn't fail the whole alert cycle. */
            _logger?.LogDebug("Skipping failed-job check for '{Server}': {Message}", server.DisplayName, ex.Message);
            return new List<FailedJobInfo>();
        }
        catch (Exception ex)
        {
            /* Unexpected error (timeout, transient, etc.) — surface at Warning so a genuine read
               failure can't masquerade as "no failed jobs", but still don't fault the alert cycle. */
            _logger?.LogWarning("Failed-job check for '{Server}' errored: {Message}", server.DisplayName, ex.Message);
            return new List<FailedJobInfo>();
        }

        return items;
    }
}

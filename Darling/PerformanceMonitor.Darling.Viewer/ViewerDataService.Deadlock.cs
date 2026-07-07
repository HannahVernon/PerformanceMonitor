/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Alerting;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The viewer's deadlock grid row (W1e). The alert-consumed members (victim process/SQL, graph XML)
/// live on the shared <see cref="DeadlockAlertRow"/> base — the same shape Lite's <c>DeadlockRow</c>
/// derives from — so the store reads flow into the shared surfaces without a mapping copy; only the
/// grid-fetch extras stay here. Mirror of Lite's <c>DeadlockRow</c>.
/// </summary>
public sealed class ViewerDeadlockRow : DeadlockAlertRow
{
    public DateTime CollectionTime { get; set; }
    public DateTime? DeadlockTime { get; set; }

    /// <summary>
    /// The deadlock's BEST-EFFORT victim plan (deadlocks.victim_query_plan_xml, #1368 / V7 — resolved at
    /// collection time from the victim's sql_handle, only when the host sets CapturePlanXml; often NULL,
    /// always NULL under Lite). Threaded onto every <see cref="DeadlockProcessDetail"/> parsed from this row.
    /// </summary>
    public string? VictimQueryPlanXml { get; set; }
}

/// <summary>
/// One parsed process inside a deadlock graph — the Deadlocks grid binds a
/// <c>List&lt;DeadlockProcessDetail&gt;</c> (one row per process, parsed from each
/// <see cref="ViewerDeadlockRow"/>'s graph XML). Copied verbatim from Lite's
/// <c>DeadlockProcessDetail</c> (LocalDataService.Blocking.cs): the sp_BlitzLock-style graph walk
/// (victim detection, owner/waiter modes, object names, proc-name resolution) is CPU-bound XML work, so
/// callers run <see cref="ParseFromRows"/> off the UI thread. The only deviation is the two *Local
/// display strings: Lite's per-server <c>ServerTimeHelper.FormatServerTime</c> becomes the viewer's one
/// machine-local <see cref="ViewerTimeHelper.ForDisplay"/> (the same swap the widened BPR row makes).
/// </summary>
public sealed class DeadlockProcessDetail
{
    public DateTime? DeadlockTime { get; set; }
    public bool IsVictim { get; set; }
    public string ProcessId { get; set; } = "";
    public int Spid { get; set; }
    public string DatabaseName { get; set; } = "";
    public string SqlText { get; set; } = "";
    public string WaitResource { get; set; } = "";
    public long WaitTime { get; set; }
    public string LockMode { get; set; } = "";
    public string IsolationLevel { get; set; } = "";
    public long LogUsed { get; set; }
    public int TransactionCount { get; set; }
    public string ClientApp { get; set; } = "";
    public string HostName { get; set; } = "";
    public string LoginName { get; set; } = "";
    public string Status { get; set; } = "";
    public string DeadlockGraphXml { get; set; } = "";
    public bool HasDeadlockXml => !string.IsNullOrEmpty(DeadlockGraphXml);

    /// <summary>
    /// The BEST-EFFORT victim plan for this deadlock (deadlocks.victim_query_plan_xml, #1368 / V7) — one
    /// plan per deadlock, copied onto every process row parsed from the same graph. The "View Victim Plan"
    /// context item is gated per row on <see cref="CanViewVictimPlan"/> so a plan-less deadlock (NULL, the
    /// common case, and always so under Lite) shows it disabled rather than shown-and-failed.
    /// </summary>
    public string? VictimQueryPlanXml { get; set; }
    public bool HasVictimQueryPlan => !string.IsNullOrEmpty(VictimQueryPlanXml);

    /// <summary>
    /// Whether "View Victim Plan" should be active for THIS row. The victim plan is a single per-deadlock plan
    /// copied onto every process row, so gating only on <see cref="HasVictimQueryPlan"/> lit it up on the
    /// deadlocker rows too (where it reads as "this process's plan", which it isn't). Requires the row to be the
    /// victim AND to carry a plan, so the item is active only on the victim row.
    /// </summary>
    public bool CanViewVictimPlan => IsVictim && HasVictimQueryPlan;

    /* New fields from sp_BlitzLock analysis */
    public string DeadlockType { get; set; } = "";
    public string ObjectNames { get; set; } = "";
    public string ProcName { get; set; } = "";
    public string OwnerMode { get; set; } = "";
    public string WaiterMode { get; set; } = "";
    public string TransactionName { get; set; } = "";
    public int Priority { get; set; }
    public DateTime? LastTranStarted { get; set; }
    public DateTime? LastBatchStarted { get; set; }
    public DateTime? LastBatchCompleted { get; set; }

    public string DeadlockTimeLocal
        => DeadlockTime is { } t ? ViewerTimeHelper.ForDisplay(t).ToString("yyyy-MM-dd HH:mm:ss") : "";
    public string VictimDisplay => IsVictim ? "Victim" : "";
    public string WaitTimeFormatted => WaitTime > 0 ? $"{WaitTime:N0} ms" : "";
    public string LastTranStartedLocal
        => LastTranStarted is { } t ? ViewerTimeHelper.ForDisplay(t).ToString("yyyy-MM-dd HH:mm:ss") : "";

    /// <summary>
    /// Parses a list of deadlock rows into per-process detail rows.
    /// </summary>
    public static List<DeadlockProcessDetail> ParseFromRows(List<ViewerDeadlockRow> rows)
    {
        var details = new List<DeadlockProcessDetail>();
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.DeadlockGraphXml))
                continue;

            try
            {
                var doc = System.Xml.Linq.XElement.Parse(row.DeadlockGraphXml);

                /* Detect parallel deadlock */
                var resourceList = doc.Descendants("resource-list").FirstOrDefault();
                var isParallel = resourceList != null &&
                    (resourceList.Elements("exchangeEvent").Any() || resourceList.Elements("SyncPoint").Any());
                var deadlockType = isParallel ? "Parallel" : "Regular";

                /* Get victim IDs from victim-list */
                var victimIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var vp in doc.Descendants("victimProcess"))
                {
                    var id = vp.Attribute("id")?.Value;
                    if (id != null) victimIds.Add(id);
                }

                /* Parse lock resources to build per-process owner/waiter modes and object names */
                var processOwnerModes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var processWaiterModes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var processObjectNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                if (resourceList != null)
                {
                    var lockTypes = new[] { "objectlock", "pagelock", "keylock", "ridlock", "rowgrouplock" };
                    foreach (var lockType in lockTypes)
                    {
                        foreach (var lockNode in resourceList.Elements(lockType))
                        {
                            var objectName = lockNode.Attribute("objectname")?.Value ?? "";

                            /* Parse owners */
                            foreach (var owner in lockNode.Descendants("owner"))
                            {
                                var ownerId = owner.Attribute("id")?.Value ?? "";
                                var ownerMode = owner.Attribute("mode")?.Value ?? "";
                                if (!string.IsNullOrEmpty(ownerId) && !string.IsNullOrEmpty(ownerMode))
                                {
                                    if (!processOwnerModes.ContainsKey(ownerId))
                                        processOwnerModes[ownerId] = new HashSet<string>();
                                    processOwnerModes[ownerId].Add(ownerMode);
                                }
                                if (!string.IsNullOrEmpty(ownerId) && !string.IsNullOrEmpty(objectName))
                                {
                                    if (!processObjectNames.ContainsKey(ownerId))
                                        processObjectNames[ownerId] = new HashSet<string>();
                                    processObjectNames[ownerId].Add(objectName);
                                }
                            }

                            /* Parse waiters */
                            foreach (var waiter in lockNode.Descendants("waiter"))
                            {
                                var waiterId = waiter.Attribute("id")?.Value ?? "";
                                var waiterMode = waiter.Attribute("mode")?.Value ?? "";
                                if (!string.IsNullOrEmpty(waiterId) && !string.IsNullOrEmpty(waiterMode))
                                {
                                    if (!processWaiterModes.ContainsKey(waiterId))
                                        processWaiterModes[waiterId] = new HashSet<string>();
                                    processWaiterModes[waiterId].Add(waiterMode);
                                }
                                if (!string.IsNullOrEmpty(waiterId) && !string.IsNullOrEmpty(objectName))
                                {
                                    if (!processObjectNames.ContainsKey(waiterId))
                                        processObjectNames[waiterId] = new HashSet<string>();
                                    processObjectNames[waiterId].Add(objectName);
                                }
                            }
                        }
                    }
                }

                /* Parse each process */
                foreach (var proc in doc.Descendants("process"))
                {
                    var id = proc.Attribute("id")?.Value ?? "";

                    /* Get proc name from execution stack */
                    var procName = "";
                    foreach (var frame in proc.Descendants("frame"))
                    {
                        var frameProcName = frame.Attribute("procname")?.Value ?? "";
                        if (!string.IsNullOrEmpty(frameProcName) && frameProcName != "adhoc" && frameProcName != "unknown")
                        {
                            procName = frameProcName;
                            break;
                        }
                    }

                    details.Add(new DeadlockProcessDetail
                    {
                        DeadlockTime = row.DeadlockTime,
                        ProcessId = id,
                        IsVictim = victimIds.Contains(id),
                        Spid = int.TryParse(proc.Attribute("spid")?.Value, out var spid) ? spid : 0,
                        DatabaseName = proc.Attribute("currentdbname")?.Value ?? "",
                        SqlText = proc.Element("inputbuf")?.Value?.Trim() ?? "",
                        WaitResource = proc.Attribute("waitresource")?.Value ?? "",
                        WaitTime = long.TryParse(proc.Attribute("waittime")?.Value, out var wt) ? wt : 0,
                        LockMode = proc.Attribute("lockMode")?.Value ?? "",
                        IsolationLevel = proc.Attribute("isolationlevel")?.Value ?? "",
                        LogUsed = long.TryParse(proc.Attribute("logused")?.Value, out var lu) ? lu : 0,
                        TransactionCount = int.TryParse(proc.Attribute("trancount")?.Value, out var tc) ? tc : 0,
                        ClientApp = proc.Attribute("clientapp")?.Value ?? "",
                        HostName = proc.Attribute("hostname")?.Value ?? "",
                        LoginName = proc.Attribute("loginname")?.Value ?? "",
                        Status = proc.Attribute("status")?.Value ?? "",
                        DeadlockGraphXml = row.DeadlockGraphXml,
                        VictimQueryPlanXml = row.VictimQueryPlanXml,
                        DeadlockType = deadlockType,
                        ProcName = procName,
                        TransactionName = proc.Attribute("transactionname")?.Value ?? "",
                        Priority = int.TryParse(proc.Attribute("priority")?.Value, out var pri) ? pri : 0,
                        LastTranStarted = DateTime.TryParse(proc.Attribute("lasttranstarted")?.Value, out var lts) ? lts : null,
                        LastBatchStarted = DateTime.TryParse(proc.Attribute("lastbatchstarted")?.Value, out var lbs) ? lbs : null,
                        LastBatchCompleted = DateTime.TryParse(proc.Attribute("lastbatchcompleted")?.Value, out var lbc) ? lbc : null,
                        OwnerMode = processOwnerModes.TryGetValue(id, out var om) ? string.Join(", ", om) : "",
                        WaiterMode = processWaiterModes.TryGetValue(id, out var wm) ? string.Join(", ", wm) : "",
                        ObjectNames = processObjectNames.TryGetValue(id, out var on) ? string.Join(", ", on) : ""
                    });
                }
            }
            catch
            {
                /* If XML parsing fails, add a single fallback row */
                details.Add(new DeadlockProcessDetail
                {
                    DeadlockTime = row.DeadlockTime,
                    SqlText = row.VictimSqlText,
                    IsVictim = true,
                    DeadlockGraphXml = row.DeadlockGraphXml,
                    VictimQueryPlanXml = row.VictimQueryPlanXml
                });
            }
        }
        return details;
    }
}

public sealed partial class ViewerDataService
{
    /// <summary>
    /// Recent deadlock events for one server — Lite's <c>GetRecentDeadlocksAsync</c> ported to Postgres.
    /// Windows on the collection prefix, orders newest deadlock first, caps at 50 (Lite's cap). Carries the
    /// graph XML so <see cref="DeadlockProcessDetail.ParseFromRows"/> and the deadlock-graph viewer work
    /// without a second fetch, and the BEST-EFFORT victim plan (#1368 / V7) the grid's "View Victim Plan"
    /// item opens.
    ///
    /// <para>Reads the BASE <c>deadlocks</c> table, not <c>v_deadlocks</c> (deliberate divergence from the
    /// other deadlock reads' v_-view twinning): the V7 <c>victim_query_plan_xml</c> column is projected
    /// reliably only by the base table. Postgres pins <c>SELECT *</c> in a view to the columns present at
    /// view-creation (V4, before V7), and V8 only <c>ALTER VIEW … SET SCHEMA</c>s it (never re-creates it),
    /// so an upgraded store's view would not expose the plan column and this read would fail. The column is
    /// Darling-only (Lite's DuckDB view has none), so this read was never twinnable with Lite once it
    /// carries the plan.</para>
    /// $1 server_id, $2 window start, $3 window end (naive UTC).
    /// </summary>
    public const string RecentDeadlocksSql = """
        SELECT
            collection_time,
            deadlock_time,
            victim_process_id,
            victim_sql_text,
            deadlock_graph_xml,
            victim_query_plan_xml
        FROM deadlocks
        WHERE server_id = $1
        AND   collection_time >= $2
        AND   collection_time <= $3
        ORDER BY deadlock_time DESC
        LIMIT 50
        """;

    /// <summary>The raw deadlock rows for the window; the grid parses them into per-process detail rows.</summary>
    public async Task<List<ViewerDeadlockRow>> GetRecentDeadlocksAsync(
        int serverId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        var rows = new List<ViewerDeadlockRow>();

        await using var command = _dataSource.CreateCommand(RecentDeadlocksSql);
        AddBlockingParameters(command, serverId, startUtc, endUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ViewerDeadlockRow
            {
                CollectionTime = reader.GetDateTime(0),
                DeadlockTime = reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                VictimProcessId = reader.IsDBNull(2) ? "" : reader.GetString(2),
                VictimSqlText = reader.IsDBNull(3) ? "" : reader.GetString(3),
                DeadlockGraphXml = reader.IsDBNull(4) ? "" : reader.GetString(4),
                VictimQueryPlanXml = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }

        return rows;
    }
}

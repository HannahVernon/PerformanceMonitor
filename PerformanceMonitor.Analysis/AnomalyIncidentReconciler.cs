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

namespace PerformanceMonitor.Analysis;

/// <summary>
/// Reconciles each surviving <c>ANOMALY_*</c> story's <see cref="AnalysisStory.IncidentId"/> onto the
/// REGULAR (non-anomaly) finding that describes the SAME symptom in the SAME run, so the anomaly folds
/// into that incident instead of rendering as its own card (viewer) and its own e-mail
/// (<c>AnalysisNotificationService</c>). This runs AFTER
/// <see cref="InferenceEngine.ClusterIntoIncidents"/> + <see cref="IncidentId.StampClusters"/> and
/// BEFORE mute-filtering, at the identical wiring site in all three analysis services.
///
/// <para><b>Fold, don't suppress, don't add graph edges</b> (Round-2 vetted design). Suppression is
/// rejected — the anomaly carries the vs-baseline delta the regular fact does not, catches
/// sub-threshold regressions, and covers no-regular-path waits (PAGELATCH_*/ASYNC_NETWORK_IO/…).
/// Broad <see cref="RelationshipGraph"/> edges are rejected — union-find over the existing THREADPOOL
/// bridge over-merges unrelated incidents. Reconciling the id keeps every finding while collapsing the
/// duplicate presentation: nothing is dropped, only the incident tag is rewritten.</para>
///
/// <para><b>Symptom-family map</b> mirrors the collector's grouping
/// (<see cref="FactCollectorHelpers.WaitFamilyKey"/> / <see cref="FactCollectorHelpers.IsGeneralLockWait"/>)
/// exactly. <c>ANOMALY_WAIT_PROFILE</c> resolves to the family of its DOMINANT <c>contrib_&lt;TYPE&gt;</c>
/// driver (Stage A made the whole wait profile one fact, so this is a clean single lookup). Anomalies
/// with no single regular counterpart (batch / session / query-duration, and the db-scoped
/// <c>ANOMALY_OBJECT_*</c> pair) are unmapped and always stay solo.</para>
///
/// <para><b>Database-aware.</b> The fold only happens when a regular parent with the matching family
/// key exists in the SAME database (server-scoped findings match on the empty-string database). A
/// db-scoped <c>ANOMALY_OBJECT_*</c> can therefore never fold into a finding in another database, and
/// two different databases never share a folded incident.</para>
///
/// <para>Pure and stateless — mutates only <see cref="AnalysisStory.IncidentId"/> on the passed
/// stories, so it is directly unit-testable without any collector or store.</para>
/// </summary>
public static class AnomalyIncidentReconciler
{
    /// <summary>
    /// Maps a server-scoped <c>ANOMALY_*</c> root key to the REGULAR fact key (symptom family) that
    /// describes the same event. <c>ANOMALY_WAIT_PROFILE</c> is resolved separately from its dominant
    /// contributor (<see cref="ResolveFamily"/>). Anomaly keys absent from this map — batch-request,
    /// session, query-duration spikes, and the db-scoped <c>ANOMALY_OBJECT_GROWTH</c> /
    /// <c>ANOMALY_OBJECT_CONTENTION</c> — have no single regular counterpart and always stay solo.
    /// </summary>
    private static readonly Dictionary<string, string> AnomalyToFamily = new(StringComparer.Ordinal)
    {
        ["ANOMALY_CPU_SPIKE"] = "CPU_SQL_PERCENT",
        ["ANOMALY_READ_LATENCY"] = "IO_READ_LATENCY_MS",
        ["ANOMALY_WRITE_LATENCY"] = "IO_WRITE_LATENCY_MS",
        ["ANOMALY_MEMORY_PRESSURE"] = "RESOURCE_SEMAPHORE",
        ["ANOMALY_BLOCKING_SPIKE"] = "BLOCKING_EVENTS",
        ["ANOMALY_DEADLOCK_SPIKE"] = "DEADLOCKS",
    };

    /// <summary>
    /// Rewrites the incident id of each <c>ANOMALY_*</c> story that has a same-run, same-database
    /// regular parent onto that parent's incident id. No-op when there are fewer than two
    /// non-absolution stories or no regular stories to fold into.
    /// </summary>
    public static void Reconcile(IReadOnlyList<AnalysisStory> stories)
    {
        if (stories is null)
            return;

        var incidentStories = stories.Where(s => s is not null && !s.IsAbsolution).ToList();
        if (incidentStories.Count < 2)
            return;

        // Index the REGULAR (non-anomaly) stories by (family key, database). The database is
        // normalized (null/empty -> "") so a server-scoped anomaly matches its server-scoped regular
        // counterpart on "", while a db-scoped anomaly only ever matches a regular finding in its OWN
        // database. On the (shouldn't-happen) chance two regular stories share a (key, db), keep the
        // highest-severity one — its incident is the primary the anomaly should join.
        var regularByKey = new Dictionary<(string Key, string Db), AnalysisStory>();
        foreach (var s in incidentStories)
        {
            if (IsAnomaly(s.RootFactKey))
                continue;

            var mapKey = (s.RootFactKey, s.DatabaseName ?? string.Empty);
            if (!regularByKey.TryGetValue(mapKey, out var existing) || s.Severity > existing.Severity)
                regularByKey[mapKey] = s;
        }

        if (regularByKey.Count == 0)
            return;

        foreach (var anomaly in incidentStories)
        {
            if (!IsAnomaly(anomaly.RootFactKey))
                continue;

            var family = ResolveFamily(anomaly);
            if (family is null)
                continue; // unmapped anomaly (batch/session/query-duration/object) -> stays solo

            var lookup = (family, anomaly.DatabaseName ?? string.Empty);
            // Only fold when a real parent exists in the SAME database and it carries an id. Never
            // overwrite a solo anomaly's id with an empty one (StampClusters always assigns a
            // non-empty id, so this guard is belt-and-suspenders).
            if (regularByKey.TryGetValue(lookup, out var parent) && !string.IsNullOrEmpty(parent.IncidentId))
                anomaly.IncidentId = parent.IncidentId;
        }
    }

    private static bool IsAnomaly(string? key) =>
        key is not null && key.StartsWith("ANOMALY_", StringComparison.Ordinal);

    /// <summary>
    /// The regular-finding family key an anomaly story should fold into, or null when the anomaly has
    /// no single regular counterpart (it then stays solo). <c>ANOMALY_WAIT_PROFILE</c> resolves to the
    /// family of its dominant <c>contrib_&lt;TYPE&gt;</c> driver.
    /// </summary>
    private static string? ResolveFamily(AnalysisStory anomaly)
    {
        if (string.Equals(anomaly.RootFactKey, "ANOMALY_WAIT_PROFILE", StringComparison.Ordinal))
            return DominantWaitFamily(anomaly.RootFactMetadata);

        return AnomalyToFamily.TryGetValue(anomaly.RootFactKey, out var family) ? family : null;
    }

    /// <summary>
    /// The wait FAMILY of the <c>ANOMALY_WAIT_PROFILE</c>'s dominant (largest-ms) <c>contrib_&lt;TYPE&gt;</c>
    /// metadata entry, mapped through <see cref="FactCollectorHelpers.WaitFamilyKey"/> so it matches the
    /// grouped regular wait fact (CX* → CXPACKET, general lock modes → LCK, everything else → itself).
    /// Ties are broken by ordinal type name so the result is deterministic across dictionary orderings.
    /// Null when there is no <c>contrib_</c> metadata to resolve.
    /// </summary>
    private static string? DominantWaitFamily(Dictionary<string, double>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        const string prefix = "contrib_";
        string? dominantType = null;
        var dominantValue = double.NegativeInfinity;

        foreach (var kv in metadata)
        {
            if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var type = kv.Key.Substring(prefix.Length);
            if (dominantType is null
                || kv.Value > dominantValue
                || (kv.Value == dominantValue && string.CompareOrdinal(type, dominantType) < 0))
            {
                dominantValue = kv.Value;
                dominantType = type;
            }
        }

        return dominantType is null ? null : FactCollectorHelpers.WaitFamilyKey(dominantType);
    }
}

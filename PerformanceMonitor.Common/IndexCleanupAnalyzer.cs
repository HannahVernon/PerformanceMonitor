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

namespace PerformanceMonitor.Common
{
    /// <summary>
    /// Monitor-side C# reproduction of Erik's <c>sp_IndexCleanup</c> analysis, driven entirely by the
    /// captured Stage-1 <c>index_object_stats</c> snapshot (no live proc, no database). Given the per-index
    /// rows for one or more databases it emits DROP/MERGE/COMPRESS recommendations, the reconstructed T-SQL,
    /// and a reclaimable-space roll-up — the same analysis a live <c>sp_IndexCleanup</c> would, for servers
    /// where the proc is not (or cannot be) installed.
    ///
    /// <para>Faithful to <c>sp_IndexCleanup</c> v2.7 (20260701). Because Stage 1 captured
    /// <c>key_columns</c>/<c>included_columns</c> in the proc's EXACT delimited <c>QUOTENAME</c> form, the
    /// string-comparison dedupe ports directly. Reproduced rules: <b>Unused Index</b> (Rule 1, with the
    /// &lt;14-day-uptime caveat and &lt;=7-day auto-dedupe), <b>Exact Duplicate</b> (Rule 2),
    /// <b>Reverse Duplicate</b> and <b>Equal Except For Filter</b> (the two exact-duplicate sub-classes the
    /// proc documents at its <c>#index_analysis</c> definition but folds into Rule 2 — implemented here as
    /// distinct labels, both derivable from the captured direction-bearing key strings), <b>Key Duplicate</b>
    /// (Rule 5), and <b>Key Subset</b>/<b>Key Superset</b> (Rules 3/4/6, with subset-chain flattening and
    /// missing-include carry). Priority scoring, <c>is_eligible_for_dedupe</c>, and the
    /// PK/unique-constraint/FK-reference disable protections match the proc's guards. PAGE-compression
    /// candidacy and the 0.20–0.60 general savings band reproduce the proc's <c>#compression_eligibility</c>
    /// / reporting math.</para>
    ///
    /// <para><b>Deliberately out of scope</b> (see <see cref="IndexCleanupAnalysisResult.Notes"/>): the proc's
    /// <c>Unique Constraint Replacement</c> (MAKE UNIQUE) and <c>Same Keys Different Order</c> (REVIEW) rules,
    /// whose actions fall outside this stage's DISABLE / MERGE INCLUDES / KEEP set. Both remain reproducible
    /// from the captured data. Script details that need metadata Stage 1 did not capture (the trailing
    /// <c>ON &lt;filegroup/partition_scheme&gt;</c> placement; sparse/legacy-LOB compression exclusions) are
    /// surfaced, never guessed.</para>
    /// </summary>
    public static class IndexCleanupAnalyzer
    {
        private const string Clustered = "CLUSTERED";
        private const string NonClustered = "NONCLUSTERED";
        private const string CompressionNone = "NONE";

        /// <summary>
        /// Runs the full analysis over the captured per-index rows. Rows are grouped internally by
        /// (database, object) scope, so a single call may span many databases. Never throws on data content;
        /// malformed/heap/columnstore/disabled rows are simply excluded from analysis.
        /// </summary>
        public static IndexCleanupAnalysisResult Analyze(
            IEnumerable<IndexCleanupIndexInput> indexes,
            IndexCleanupOptions? options = null)
        {
            options ??= new IndexCleanupOptions();

            var analyzable = (indexes ?? Array.Empty<IndexCleanupIndexInput>())
                .Where(IsAnalyzable)
                .Select(src => new WorkIndex(src))
                .ToList();

            // Uptime → the unused-index caveat (< 14 days) and sp_IndexCleanup's auto-dedupe (<= 7 days).
            double? uptimeDays = ResolveUptimeDays(analyzable, options);
            bool uptimeWarning = uptimeDays.HasValue && uptimeDays.Value < 14;
            bool dedupeOnly = options.DedupeOnly || (uptimeDays.HasValue && uptimeDays.Value <= 7);

            var recommendations = new List<IndexCleanupRecommendation>();
            var databaseRollups = new List<IndexCleanupRollup>();

            // Rules operate per (database_id, object_id) scope; rollups aggregate per database.
            foreach (var dbGroup in analyzable
                .GroupBy(w => w.Src.DatabaseId)
                .OrderBy(g => g.First().Src.DatabaseName, StringComparer.Ordinal))
            {
                var perDatabaseAnalyzed = new List<WorkIndex>();

                foreach (var scope in dbGroup
                    .GroupBy(w => w.Src.ObjectId))
                {
                    var scopeIndexes = scope.ToList();

                    // Object-level gates (mirror sp_IndexCleanup's #filtered_objects @min_rows/@min_reads/@min_writes).
                    if (!PassesObjectFilters(scopeIndexes, options))
                    {
                        continue;
                    }

                    ApplyRules(scopeIndexes, options, dedupeOnly, uptimeWarning);
                    perDatabaseAnalyzed.AddRange(scopeIndexes);
                }

                // Compression candidacy is a per-index, data-driven eligibility (independent of the dedupe action).
                foreach (var w in perDatabaseAnalyzed)
                {
                    w.CanCompress =
                        options.ServerSupportsCompression
                        && w.Src.IndexId > 0
                        && string.Equals(w.Src.DataCompressionDesc, CompressionNone, StringComparison.OrdinalIgnoreCase)
                        && w.SizeGb >= options.MinSizeGb;
                }

                recommendations.AddRange(BuildRecommendations(perDatabaseAnalyzed, options));
                databaseRollups.Add(BuildRollup(dbGroup.First().Src.DatabaseName, perDatabaseAnalyzed, options));
            }

            var ordered = recommendations
                .OrderBy(r => r.DatabaseName, StringComparer.Ordinal)
                .ThenBy(r => r.SchemaName, StringComparer.Ordinal)
                .ThenBy(r => r.TableName, StringComparer.Ordinal)
                .ThenBy(r => ResultKindSort(r.ResultKind))
                .ThenBy(r => r.IndexName, StringComparer.Ordinal)
                .ToList();

            return new IndexCleanupAnalysisResult
            {
                Recommendations = ordered,
                DatabaseRollups = databaseRollups,
                OverallRollup = BuildOverallRollup(databaseRollups),
                UptimeWarning = uptimeWarning,
                DedupeOnlyApplied = dedupeOnly,
                Notes = BuildNotes(ordered),
            };
        }

        // ────────────────────────────────────────────────────────────────────────────────────────────
        //  Rule application (per object scope)
        // ────────────────────────────────────────────────────────────────────────────────────────────

        private static void ApplyRules(List<WorkIndex> scope, IndexCleanupOptions options, bool dedupeOnly, bool uptimeWarning)
        {
            if (!dedupeOnly)
            {
                ApplyUnused(scope, uptimeWarning);
            }

            ApplyExactDuplicate(scope);
            ApplyReverseDuplicate(scope);
            ApplyEqualExceptForFilter(scope);
            ApplyKeySubset(scope);
            ResolveSubsetChains(scope);
            ApplyKeySuperset(scope);
            ApplyKeyDuplicate(scope);
        }

        /// <summary>
        /// Rule 1: an eligible (nonclustered, non-unique, non-PK/UC/FK-reference) index with no reads at all
        /// is unused → DISABLE. Skipped entirely under dedupe-only. The consolidation label carries the
        /// &lt;14-day-uptime caveat when applicable.
        /// </summary>
        private static void ApplyUnused(List<WorkIndex> scope, bool uptimeWarning)
        {
            foreach (var w in scope)
            {
                if (w.Processed || !w.IsEligibleForDedupe)
                {
                    continue;
                }

                if (w.Src.UserSeeks == 0 && w.Src.UserScans == 0 && w.Src.UserLookups == 0
                    && !w.Src.IsPrimaryKey && !w.Src.IsUniqueConstraint && !w.Src.IsUnique
                    && !w.Src.IsForeignKeyReference   // protection (redundant with is_unique, kept per spec item 3)
                    && w.Src.IndexId != 1)
                {
                    w.ConsolidationRule = uptimeWarning
                        ? IndexCleanupRules.UnusedIndexUptimeWarning
                        : IndexCleanupRules.UnusedIndex;
                    w.Action = IndexCleanupAction.Disable;
                    w.Processed = true;
                }
            }
        }

        /// <summary>Rule 2: identical key string (with direction) + identical includes + identical filter.</summary>
        private static void ApplyExactDuplicate(List<WorkIndex> scope)
        {
            foreach (var group in DedupeCandidates(scope)
                .GroupBy(w => (w.KeyColumns, w.IncludedColumns, w.Filter), KeyIncludeFilterComparer.Instance)
                .Where(g => g.Count() > 1))
            {
                LabelDuplicateGroup(group.ToList(), IndexCleanupRules.ExactDuplicate);
            }
        }

        /// <summary>
        /// Reverse Duplicate: same key columns in the same order but every sort direction inverted (a
        /// backward scan of one serves the other), same includes and filter. Pairwise, since only the FULL
        /// direction inverse — not a partial direction difference — is equivalent.
        /// </summary>
        private static void ApplyReverseDuplicate(List<WorkIndex> scope)
        {
            var candidates = DedupeCandidates(scope)
                .OrderBy(w => w.Name, StringComparer.Ordinal)
                .ToList();

            for (int i = 0; i < candidates.Count; i++)
            {
                var a = candidates[i];
                if (a.Processed)
                {
                    continue;
                }

                for (int j = i + 1; j < candidates.Count; j++)
                {
                    var b = candidates[j];
                    if (b.Processed)
                    {
                        continue;
                    }

                    if (IsReverseDuplicate(a, b))
                    {
                        LabelDuplicateGroup(new List<WorkIndex> { a, b }, IndexCleanupRules.ReverseDuplicate);
                        break; // a is now processed
                    }
                }
            }
        }

        /// <summary>Equal Except For Filter: identical keys (with direction) and includes, different filter.</summary>
        private static void ApplyEqualExceptForFilter(List<WorkIndex> scope)
        {
            foreach (var group in DedupeCandidates(scope)
                .GroupBy(w => (w.KeyColumns, w.IncludedColumns), KeyIncludeComparer.Instance)
                .Where(g => g.Count() > 1))
            {
                // After Exact, same keys+includes among the unprocessed necessarily differ only by filter.
                LabelDuplicateGroup(group.ToList(), IndexCleanupRules.EqualExceptForFilter);
            }
        }

        /// <summary>
        /// Rule 3: a narrower non-unique index whose key string is a leading prefix (at a ", " boundary) of a
        /// wider index's key, with a matching filter, is disabled in favor of the closest (shortest-key)
        /// superset. Direction consistency on the shared prefix is enforced by the string prefix itself.
        /// </summary>
        private static void ApplyKeySubset(List<WorkIndex> scope)
        {
            foreach (var narrow in DedupeCandidates(scope)
                .Where(w => !w.Src.IsUnique)   // never disable a unique narrower via supersession
                .OrderBy(w => w.Name, StringComparer.Ordinal))
            {
                if (narrow.Processed)
                {
                    continue;
                }

                var prefix = narrow.KeyColumns + ", ";
                var target = scope
                    .Where(w => !ReferenceEquals(w, narrow)
                        && !w.Processed
                        && w.IsEligibleForDedupe
                        && string.Equals(w.Filter, narrow.Filter, StringComparison.Ordinal)
                        && w.KeyColumns.StartsWith(prefix, StringComparison.Ordinal))
                    .OrderBy(w => w.KeyColumns.Length)          // closest superset = fewest extra key columns
                    .ThenBy(w => w.Name, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (target != null)
                {
                    narrow.ConsolidationRule = IndexCleanupRules.KeySubset;
                    narrow.Action = IndexCleanupAction.Disable;
                    narrow.TargetIndexName = target.Name;
                    narrow.Processed = true;
                    // The superset is labelled in ApplyKeySuperset (it may absorb several subsets).
                }
            }
        }

        /// <summary>Flattens subset chains (A → B → C becomes A → C) so include-merge reaches the final superset.</summary>
        private static void ResolveSubsetChains(List<WorkIndex> scope)
        {
            var byName = scope.Where(w => w.Name.Length > 0)
                .GroupBy(w => w.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var w in scope)
                {
                    if (w.ConsolidationRule == IndexCleanupRules.KeySubset
                        && w.Action == IndexCleanupAction.Disable
                        && w.TargetIndexName != null
                        && byName.TryGetValue(w.TargetIndexName, out var mid)
                        && mid.ConsolidationRule == IndexCleanupRules.KeySubset
                        && mid.Action == IndexCleanupAction.Disable
                        && mid.TargetIndexName != null
                        && !string.Equals(mid.TargetIndexName, w.TargetIndexName, StringComparison.Ordinal))
                    {
                        w.TargetIndexName = mid.TargetIndexName;
                        changed = true;
                    }
                }
            }
        }

        /// <summary>
        /// Rule 4/6: a wider index that one or more subsets defer to becomes a Key Superset — kept but rebuilt
        /// with the subsets' include columns merged in (excluding any already in its key). Carries the
        /// "Supersedes …" list and the newly-merged (missing) include columns.
        /// </summary>
        private static void ApplyKeySuperset(List<WorkIndex> scope)
        {
            foreach (var superset in scope)
            {
                if (superset.Processed)
                {
                    continue;
                }

                var subsets = scope
                    .Where(s => s.ConsolidationRule == IndexCleanupRules.KeySubset
                        && s.Action == IndexCleanupAction.Disable
                        && string.Equals(s.TargetIndexName, superset.Name, StringComparison.Ordinal))
                    .OrderBy(s => s.Name, StringComparer.Ordinal)
                    .ToList();

                if (subsets.Count == 0)
                {
                    continue;
                }

                var keyNames = new HashSet<string>(superset.KeyNames, StringComparer.Ordinal);
                var ownIncludes = new HashSet<string>(superset.IncludeTokens, StringComparer.Ordinal);

                // Merge = superset's own includes + every subset's includes, minus anything already a key column.
                var merged = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var token in superset.IncludeTokens.Concat(subsets.SelectMany(s => s.IncludeTokens)))
                {
                    if (keyNames.Contains(token) || !seen.Add(token))
                    {
                        continue;
                    }
                    merged.Add(token);
                }
                merged.Sort(StringComparer.Ordinal); // deterministic, matches the collector's name-ordered includes

                var missing = merged.Where(t => !ownIncludes.Contains(t)).ToList();

                superset.ConsolidationRule = IndexCleanupRules.KeySuperset;
                superset.Action = IndexCleanupAction.MergeIncludes;
                superset.MergedIncludes = merged;
                superset.MissingIncludedColumns = missing.Count > 0 ? string.Join(", ", missing) : null;
                superset.SupersededBy = "Supersedes " + string.Join(", ", subsets.Select(s => s.Name));
                superset.Processed = true;
            }
        }

        /// <summary>
        /// Rule 5: indexes with the same keys (and filter) but different includes. The keeper (unique first,
        /// then priority, then name) absorbs the losers' includes (MERGE INCLUDES); the losers are disabled.
        /// </summary>
        private static void ApplyKeyDuplicate(List<WorkIndex> scope)
        {
            foreach (var group in DedupeCandidates(scope)
                .GroupBy(w => (w.KeyColumns, w.Filter), KeyFilterComparer.Instance)
                .Where(g => g.Count() > 1))
            {
                var members = group.ToList();
                var keeper = PickKeyDuplicateKeeper(members);
                if (keeper == null)
                {
                    continue;
                }

                var mergedIncludes = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var token in members.SelectMany(m => m.IncludeTokens))
                {
                    if (seen.Add(token))
                    {
                        mergedIncludes.Add(token);
                    }
                }
                mergedIncludes.Sort(StringComparer.Ordinal);

                var losers = members.Where(m => !ReferenceEquals(m, keeper)).ToList();
                var disabled = new List<WorkIndex>();
                foreach (var loser in losers)
                {
                    loser.ConsolidationRule = IndexCleanupRules.KeyDuplicate;
                    loser.Processed = true;
                    if (loser.Protected)
                    {
                        continue; // never disable a protected index
                    }
                    loser.Action = IndexCleanupAction.Disable;
                    loser.TargetIndexName = keeper.Name;
                    disabled.Add(loser);
                }

                keeper.ConsolidationRule = IndexCleanupRules.KeyDuplicate;
                keeper.Action = IndexCleanupAction.MergeIncludes;
                keeper.MergedIncludes = mergedIncludes;
                keeper.Processed = true;
                if (disabled.Count > 0)
                {
                    keeper.SupersededBy = "Supersedes " + string.Join(", ", disabled.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal));
                }
            }
        }

        // ────────────────────────────────────────────────────────────────────────────────────────────
        //  Keeper / grouping helpers
        // ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Indexes eligible to participate in the string-compare dedupe rules (nonclustered, not a unique constraint, still unprocessed).</summary>
        private static IEnumerable<WorkIndex> DedupeCandidates(List<WorkIndex> scope) =>
            scope.Where(w => !w.Processed && w.IsEligibleForDedupe && !w.Src.IsUniqueConstraint);

        /// <summary>
        /// Labels a set of equivalent duplicates: the highest-priority (then lowest-name) member is the
        /// keeper (KEEP); the rest are disabled (unless protected, in which case the disable is skipped —
        /// the protection is a hard guarantee). If the natural keeper is unprotected but a protected member
        /// exists, the protected member is promoted to keeper so nothing protected is ever disabled.
        /// </summary>
        private static void LabelDuplicateGroup(List<WorkIndex> members, string rule)
        {
            var keeper = members
                .OrderByDescending(m => m.Protected)   // never disable a protected index → make it the keeper
                .ThenByDescending(m => m.Priority)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .First();

            foreach (var m in members)
            {
                m.ConsolidationRule = rule;
                m.Processed = true;

                if (ReferenceEquals(m, keeper))
                {
                    m.Action = IndexCleanupAction.Keep;
                    continue;
                }

                if (m.Protected)
                {
                    // Two protected members in one duplicate set (rare): keep both, disable neither.
                    m.Action = IndexCleanupAction.Keep;
                    continue;
                }

                m.Action = IndexCleanupAction.Disable;
                m.TargetIndexName = keeper.Name;
            }
        }

        /// <summary>Rule 5 keeper: prefer a unique member, then priority, then name.</summary>
        private static WorkIndex? PickKeyDuplicateKeeper(List<WorkIndex> members) =>
            members
                .OrderByDescending(m => m.Protected)
                .ThenByDescending(m => m.Src.IsUnique)
                .ThenByDescending(m => m.Priority)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .FirstOrDefault();

        private static bool IsReverseDuplicate(WorkIndex a, WorkIndex b)
        {
            if (a.Keys.Count == 0 || a.Keys.Count != b.Keys.Count)
            {
                return false;
            }

            for (int k = 0; k < a.Keys.Count; k++)
            {
                // Same column, same position, but the sort direction must be inverted on EVERY key column.
                if (!string.Equals(a.Keys[k].Name, b.Keys[k].Name, StringComparison.Ordinal)
                    || a.Keys[k].Descending == b.Keys[k].Descending)
                {
                    return false;
                }
            }

            return string.Equals(a.IncludedColumns, b.IncludedColumns, StringComparison.Ordinal)
                && string.Equals(a.Filter, b.Filter, StringComparison.Ordinal);
        }

        // ────────────────────────────────────────────────────────────────────────────────────────────
        //  Recommendation + script assembly
        // ────────────────────────────────────────────────────────────────────────────────────────────

        private static IEnumerable<IndexCleanupRecommendation> BuildRecommendations(List<WorkIndex> analyzed, IndexCleanupOptions options)
        {
            foreach (var w in analyzed)
            {
                if (w.Action == IndexCleanupAction.Disable)
                {
                    yield return BuildDisable(w);
                }
                else if (w.Action == IndexCleanupAction.MergeIncludes)
                {
                    yield return BuildMerge(w, options);
                }
                else if ((w.Action == IndexCleanupAction.None || w.Action == IndexCleanupAction.Keep) && w.CanCompress)
                {
                    // Only untouched / kept indexes get a compression row (matches sp_IndexCleanup).
                    yield return BuildCompress(w, options);
                }
            }
        }

        private static IndexCleanupRecommendation BuildDisable(WorkIndex w)
        {
            string additionalInfo = w.ConsolidationRule switch
            {
                IndexCleanupRules.KeySubset => "This index is superseded by a wider index: " + (w.TargetIndexName ?? "(unknown)"),
                IndexCleanupRules.ExactDuplicate => "This index is an exact duplicate of: " + (w.TargetIndexName ?? "(unknown)"),
                IndexCleanupRules.ReverseDuplicate => "This index is a reverse-order duplicate of: " + (w.TargetIndexName ?? "(unknown)"),
                IndexCleanupRules.EqualExceptForFilter => "This index matches except for its filter: " + (w.TargetIndexName ?? "(unknown)") + " (verify the differing WHERE clauses before disabling)",
                IndexCleanupRules.KeyDuplicate => "This index has the same keys as: " + (w.TargetIndexName ?? "(unknown)"),
                _ when w.ConsolidationRule != null && w.ConsolidationRule.StartsWith(IndexCleanupRules.UnusedIndex, StringComparison.Ordinal) => w.ConsolidationRule,
                _ => "This index is redundant and will be disabled",
            };

            return NewRecommendation(w) with
            {
                Action = IndexCleanupAction.Disable,
                ResultKind = IndexCleanupResultKind.Disable,
                TargetIndexName = w.TargetIndexName,
                ScriptType = "DISABLE SCRIPT",
                Script = DisableScript(w),
                AdditionalInfo = additionalInfo,
            };
        }

        private static IndexCleanupRecommendation BuildMerge(WorkIndex w, IndexCleanupOptions options)
        {
            string additionalInfo = w.ConsolidationRule == IndexCleanupRules.KeySuperset
                ? "This index will absorb includes from the narrower indexes it supersedes"
                : "This index will absorb includes from duplicate indexes";

            var (script, omitsPlacement) = MergeScript(w, options);

            return NewRecommendation(w) with
            {
                Action = IndexCleanupAction.MergeIncludes,
                ResultKind = IndexCleanupResultKind.Merge,
                SupersededBy = w.SupersededBy,
                MissingIncludedColumns = w.MissingIncludedColumns,
                ScriptType = "MERGE SCRIPT",
                Script = script,
                AdditionalInfo = additionalInfo,
                ScriptOmitsPartitionPlacement = omitsPlacement,
            };
        }

        private static IndexCleanupRecommendation BuildCompress(WorkIndex w, IndexCleanupOptions options)
        {
            return NewRecommendation(w) with
            {
                Action = IndexCleanupAction.Keep,
                ResultKind = IndexCleanupResultKind.Compress,
                ConsolidationRule = w.ConsolidationRule,
                ScriptType = "COMPRESSION SCRIPT",
                Script = CompressionScript(w, options),
                AdditionalInfo = "Compression type: PAGE (All Partitions)",
            };
        }

        private static IndexCleanupRecommendation NewRecommendation(WorkIndex w) => new()
        {
            DatabaseName = w.Src.DatabaseName,
            SchemaName = w.Src.SchemaName,
            TableName = w.Src.TableName,
            IndexName = w.Name,
            IndexId = w.Src.IndexId,
            ConsolidationRule = w.ConsolidationRule,
            OriginalIndexDefinition = OriginalIndexDefinition(w),
            IndexSizeGb = w.SizeGb,
            IndexRows = w.Src.TotalRows ?? 0,
            IndexReads = w.Reads,
            IndexWrites = w.Src.UserUpdates,
            CanCompress = w.CanCompress,
            IsForeignKey = w.Src.IsForeignKey,
        };

        private static string DisableScript(WorkIndex w) =>
            "ALTER INDEX " + QuoteName(w.Name) + " ON " + FullName(w) + " DISABLE;";

        private static (string Script, bool OmitsPlacement) MergeScript(WorkIndex w, IndexCleanupOptions options)
        {
            string includes = string.Join(", ", w.MergedIncludes ?? w.IncludeTokens);

            var script = "CREATE " + (w.Src.IsUnique ? "UNIQUE " : "") + "INDEX " + QuoteName(w.Name)
                + " ON " + FullName(w) + " (" + w.KeyColumns + ")"
                + (includes.Length > 0 ? " INCLUDE (" + includes + ")" : "")
                + (w.Filter.Length > 0 ? " WHERE " + w.Filter : "")
                + " WITH (DROP_EXISTING = ON, FILLFACTOR = 100, SORT_IN_TEMPDB = ON, ONLINE = "
                + (options.ServerSupportsOnline ? "ON" : "OFF")
                + (w.CanCompress ? ", DATA_COMPRESSION = PAGE" : "")
                + (w.Src.OptimizeForSequentialKey ? ", OPTIMIZE_FOR_SEQUENTIAL_KEY = ON" : "")
                + ");";

            return (script, IsPartitioned(w));
        }

        private static string CompressionScript(WorkIndex w, IndexCleanupOptions options) =>
            "ALTER INDEX " + QuoteName(w.Name) + " ON " + FullName(w)
            + (IsPartitioned(w) ? " REBUILD PARTITION = ALL" : " REBUILD")
            + " WITH (FILLFACTOR = 100, SORT_IN_TEMPDB = ON, ONLINE = "
            + (options.ServerSupportsOnline ? "ON" : "OFF")
            + ", DATA_COMPRESSION = PAGE"
            + (w.Src.OptimizeForSequentialKey ? ", OPTIMIZE_FOR_SEQUENTIAL_KEY = ON" : "")
            + ");";

        /// <summary>
        /// Reconstructs the original CREATE (or ADD CONSTRAINT) from captured metadata — the rollback /
        /// validation reference. Uses the captured delimited key/include strings verbatim. The trailing
        /// <c>ON &lt;filegroup/partition_scheme&gt;</c> is omitted (Stage 1 did not capture it).
        /// </summary>
        private static string OriginalIndexDefinition(WorkIndex w)
        {
            if (w.Src.IsUniqueConstraint)
            {
                return "ALTER TABLE " + FullName(w) + " ADD CONSTRAINT " + QuoteName(w.Name)
                    + " UNIQUE (" + w.KeyColumns + ");";
            }

            string clustering = w.Src.IndexId == 1 ? "CLUSTERED " : "NONCLUSTERED ";
            return "CREATE " + (w.Src.IsUnique ? "UNIQUE " : "") + clustering + "INDEX " + QuoteName(w.Name)
                + " ON " + FullName(w) + " (" + w.KeyColumns + ")"
                + (w.IncludedColumns.Length > 0 ? " INCLUDE (" + w.IncludedColumns + ")" : "")
                + (w.Filter.Length > 0 ? " WHERE " + w.Filter : "")
                + ";";
        }

        private static bool IsPartitioned(WorkIndex w) => (w.Src.PartitionCount ?? 1) > 1;

        private static string FullName(WorkIndex w) =>
            QuoteName(w.Src.DatabaseName) + "." + QuoteName(w.Src.SchemaName) + "." + QuoteName(w.Src.TableName);

        /// <summary>QUOTENAME: wrap in brackets, doubling any embedded closing bracket.</summary>
        private static string QuoteName(string name) =>
            "[" + (name ?? "").Replace("]", "]]", StringComparison.Ordinal) + "]";

        // ────────────────────────────────────────────────────────────────────────────────────────────
        //  Rollups + notes
        // ────────────────────────────────────────────────────────────────────────────────────────────

        private static IndexCleanupRollup BuildRollup(string databaseName, List<WorkIndex> analyzed, IndexCleanupOptions options)
        {
            decimal unusedSize = 0, compMin = 0, compMax = 0;
            int disable = 0, merge = 0, compressable = 0, unused = 0;
            decimal totalSize = 0;

            foreach (var w in analyzed)
            {
                totalSize += w.SizeGb;

                if (w.Action == IndexCleanupAction.Disable)
                {
                    disable++;
                    unusedSize += w.SizeGb;
                    if (w.ConsolidationRule != null && w.ConsolidationRule.StartsWith(IndexCleanupRules.UnusedIndex, StringComparison.Ordinal))
                    {
                        unused++;
                    }
                }
                else if (w.Action == IndexCleanupAction.MergeIncludes)
                {
                    merge++;
                }

                if (w.CanCompress)
                {
                    compressable++;
                    if (w.Action == IndexCleanupAction.None || w.Action == IndexCleanupAction.Keep)
                    {
                        compMin += w.SizeGb * options.CompressionMinSavingsFactor;
                        compMax += w.SizeGb * options.CompressionMaxSavingsFactor;
                    }
                }
            }

            // Table row count = the base row (index_id 0/1) per object, summed across objects.
            long totalRows = analyzed
                .Where(w => w.Src.IndexId <= 1)
                .GroupBy(w => w.Src.ObjectId)
                .Sum(g => g.First().Src.TotalRows ?? 0);

            return new IndexCleanupRollup
            {
                DatabaseName = databaseName,
                TablesAnalyzed = analyzed.Select(w => w.Src.ObjectId).Distinct().Count(),
                IndexCount = analyzed.Count,
                TotalSizeGb = totalSize,
                TotalRows = totalRows,
                IndexesToDisable = disable,
                IndexesToMerge = merge,
                CompressableIndexes = compressable,
                UnusedIndexes = unused,
                UnusedSizeGb = unusedSize,
                CompressionMinSavingsGb = compMin,
                CompressionMaxSavingsGb = compMax,
                TotalMinSavingsGb = unusedSize + compMin,
                TotalMaxSavingsGb = unusedSize + compMax,
            };
        }

        private static IndexCleanupRollup BuildOverallRollup(List<IndexCleanupRollup> perDb) => new()
        {
            DatabaseName = null,
            TablesAnalyzed = perDb.Sum(r => r.TablesAnalyzed),
            IndexCount = perDb.Sum(r => r.IndexCount),
            TotalSizeGb = perDb.Sum(r => r.TotalSizeGb),
            TotalRows = perDb.Sum(r => r.TotalRows),
            IndexesToDisable = perDb.Sum(r => r.IndexesToDisable),
            IndexesToMerge = perDb.Sum(r => r.IndexesToMerge),
            CompressableIndexes = perDb.Sum(r => r.CompressableIndexes),
            UnusedIndexes = perDb.Sum(r => r.UnusedIndexes),
            UnusedSizeGb = perDb.Sum(r => r.UnusedSizeGb),
            CompressionMinSavingsGb = perDb.Sum(r => r.CompressionMinSavingsGb),
            CompressionMaxSavingsGb = perDb.Sum(r => r.CompressionMaxSavingsGb),
            TotalMinSavingsGb = perDb.Sum(r => r.TotalMinSavingsGb),
            TotalMaxSavingsGb = perDb.Sum(r => r.TotalMaxSavingsGb),
        };

        private static List<string> BuildNotes(List<IndexCleanupRecommendation> recommendations)
        {
            var notes = new List<string>();

            if (recommendations.Any(r => r.ScriptOmitsPartitionPlacement))
            {
                notes.Add("Reconstructed CREATE/MERGE scripts omit the trailing ON <filegroup/partition_scheme> clause: "
                    + "Stage 1 did not capture filegroup/partition placement. For a partitioned index the rebuild would "
                    + "default to the table's scheme mapping; verify placement before running.");
            }

            if (recommendations.Any(r => r.ResultKind == IndexCleanupResultKind.Compress))
            {
                notes.Add("Compression eligibility cannot exclude tables with sparse columns or legacy LOB types "
                    + "(text/ntext/image): Stage 1 did not capture per-column type/sparse metadata, so a small number of "
                    + "compression candidates may be ineligible in practice (sp_IndexCleanup excludes them via a live column scan).");
                notes.Add("Compression savings use sp_IndexCleanup's general 0.20–0.60 × size band, NOT "
                    + "sp_estimate_data_compression_savings (a live-sampling proc, out of scope for data-driven analysis).");
                notes.Add("COMPRESSION REBUILD uses PARTITION = ALL when partition_count > 1 (a proxy; Stage 1 did not "
                    + "capture the partition scheme name).");
            }

            notes.Add("Out of scope per this stage's DISABLE/MERGE INCLUDES/KEEP action set: sp_IndexCleanup's "
                + "'Unique Constraint Replacement' (MAKE UNIQUE) and 'Same Keys Different Order' (REVIEW) rules. Both are "
                + "reproducible from the captured metadata if later required.");

            return notes;
        }

        // ────────────────────────────────────────────────────────────────────────────────────────────
        //  Input shaping
        // ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Only rowstore CLUSTERED/NONCLUSTERED, enabled, named indexes are analyzed (mirrors <c>i.type IN (1,2) AND i.is_disabled = 0</c>).</summary>
        private static bool IsAnalyzable(IndexCleanupIndexInput src)
        {
            if (src == null || src.IsDisabled || string.IsNullOrEmpty(src.IndexName))
            {
                return false;
            }

            return string.Equals(src.IndexTypeDesc, Clustered, StringComparison.OrdinalIgnoreCase)
                || string.Equals(src.IndexTypeDesc, NonClustered, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Object-level gates: @min_rows always; @min_reads/@min_writes only when the caller set them (&gt; 0), matching sp_IndexCleanup.</summary>
        private static bool PassesObjectFilters(List<WorkIndex> scopeIndexes, IndexCleanupOptions options)
        {
            long objectRows = scopeIndexes
                .Where(w => w.Src.IndexId <= 1)
                .Select(w => w.Src.TotalRows ?? 0)
                .DefaultIfEmpty(scopeIndexes.Max(w => w.Src.TotalRows ?? 0))
                .First();

            if (objectRows < options.MinRows)
            {
                return false;
            }

            if (options.MinReads > 0 || options.MinWrites > 0)
            {
                long reads = scopeIndexes.Sum(w => w.Reads);
                long writes = scopeIndexes.Sum(w => w.Src.UserUpdates);
                if (reads < options.MinReads && writes < options.MinWrites)
                {
                    return false;
                }
            }

            return true;
        }

        private static double? ResolveUptimeDays(List<WorkIndex> analyzable, IndexCleanupOptions options)
        {
            if (options.ServerUptimeDays.HasValue)
            {
                return options.ServerUptimeDays;
            }

            DateTime? start = analyzable
                .Select(w => w.Src.SqlServerStartTime)
                .Where(t => t.HasValue)
                .DefaultIfEmpty(null)
                .Max();

            if (!start.HasValue)
            {
                return null;
            }

            var reference = options.ReferenceTimeUtc ?? DateTime.UtcNow;
            var days = (reference - start.Value).TotalDays;
            return days < 0 ? null : days;
        }

        private static int ResultKindSort(IndexCleanupResultKind kind) => kind switch
        {
            IndexCleanupResultKind.Merge => 0,
            IndexCleanupResultKind.Disable => 1,
            IndexCleanupResultKind.Compress => 2,
            _ => 3,
        };

        // ────────────────────────────────────────────────────────────────────────────────────────────
        //  Working state + delimited-string parsing
        // ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Mutable per-index state carried through the sequential rule passes.</summary>
        private sealed class WorkIndex
        {
            public WorkIndex(IndexCleanupIndexInput src)
            {
                Src = src;
                Name = src.IndexName ?? "";
                KeyColumns = src.KeyColumns ?? "";
                IncludedColumns = src.IncludedColumns ?? "";
                Filter = src.FilterDefinition ?? "";
                Keys = ParseKeyTokens(KeyColumns);
                KeyNames = Keys.Select(k => k.Name).ToList();
                IncludeTokens = SplitDelimited(IncludedColumns);

                // is_eligible_for_dedupe: nonclustered → yes; clustered → no (mirrors sp_IndexCleanup's CASE,
                // which checks type = 2 before the clustered/PK branch).
                IsEligibleForDedupe = string.Equals(src.IndexTypeDesc, NonClustered, StringComparison.OrdinalIgnoreCase);

                Priority = ComputePriority(src);
                SizeGb = (src.ReservedMb ?? 0m) / 1024m;
            }

            public IndexCleanupIndexInput Src { get; }
            public string Name { get; }
            public string KeyColumns { get; }
            public string IncludedColumns { get; }
            public string Filter { get; }
            public List<KeyColumn> Keys { get; }
            public List<string> KeyNames { get; }
            public List<string> IncludeTokens { get; }
            public bool IsEligibleForDedupe { get; }
            public int Priority { get; }
            public decimal SizeGb { get; }

            public long Reads => Src.UserSeeks + Src.UserScans + Src.UserLookups;

            /// <summary>Hard disable protection: PK, unique constraint, or FK-referenced (matches spec item 3).</summary>
            public bool Protected => Src.IsPrimaryKey || Src.IsUniqueConstraint || Src.IsForeignKeyReference;

            // Mutable rule state
            public bool Processed { get; set; }
            public string? ConsolidationRule { get; set; }
            public IndexCleanupAction Action { get; set; } = IndexCleanupAction.None;
            public string? TargetIndexName { get; set; }
            public string? SupersededBy { get; set; }
            public List<string>? MergedIncludes { get; set; }
            public string? MissingIncludedColumns { get; set; }
            public bool CanCompress { get; set; }
        }

        private readonly record struct KeyColumn(string Name, bool Descending);

        /// <summary>sp_IndexCleanup priority: clustered 1000; unique 500 (unique constraint only 50); seeks 200; scans 100; has includes 50.</summary>
        private static int ComputePriority(IndexCleanupIndexInput src)
        {
            int priority = src.IndexId == 1 ? 1000 : 0;

            if (src.IsUnique)
            {
                priority += src.IsUniqueConstraint ? 50 : 500;
            }

            if (src.UserSeeks > 0)
            {
                priority += 200;
            }

            if (src.UserScans > 0)
            {
                priority += 100;
            }

            if (!string.IsNullOrEmpty(src.IncludedColumns))
            {
                priority += 50;
            }

            return priority;
        }

        /// <summary>Parses a delimited key list into (bracketed-name, descending) tokens, respecting QUOTENAME bracketing.</summary>
        private static List<KeyColumn> ParseKeyTokens(string raw)
        {
            var result = new List<KeyColumn>();
            foreach (var token in SplitDelimited(raw))
            {
                if (token.EndsWith(" DESC", StringComparison.Ordinal))
                {
                    result.Add(new KeyColumn(token[..^5].TrimEnd(), true));
                }
                else
                {
                    result.Add(new KeyColumn(token, false));
                }
            }
            return result;
        }

        /// <summary>
        /// Splits a QUOTENAME-delimited list on the ", " separator that sits between tokens, ignoring commas
        /// and spaces inside <c>[ ... ]</c> (where <c>]]</c> is an escaped bracket). Robust to column names
        /// that themselves contain commas, spaces, or brackets.
        /// </summary>
        private static List<string> SplitDelimited(string? raw)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(raw))
            {
                return tokens;
            }

            int n = raw.Length;
            int start = 0;
            int i = 0;
            bool inBracket = false;

            while (i < n)
            {
                char c = raw[i];
                if (!inBracket)
                {
                    if (c == '[')
                    {
                        inBracket = true;
                        i++;
                    }
                    else if (c == ',' && i + 1 < n && raw[i + 1] == ' ')
                    {
                        AddToken(tokens, raw, start, i);
                        i += 2;
                        start = i;
                    }
                    else
                    {
                        i++;
                    }
                }
                else
                {
                    if (c == ']')
                    {
                        if (i + 1 < n && raw[i + 1] == ']')
                        {
                            i += 2; // escaped ]] inside a name
                        }
                        else
                        {
                            inBracket = false;
                            i++;
                        }
                    }
                    else
                    {
                        i++;
                    }
                }
            }

            AddToken(tokens, raw, start, n);
            return tokens;
        }

        private static void AddToken(List<string> tokens, string raw, int start, int end)
        {
            var token = raw.Substring(start, end - start).Trim();
            if (token.Length > 0)
            {
                tokens.Add(token);
            }
        }

        // Composite grouping-key comparers over the raw delimited strings (byte-for-byte, ordinal).

        private sealed class KeyIncludeFilterComparer : IEqualityComparer<(string Key, string Include, string Filter)>
        {
            public static readonly KeyIncludeFilterComparer Instance = new();
            public bool Equals((string Key, string Include, string Filter) x, (string Key, string Include, string Filter) y) =>
                string.Equals(x.Key, y.Key, StringComparison.Ordinal)
                && string.Equals(x.Include, y.Include, StringComparison.Ordinal)
                && string.Equals(x.Filter, y.Filter, StringComparison.Ordinal);
            public int GetHashCode((string Key, string Include, string Filter) obj) =>
                HashCode.Combine(obj.Key, obj.Include, obj.Filter);
        }

        private sealed class KeyIncludeComparer : IEqualityComparer<(string Key, string Include)>
        {
            public static readonly KeyIncludeComparer Instance = new();
            public bool Equals((string Key, string Include) x, (string Key, string Include) y) =>
                string.Equals(x.Key, y.Key, StringComparison.Ordinal)
                && string.Equals(x.Include, y.Include, StringComparison.Ordinal);
            public int GetHashCode((string Key, string Include) obj) =>
                HashCode.Combine(obj.Key, obj.Include);
        }

        private sealed class KeyFilterComparer : IEqualityComparer<(string Key, string Filter)>
        {
            public static readonly KeyFilterComparer Instance = new();
            public bool Equals((string Key, string Filter) x, (string Key, string Filter) y) =>
                string.Equals(x.Key, y.Key, StringComparison.Ordinal)
                && string.Equals(x.Filter, y.Filter, StringComparison.Ordinal);
            public int GetHashCode((string Key, string Filter) obj) =>
                HashCode.Combine(obj.Key, obj.Filter);
        }
    }
}

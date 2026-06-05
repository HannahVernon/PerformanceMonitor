/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using PerformanceMonitor.Analysis;
using PerformanceMonitorDashboard.Analysis;
using PerformanceMonitorDashboard.Models;

namespace PerformanceMonitorDashboard.Services.Recommendations
{
    /// <summary>
    /// The data layer for the unified Recommendations surface (WS1a). Reads BOTH producers
    /// for a server — engine findings from <c>config.analysis_findings</c> (via the existing
    /// <see cref="SqlServerFindingStore.GetRecentFindingsAsync"/>) and legacy config recs from
    /// <c>config.critical_issues</c> (via the existing
    /// <see cref="DatabaseService.GetCriticalIssuesAsync"/>) — maps each row to a unified
    /// <see cref="RecommendationItem"/>, then de-dupes + sorts via the pure
    /// <see cref="RecommendationDeduper"/>. No new raw SQL is written for the reads; this class
    /// reuses the two existing read methods so it inherits their parameterization.
    ///
    /// <para>
    /// Nothing renders here — this is the foundational data layer (the XAML control + tab
    /// wiring are a later workstream). The per-row mappers are <c>internal static</c> so the
    /// engine/legacy → setting + copy-paste mapping can be unit-tested directly.
    /// </para>
    /// </summary>
    public sealed class RecommendationsReader
    {
        private readonly DatabaseService _databaseService;
        private readonly SqlServerFindingStore _findingStore;

        /// <summary>The two legacy problem-areas that carry per-database config-setting recs.</summary>
        internal const string ProblemAreaDatabaseConfiguration = "Database Configuration";
        internal const string ProblemAreaQueryStoreConfiguration = "Query Store Configuration";

        /// <summary>
        /// Legacy "pressure/growth" problem-areas that are noise — short-window deltas, no
        /// quantification, circular investigate queries — AND inferior duplicates of analysis-
        /// engine facts (RESOURCE_SEMAPHORE / SOS_SCHEDULER_YIELD / THREADPOOL / memory-grant
        /// waits). Suppressed from the Recommendations surface; also cut at the source in
        /// install/50. See the recommendations-engine-rebuild plan, "Legacy-rule curation".
        /// </summary>
        internal static readonly HashSet<string> SuppressedLegacyProblemAreas =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Memory Pressure",
                "Memory Grant Pressure",
                "CPU Scheduling Pressure",
                "Memory Clerk Growth",
            };

        public RecommendationsReader(DatabaseService databaseService, SqlServerFindingStore findingStore)
        {
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _findingStore = findingStore ?? throw new ArgumentNullException(nameof(findingStore));
        }

        /// <summary>
        /// Reads both stores for the server, maps each row, de-dupes (C3), and returns the
        /// unified list sorted by severity desc. <paramref name="serverName"/> is carried for
        /// display only (the reads key on <paramref name="serverId"/> for findings and on the
        /// connection's own server for the legacy store). <paramref name="hoursBack"/> bounds
        /// both reads to the same window. <paramref name="utcOffsetMinutes"/> is the monitored
        /// server's UTC offset; it is applied ONLY to the legacy <c>log_date</c> (server-local)
        /// to normalize it to the UTC window the engine rows already carry, so both producers
        /// expose a single consistent UTC window (the navigation handler converts back).
        /// </summary>
        public async Task<List<RecommendationItem>> GetRecommendationsAsync(
            int serverId, string serverName, int hoursBack = 24, int limit = 100, int utcOffsetMinutes = 0)
        {
            var findings = await _findingStore.GetRecentFindingsAsync(serverId, hoursBack, limit);
            var legacy = await _databaseService.GetCriticalIssuesAsync(hoursBack);

            var engineItems = new List<RecommendationItem>(findings.Count);
            foreach (var finding in findings)
                engineItems.Add(MapEngineFinding(finding));

            var legacyItems = new List<RecommendationItem>(legacy.Count);
            foreach (var issue in legacy)
            {
                // Drop the legacy pressure/growth noise (duplicated, better, by engine facts).
                if (SuppressedLegacyProblemAreas.Contains(issue.ProblemArea ?? string.Empty))
                    continue;
                legacyItems.Add(MapLegacyIssue(issue, utcOffsetMinutes));
            }

            return RecommendationDeduper.Merge(engineItems, legacyItems);
        }

        /// <summary>
        /// Maps one engine <see cref="AnalysisFinding"/> to a <see cref="RecommendationItem"/>.
        /// Advice comes from <see cref="FactAdvice"/> for the root fact key; the de-dupe
        /// <see cref="RecommendationSetting"/> and the copy-paste SQL come from the PERSISTED
        /// <see cref="AnalysisFinding.Remediation"/> action — the drill-down that the live
        /// builders consume is ephemeral and is NOT returned by the store read, so the built
        /// action (which IS persisted) is the authoritative source on read.
        /// </summary>
        internal static RecommendationItem MapEngineFinding(AnalysisFinding finding)
        {
            var band = RecommendationDeduper.FromEngineSeverity(finding.Severity);
            var advice = FactAdvice.GetForFactKey(finding.RootFactKey);

            return new RecommendationItem
            {
                Source = RecommendationSource.Engine,
                CanonicalSeverity = band,
                RawSeverity = finding.Severity,
                Database = finding.DatabaseName,
                Title = !string.IsNullOrEmpty(advice?.Headline) ? advice!.Headline : finding.StoryText,
                ProblemArea = finding.Category,
                AdviceText = ComposeEngineAdvice(advice),
                CopyPasteSql = BuildCopyPasteFromAction(finding.Remediation),
                Remediation = finding.Remediation,
                StoryPathHash = finding.StoryPathHash,
                StoryPath = finding.StoryPath,
                Setting = SettingFromAction(finding.Remediation),
                WindowStartUtc = AsUtc(finding.TimeRangeStart),
                WindowEndUtc = AsUtc(finding.TimeRangeEnd)
            };
        }

        /// <summary>
        /// Maps one legacy <see cref="CriticalIssueItem"/> to a
        /// <see cref="RecommendationItem"/>. Advice is the row's <c>message</c>; copy-paste is
        /// the row's <c>investigate_query</c>; the de-dupe setting is derived ONLY for the two
        /// config problem-areas, from the <c>investigate_query</c> ALTER keyword (never the
        /// free-text message). Every other legacy row gets
        /// <see cref="RecommendationSetting.None"/> and is non-deduping pass-through.
        /// </summary>
        internal static RecommendationItem MapLegacyIssue(CriticalIssueItem issue, int utcOffsetMinutes = 0)
        {
            var band = RecommendationDeduper.FromLegacySeverity(issue.Severity);
            var database = string.IsNullOrEmpty(issue.AffectedDatabase) ? null : issue.AffectedDatabase;
            var sql = string.IsNullOrEmpty(issue.InvestigateQuery) ? null : issue.InvestigateQuery;

            return new RecommendationItem
            {
                Source = RecommendationSource.Legacy,
                CanonicalSeverity = band,
                RawSeverity = RecommendationDeduper.LegacyRawSeverity(band),
                Database = database,
                Title = issue.ProblemArea,
                ProblemArea = issue.ProblemArea,
                AdviceText = string.IsNullOrEmpty(issue.Message) ? null : issue.Message,
                CopyPasteSql = sql,
                Remediation = null,           // legacy rows are advise/copy-paste only — never Apply
                StoryPathHash = null,          // the legacy store has no mute concept
                Setting = SettingFromLegacy(issue.ProblemArea, sql),
                // log_date is server-local; subtract the offset to express the same instant in UTC.
                WindowStartUtc = LegacyLogDateToUtc(issue.LogDate, utcOffsetMinutes),
                WindowEndUtc = LegacyLogDateToUtc(issue.LogDate, utcOffsetMinutes)
            };
        }

        /// <summary>
        /// Derives the de-dupe <see cref="RecommendationSetting"/> for an ENGINE finding from
        /// its persisted <see cref="RemediationAction"/>. RCSI is its own fact-key (and its own
        /// setting); DB_CONFIG carries one or more <see cref="DbConfigTarget"/>s whose
        /// <see cref="DbConfigSetting"/> maps to a setting per flagged option. Only the FIRST
        /// flagged DB-config option contributes the key (a finding's database is single, and
        /// AUTO_SHRINK/AUTO_CLOSE are the only cross-store collisions — distinct per-DB
        /// findings keep distinct rows). Returns <see cref="RecommendationSetting.None"/> when
        /// there is no action or no recognized config target (so non-config findings —
        /// CPU/waits/etc. — never de-dupe).
        /// </summary>
        internal static RecommendationSetting SettingFromAction(RemediationAction? action)
        {
            if (action is null)
                return RecommendationSetting.None;

            // RCSI is routed through a distinct fact key with a single ReadCommittedSnapshotOn
            // target; surface it as the Rcsi setting (engine-only — it never collides).
            if (string.Equals(action.FactKey, "RCSI", StringComparison.Ordinal))
                return RecommendationSetting.Rcsi;

            if (action.DbConfigTargets is { Count: > 0 } targets)
            {
                foreach (var target in targets)
                {
                    var setting = SettingFromDbConfig(target.Setting);
                    if (setting != RecommendationSetting.None)
                        return setting;
                }
            }

            return RecommendationSetting.None;
        }

        /// <summary>
        /// Maps a single <see cref="DbConfigSetting"/> to its canonical de-dupe
        /// <see cref="RecommendationSetting"/>.
        /// </summary>
        internal static RecommendationSetting SettingFromDbConfig(DbConfigSetting setting) => setting switch
        {
            DbConfigSetting.AutoShrinkOff => RecommendationSetting.AutoShrink,
            DbConfigSetting.AutoCloseOff => RecommendationSetting.AutoClose,
            DbConfigSetting.PageVerifyChecksum => RecommendationSetting.PageVerify,
            DbConfigSetting.ReadCommittedSnapshotOn => RecommendationSetting.Rcsi,
            _ => RecommendationSetting.None
        };

        /// <summary>
        /// Derives the de-dupe <see cref="RecommendationSetting"/> for a LEGACY row. ONLY the
        /// two config problem-areas are eligible; for them the setting is read from the
        /// <c>investigate_query</c> ALTER keyword (AUTO_SHRINK → AutoShrink, AUTO_CLOSE →
        /// AutoClose, QUERY_STORE → QueryStore). The free-text <c>message</c> is never parsed.
        /// Any other problem-area → <see cref="RecommendationSetting.None"/> (pass-through).
        /// </summary>
        internal static RecommendationSetting SettingFromLegacy(string? problemArea, string? investigateQuery)
        {
            var area = problemArea?.Trim();
            bool isConfigArea =
                string.Equals(area, ProblemAreaDatabaseConfiguration, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(area, ProblemAreaQueryStoreConfiguration, StringComparison.OrdinalIgnoreCase);

            if (!isConfigArea || string.IsNullOrEmpty(investigateQuery))
                return RecommendationSetting.None;

            // Match on the ALTER keyword in the copy-paste SQL only. AUTO_SHRINK / AUTO_CLOSE
            // are substrings of distinct keywords; QUERY_STORE covers both SET QUERY_STORE and
            // ALTER DATABASE ... SET QUERY_STORE forms.
            if (ContainsToken(investigateQuery, "AUTO_SHRINK"))
                return RecommendationSetting.AutoShrink;
            if (ContainsToken(investigateQuery, "AUTO_CLOSE"))
                return RecommendationSetting.AutoClose;
            if (ContainsToken(investigateQuery, "QUERY_STORE"))
                return RecommendationSetting.QueryStore;

            return RecommendationSetting.None;
        }

        /// <summary>
        /// Rebuilds the copy-paste <c>ALTER DATABASE</c> statements for an engine row from the
        /// PERSISTED action's DB-config targets (the live drill-down preview is not persisted).
        /// Mirrors the executor's SET-clause literals exactly. Returns null when the action has
        /// no DB-config targets (force-plan / clear-plan / null → no DB-config copy-paste here).
        /// </summary>
        internal static string? BuildCopyPasteFromAction(RemediationAction? action)
        {
            if (action?.DbConfigTargets is not { Count: > 0 } targets)
                return null;

            var sb = new StringBuilder();
            foreach (var target in targets)
            {
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append(AlterStatementFor(target));
            }

            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>
        /// One <c>ALTER DATABASE [db] SET ...;</c> statement for a persisted
        /// <see cref="DbConfigTarget"/>. The bracketed identifier doubles any embedded
        /// close-bracket (QUOTENAME-equivalent); the SET-clause literal is selected by the
        /// hardcoded enum, matching the executor and FactRemediation's renderer.
        /// </summary>
        private static string AlterStatementFor(DbConfigTarget target)
        {
            var setClause = target.Setting switch
            {
                DbConfigSetting.AutoShrinkOff => "SET AUTO_SHRINK OFF",
                DbConfigSetting.AutoCloseOff => "SET AUTO_CLOSE OFF",
                DbConfigSetting.PageVerifyChecksum => "SET PAGE_VERIFY CHECKSUM",
                DbConfigSetting.ReadCommittedSnapshotOn => "SET READ_COMMITTED_SNAPSHOT ON",
                _ => null
            };

            if (setClause is null)
                return string.Empty;

            return $"ALTER DATABASE {QuoteName(target.Database)} {setClause};";
        }

        /// <summary>
        /// Composes the engine advice prose from a <see cref="FactAdvice"/> block: the
        /// remediation line, with the investigation line appended when present. Null when no
        /// advice matched the root fact key.
        /// </summary>
        private static string? ComposeEngineAdvice(AdviceBlock? advice)
        {
            if (advice is null)
                return null;

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(advice.Remediation))
                sb.Append(advice.Remediation);
            if (!string.IsNullOrEmpty(advice.Investigation))
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(advice.Investigation);
            }

            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>
        /// Case-insensitive substring test for an ALTER keyword in the copy-paste SQL.
        /// </summary>
        private static bool ContainsToken(string text, string token) =>
            text.Contains(token, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// QUOTENAME-equivalent: bracket an identifier, doubling any embedded close-bracket.
        /// </summary>
        private static string QuoteName(string identifier) =>
            "[" + (identifier ?? string.Empty).Replace("]", "]]") + "]";

        /// <summary>
        /// Stamps a nullable engine timestamp as UTC (the analysis pipeline records
        /// <c>TimeRangeStart/End</c> in UTC; the store read-back can return Kind=Unspecified, so
        /// fix the Kind to avoid an ambiguous later conversion). Null passes through.
        /// </summary>
        private static System.DateTime? AsUtc(System.DateTime? value) =>
            value is null ? null : System.DateTime.SpecifyKind(value.Value, System.DateTimeKind.Utc);

        /// <summary>
        /// Converts a legacy server-local <c>log_date</c> to the equivalent UTC instant by
        /// subtracting the monitored server's UTC offset, and stamps it Utc. With
        /// <paramref name="utcOffsetMinutes"/> == 0 (unit tests) this is an identity stamp.
        /// </summary>
        private static System.DateTime LegacyLogDateToUtc(System.DateTime logDate, int utcOffsetMinutes) =>
            System.DateTime.SpecifyKind(logDate.AddMinutes(-utcOffsetMinutes), System.DateTimeKind.Utc);
    }
}

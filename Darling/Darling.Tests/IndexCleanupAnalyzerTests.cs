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
using PerformanceMonitor.Common;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// DB-free tests for the shared <see cref="IndexCleanupAnalyzer"/> — the monitor-side reproduction of
/// <c>sp_IndexCleanup</c>'s analysis over the captured <c>index_object_stats</c> snapshot. Every rule
/// (Unused + uptime caveat, Exact/Reverse/Equal-Except-Filter, Key Duplicate, Key Subset/Superset, subset
/// chains), every disable protection (PK, unique constraint, FK-reference), compression candidacy + the
/// 0.20–0.60 savings band, the reclaimable roll-up, and the reconstructed T-SQL are asserted against
/// synthetic index sets. No database.
/// </summary>
public class IndexCleanupAnalyzerTests
{
    // ── input builder ──────────────────────────────────────────────────────────────────────────────

    private static IndexCleanupIndexInput Idx(
        string name,
        string? keys,
        int indexId = 2,
        string? includes = null,
        string? filter = null,
        string type = "NONCLUSTERED",
        bool unique = false,
        bool primaryKey = false,
        bool uniqueConstraint = false,
        bool foreignKeyReference = false,
        bool foreignKey = false,
        long seeks = 0,
        long scans = 0,
        long lookups = 0,
        long updates = 0,
        decimal reservedMb = 0m,
        string? compression = "PAGE",   // default already-compressed so dedupe tests aren't cluttered with COMPRESS rows
        long rows = 1000,
        int partitionCount = 1,
        bool optimizeSequentialKey = false,
        int objectId = 1,
        string database = "TestDb",
        string schema = "dbo",
        string table = "T") =>
        new()
        {
            DatabaseName = database,
            DatabaseId = 5,
            SchemaName = schema,
            ObjectId = objectId,
            TableName = table,
            IndexId = indexId,
            IndexName = name,
            IndexTypeDesc = type,
            KeyColumns = keys,
            IncludedColumns = includes,
            FilterDefinition = filter,
            IsUnique = unique || primaryKey || uniqueConstraint,
            IsUniqueConstraint = uniqueConstraint,
            IsPrimaryKey = primaryKey,
            IsForeignKey = foreignKey,
            IsForeignKeyReference = foreignKeyReference,
            IsDisabled = false,
            DataCompressionDesc = compression,
            OptimizeForSequentialKey = optimizeSequentialKey,
            UserSeeks = seeks,
            UserScans = scans,
            UserLookups = lookups,
            UserUpdates = updates,
            ReservedMb = reservedMb,
            TotalRows = rows,
            PartitionCount = partitionCount,
            SqlServerStartTime = null,
        };

    private static IndexCleanupOptions Opts(
        bool dedupeOnly = false,
        double? uptimeDays = 30,
        decimal minSizeGb = 0m,
        long minRows = 0,
        long minReads = 0,
        long minWrites = 0,
        bool supportsCompression = true,
        bool supportsOnline = false) =>
        new()
        {
            DedupeOnly = dedupeOnly,
            ServerUptimeDays = uptimeDays,
            MinSizeGb = minSizeGb,
            MinRows = minRows,
            MinReads = minReads,
            MinWrites = minWrites,
            ServerSupportsCompression = supportsCompression,
            ServerSupportsOnline = supportsOnline,
        };

    private static IndexCleanupRecommendation? Rec(IndexCleanupAnalysisResult r, string indexName) =>
        r.Recommendations.FirstOrDefault(x => x.IndexName == indexName);

    // ── Rule 1: Unused ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unused_NonclusteredWithNoReads_IsDisabled()
    {
        var r = IndexCleanupAnalyzer.Analyze(new[] { Idx("IX_Unused", "[a]") }, Opts());

        var rec = Assert.Single(r.Recommendations);
        Assert.Equal("IX_Unused", rec.IndexName);
        Assert.Equal(IndexCleanupAction.Disable, rec.Action);
        Assert.Equal(IndexCleanupRules.UnusedIndex, rec.ConsolidationRule);
        Assert.Equal("ALTER INDEX [IX_Unused] ON [TestDb].[dbo].[T] DISABLE;", rec.Script);
        Assert.False(r.UptimeWarning);
    }

    [Fact]
    public void Unused_UptimeUnder14Days_CarriesWarningLabel()
    {
        var r = IndexCleanupAnalyzer.Analyze(new[] { Idx("IX_Unused", "[a]") }, Opts(uptimeDays: 10));

        var rec = Assert.Single(r.Recommendations);
        Assert.Equal(IndexCleanupRules.UnusedIndexUptimeWarning, rec.ConsolidationRule);
        Assert.Contains("uptime < 14 days", rec.ConsolidationRule);
        Assert.True(r.UptimeWarning);
        Assert.False(r.DedupeOnlyApplied);
    }

    [Fact]
    public void Unused_UptimeUnder7Days_AutoDedupeSkipsUnused()
    {
        var r = IndexCleanupAnalyzer.Analyze(new[] { Idx("IX_Unused", "[a]") }, Opts(uptimeDays: 5));

        Assert.Empty(r.Recommendations);          // unused not flagged under auto-dedupe
        Assert.True(r.DedupeOnlyApplied);
    }

    [Fact]
    public void Unused_DedupeOnlyOption_SkipsUnused()
    {
        var r = IndexCleanupAnalyzer.Analyze(new[] { Idx("IX_Unused", "[a]") }, Opts(dedupeOnly: true));
        Assert.Empty(r.Recommendations);
    }

    [Fact]
    public void Unused_ClusteredIndex_NeverFlagged()
    {
        // A clustered index with no reads must never be "unused".
        var r = IndexCleanupAnalyzer.Analyze(new[] { Idx("PK_T", "[a]", indexId: 1, type: "CLUSTERED", primaryKey: true) }, Opts());
        Assert.DoesNotContain(r.Recommendations, x => x.Action == IndexCleanupAction.Disable);
    }

    // ── Rule 2: Exact Duplicate ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExactDuplicate_DisablesLowerPriorityKeepsHigher()
    {
        var keeper = Idx("IX_Keep", "[a], [b]", indexId: 2, includes: "[c]", seeks: 10, scans: 5);
        var loser = Idx("IX_Drop", "[a], [b]", indexId: 3, includes: "[c]", seeks: 5);

        var r = IndexCleanupAnalyzer.Analyze(new[] { keeper, loser }, Opts());

        var rec = Assert.Single(r.Recommendations);
        Assert.Equal("IX_Drop", rec.IndexName);
        Assert.Equal(IndexCleanupAction.Disable, rec.Action);
        Assert.Equal(IndexCleanupRules.ExactDuplicate, rec.ConsolidationRule);
        Assert.Equal("IX_Keep", rec.TargetIndexName);
        Assert.Contains("exact duplicate", rec.AdditionalInfo);
    }

    [Fact]
    public void ExactDuplicate_TieBreaksOnIndexName()
    {
        // Identical priority (both seeks + includes) → the alphabetically-first name is the keeper.
        var a = Idx("IX_AAA", "[a]", indexId: 2, includes: "[c]", seeks: 10);
        var b = Idx("IX_BBB", "[a]", indexId: 3, includes: "[c]", seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { b, a }, Opts());

        var rec = Assert.Single(r.Recommendations);
        Assert.Equal("IX_BBB", rec.IndexName);   // BBB disabled, AAA kept
        Assert.Equal("IX_AAA", rec.TargetIndexName);
    }

    // ── Reverse Duplicate (review only — never auto-disabled; mirrors adversarial test 9a) ──────────

    [Fact]
    public void ReverseDuplicate_ReversedColumnOrder_IsReviewNotDisabled()
    {
        // (a, b) vs (b, a): same column set, different leading column → serve different queries.
        var ab = Idx("IX_ab", "[a], [b]", indexId: 2, seeks: 10);
        var ba = Idx("IX_ba", "[b], [a]", indexId: 3, seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { ab, ba }, Opts());

        Assert.DoesNotContain(r.Recommendations, x => x.Action == IndexCleanupAction.Disable);

        var review = r.Recommendations.First(x => x.IndexName == "IX_ab");
        Assert.Equal(IndexCleanupResultKind.Review, review.ResultKind);
        Assert.Equal(IndexCleanupRules.ReverseDuplicate, review.ConsolidationRule);
        Assert.Equal(IndexCleanupAction.Keep, review.Action);
        Assert.Equal("", review.Script);   // informational — no destructive script
    }

    [Fact]
    public void ReverseDuplicate_InvertedDirection_IsReviewNotDisabled()
    {
        // (a ASC) vs (a DESC): functionally scannable both ways, but sp_IndexCleanup does not auto-drop them.
        var asc = Idx("IX_asc", "[a]", indexId: 2, seeks: 10);
        var desc = Idx("IX_desc", "[a] DESC", indexId: 3, seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { asc, desc }, Opts());

        Assert.DoesNotContain(r.Recommendations, x => x.Action == IndexCleanupAction.Disable);
        Assert.Contains(r.Recommendations, x => x.ResultKind == IndexCleanupResultKind.Review && x.ConsolidationRule == IndexCleanupRules.ReverseDuplicate);
    }

    [Fact]
    public void ExactDuplicate_SameDescendingKeys_IsDisabled()
    {
        // Two byte-identical DESC indexes ARE exact duplicates (mirrors adversarial test 2a).
        var d1 = Idx("IX_d1", "[a] DESC", indexId: 2, seeks: 10, scans: 5);
        var d2 = Idx("IX_d2", "[a] DESC", indexId: 3, seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { d1, d2 }, Opts());
        Assert.Contains(r.Recommendations, x => x.Action == IndexCleanupAction.Disable && x.ConsolidationRule == IndexCleanupRules.ExactDuplicate);
    }

    // ── Equal Except For Filter (review only — never auto-disabled; mirrors adversarial test 10a) ───

    [Fact]
    public void EqualExceptForFilter_FilteredVsUnfiltered_IsReviewNotDisabled()
    {
        var unfiltered = Idx("IX_a", "[a]", indexId: 2, seeks: 10);
        var filtered = Idx("IX_a_filt", "[a]", indexId: 3, filter: "([status_code]=(1))", seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { unfiltered, filtered }, Opts());

        Assert.DoesNotContain(r.Recommendations, x => x.Action == IndexCleanupAction.Disable);
        var review = r.Recommendations.First(x => x.IndexName == "IX_a_filt" && x.ResultKind == IndexCleanupResultKind.Review);
        Assert.Equal(IndexCleanupRules.EqualExceptForFilter, review.ConsolidationRule);
        Assert.Contains("filter", review.AdditionalInfo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EqualExceptForFilter_DifferentFilters_AreReviewNotDisabled()
    {
        var a = Idx("IX_A", "[a]", indexId: 2, includes: "[b]", filter: "([a]>(0))", seeks: 10);
        var b = Idx("IX_B", "[a]", indexId: 3, includes: "[b]", filter: "([a]>(100))", seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { a, b }, Opts());

        Assert.DoesNotContain(r.Recommendations, x => x.Action == IndexCleanupAction.Disable);
        Assert.Equal(2, r.Recommendations.Count(x => x.ResultKind == IndexCleanupResultKind.Review));
    }

    // ── Rule 5: Key Duplicate ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void KeyDuplicate_SameKeysDifferentIncludes_MergesIntoKeeper()
    {
        var keeper = Idx("IX_Keep", "[a]", indexId: 2, includes: "[b]", seeks: 10, scans: 5);
        var loser = Idx("IX_Drop", "[a]", indexId: 3, includes: "[c]", seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { keeper, loser }, Opts());

        var keep = Rec(r, "IX_Keep");
        var drop = Rec(r, "IX_Drop");
        Assert.NotNull(keep);
        Assert.NotNull(drop);

        Assert.Equal(IndexCleanupAction.MergeIncludes, keep!.Action);
        Assert.Equal(IndexCleanupRules.KeyDuplicate, keep.ConsolidationRule);
        Assert.Contains("INCLUDE ([b], [c])", keep.Script);        // absorbed the loser's include
        Assert.Contains("DROP_EXISTING = ON", keep.Script);
        Assert.Equal("Supersedes IX_Drop", keep.SupersededBy);

        Assert.Equal(IndexCleanupAction.Disable, drop!.Action);
        Assert.Equal("IX_Keep", drop.TargetIndexName);
    }

    [Fact]
    public void KeyDuplicate_PrefersUniqueAsKeeper()
    {
        // Non-unique has higher usage priority, but the unique index must win.
        var uniqueIx = Idx("IX_Unique", "[a]", indexId: 2, includes: "[b]", unique: true, seeks: 1);
        var busyIx = Idx("IX_Busy", "[a]", indexId: 3, includes: "[c]", seeks: 99, scans: 99);

        var r = IndexCleanupAnalyzer.Analyze(new[] { busyIx, uniqueIx }, Opts());

        Assert.Equal(IndexCleanupAction.MergeIncludes, Rec(r, "IX_Unique")!.Action);
        Assert.Equal(IndexCleanupAction.Disable, Rec(r, "IX_Busy")!.Action);
    }

    // ── Rules 3/4/6: Key Subset / Superset ─────────────────────────────────────────────────────────

    [Fact]
    public void KeySubset_NarrowerPrefix_DisabledInFavorOfSuperset()
    {
        var narrow = Idx("IX_Narrow", "[a]", indexId: 2, includes: "[z]", seeks: 10);
        var wide = Idx("IX_Wide", "[a], [b]", indexId: 3, seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { narrow, wide }, Opts());

        var sub = Rec(r, "IX_Narrow");
        var sup = Rec(r, "IX_Wide");
        Assert.NotNull(sub);
        Assert.NotNull(sup);

        Assert.Equal(IndexCleanupAction.Disable, sub!.Action);
        Assert.Equal(IndexCleanupRules.KeySubset, sub.ConsolidationRule);
        Assert.Equal("IX_Wide", sub.TargetIndexName);

        Assert.Equal(IndexCleanupAction.MergeIncludes, sup!.Action);
        Assert.Equal(IndexCleanupRules.KeySuperset, sup.ConsolidationRule);
        Assert.Equal("[z]", sup.MissingIncludedColumns);          // subset's include merged into the superset
        Assert.Contains("INCLUDE ([z])", sup.Script);
        Assert.Equal("Supersedes IX_Narrow", sup.SupersededBy);
    }

    [Fact]
    public void KeySubset_UniqueNarrower_IsNotDisabled()
    {
        // A unique index on (A) enforces uniqueness a wider (A,B) index does not — never disable it.
        var uniqueNarrow = Idx("IX_UQ", "[a]", indexId: 2, unique: true, seeks: 10);
        var wide = Idx("IX_Wide", "[a], [b]", indexId: 3, seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { uniqueNarrow, wide }, Opts());

        Assert.DoesNotContain(r.Recommendations, x => x.IndexName == "IX_UQ" && x.Action == IndexCleanupAction.Disable);
    }

    [Fact]
    public void KeySubset_ChooseClosestSuperset()
    {
        // Two supersets that are NOT nested in each other (different 2nd key), so neither is itself
        // disabled — the narrow index must defer to the one with the fewest extra key columns.
        var narrow = Idx("IX_N", "[a]", indexId: 2, seeks: 10);
        var near = Idx("IX_Near", "[a], [b]", indexId: 3, seeks: 10);
        var far = Idx("IX_Far", "[a], [c], [d]", indexId: 4, seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { narrow, near, far }, Opts());

        Assert.Equal("IX_Near", Rec(r, "IX_N")!.TargetIndexName);  // fewest extra key columns
    }

    [Fact]
    public void KeySubset_ChainIsFlattenedToFinalSuperset()
    {
        var a = Idx("IX_A", "[a]", indexId: 2, seeks: 10);
        var b = Idx("IX_B", "[a], [b]", indexId: 3, seeks: 10);
        var c = Idx("IX_C", "[a], [b], [c]", indexId: 4, seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { a, b, c }, Opts());

        Assert.Equal("IX_C", Rec(r, "IX_A")!.TargetIndexName);   // A → B → C flattened to A → C
        Assert.Equal("IX_C", Rec(r, "IX_B")!.TargetIndexName);
        Assert.Equal(IndexCleanupAction.MergeIncludes, Rec(r, "IX_C")!.Action);
        Assert.Equal(IndexCleanupRules.KeySuperset, Rec(r, "IX_C")!.ConsolidationRule);
    }

    [Fact]
    public void KeySubset_SameFilter_IsDisabled()
    {
        // Filtered subset with a MATCHING filter → disabled (mirrors adversarial test 3c).
        var narrow = Idx("IX_n", "[a]", indexId: 2, filter: "([status_code]=(3))", seeks: 10);
        var wide = Idx("IX_w", "[a], [b]", indexId: 3, filter: "([status_code]=(3))", seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { narrow, wide }, Opts());

        Assert.Equal(IndexCleanupAction.Disable, Rec(r, "IX_n")!.Action);
        Assert.Equal(IndexCleanupRules.KeySubset, Rec(r, "IX_n")!.ConsolidationRule);
    }

    [Fact]
    public void KeySubset_DifferentFilter_IsNotSubset()
    {
        // Prefix key but a DIFFERENT filter → NOT a subset (mirrors adversarial test 3d).
        var narrow = Idx("IX_n", "[a]", indexId: 2, filter: "([status_code]=(1))", seeks: 10);
        var wide = Idx("IX_w", "[a], [b]", indexId: 3, filter: "([status_code]=(2))", seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { narrow, wide }, Opts());

        Assert.DoesNotContain(r.Recommendations, x => x.Action == IndexCleanupAction.Disable);
    }

    // ── Protections ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Protection_PrimaryKey_IsKeptNotDisabled()
    {
        var pk = Idx("PK_T", "[a]", indexId: 2, primaryKey: true, unique: true, seeks: 1);
        var dup = Idx("IX_Dup", "[a]", indexId: 3, seeks: 99, scans: 99);   // higher usage, but must not win

        var r = IndexCleanupAnalyzer.Analyze(new[] { pk, dup }, Opts());

        Assert.DoesNotContain(r.Recommendations, x => x.IndexName == "PK_T" && x.Action == IndexCleanupAction.Disable);
        Assert.Equal(IndexCleanupAction.Disable, Rec(r, "IX_Dup")!.Action);
    }

    [Fact]
    public void Protection_ForeignKeyReference_IsKeptNotDisabled()
    {
        var fkRef = Idx("UQ_Ref", "[a]", indexId: 2, unique: true, foreignKeyReference: true, seeks: 1);
        var dup = Idx("IX_Dup", "[a]", indexId: 3, seeks: 99, scans: 99);

        var r = IndexCleanupAnalyzer.Analyze(new[] { fkRef, dup }, Opts());

        Assert.DoesNotContain(r.Recommendations, x => x.IndexName == "UQ_Ref" && x.Action == IndexCleanupAction.Disable);
        Assert.Equal(IndexCleanupAction.Disable, Rec(r, "IX_Dup")!.Action);
    }

    [Fact]
    public void Protection_UniqueConstraint_ExcludedFromDedupe()
    {
        var uc = Idx("UQ_C", "[a]", indexId: 2, uniqueConstraint: true, unique: true, seeks: 10);
        var dup = Idx("IX_Dup", "[a]", indexId: 3, seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { uc, dup }, Opts());

        // The unique constraint is never disabled (out of the dedupe candidate set entirely).
        Assert.DoesNotContain(r.Recommendations, x => x.IndexName == "UQ_C" && x.Action == IndexCleanupAction.Disable);
    }

    [Fact]
    public void Protection_ForeignKeyReferencedUnused_IsNotDisabled()
    {
        // FK-referenced index with zero reads: must not be flagged unused (it backs referential integrity).
        var fkRef = Idx("UQ_Ref", "[a]", indexId: 2, unique: true, foreignKeyReference: true);

        var r = IndexCleanupAnalyzer.Analyze(new[] { fkRef }, Opts());
        Assert.Empty(r.Recommendations);
    }

    // ── Compression ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compression_UncompressedLargeIndex_IsCandidateWithSavingsBand()
    {
        var clustered = Idx("CX_T", "[a]", indexId: 1, type: "CLUSTERED", compression: "NONE", reservedMb: 10240m, seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { clustered }, Opts());

        var rec = Assert.Single(r.Recommendations);
        Assert.Equal(IndexCleanupResultKind.Compress, rec.ResultKind);
        Assert.True(rec.CanCompress);
        Assert.Equal("ALTER INDEX [CX_T] ON [TestDb].[dbo].[T] REBUILD WITH (FILLFACTOR = 100, SORT_IN_TEMPDB = ON, ONLINE = OFF, DATA_COMPRESSION = PAGE);", rec.Script);

        Assert.Equal(1, r.OverallRollup.CompressableIndexes);
        Assert.Equal(2m, Math.Round(r.OverallRollup.CompressionMinSavingsGb, 4));   // 10 GB * 0.20
        Assert.Equal(6m, Math.Round(r.OverallRollup.CompressionMaxSavingsGb, 4));   // 10 GB * 0.60
    }

    [Fact]
    public void Compression_AlreadyPageCompressed_NotCandidate()
    {
        var ix = Idx("CX_T", "[a]", indexId: 1, type: "CLUSTERED", compression: "PAGE", reservedMb: 10240m, seeks: 10);
        var r = IndexCleanupAnalyzer.Analyze(new[] { ix }, Opts());
        Assert.Empty(r.Recommendations);
        Assert.Equal(0, r.OverallRollup.CompressableIndexes);
    }

    [Fact]
    public void Compression_BelowMinSize_Excluded()
    {
        var ix = Idx("CX_T", "[a]", indexId: 1, type: "CLUSTERED", compression: "NONE", reservedMb: 512m, seeks: 10);
        var r = IndexCleanupAnalyzer.Analyze(new[] { ix }, Opts(minSizeGb: 1m));   // 0.5 GB < 1 GB
        Assert.Empty(r.Recommendations);
    }

    [Fact]
    public void Compression_EditionUnsupported_Excluded()
    {
        var ix = Idx("CX_T", "[a]", indexId: 1, type: "CLUSTERED", compression: "NONE", reservedMb: 10240m, seeks: 10);
        var r = IndexCleanupAnalyzer.Analyze(new[] { ix }, Opts(supportsCompression: false));
        Assert.Empty(r.Recommendations);
    }

    [Fact]
    public void Compression_PartitionedIndex_UsesPartitionAll()
    {
        var ix = Idx("CX_T", "[a]", indexId: 1, type: "CLUSTERED", compression: "NONE", reservedMb: 10240m, partitionCount: 4, seeks: 10);
        var r = IndexCleanupAnalyzer.Analyze(new[] { ix }, Opts());
        Assert.Contains("REBUILD PARTITION = ALL", Assert.Single(r.Recommendations).Script);
    }

    [Fact]
    public void Compression_OnlineSupported_EmitsOnlineOn()
    {
        var ix = Idx("CX_T", "[a]", indexId: 1, type: "CLUSTERED", compression: "NONE", reservedMb: 10240m, optimizeSequentialKey: true, seeks: 10);
        var r = IndexCleanupAnalyzer.Analyze(new[] { ix }, Opts(supportsOnline: true));
        var rec = Assert.Single(r.Recommendations);
        Assert.Contains("ONLINE = ON", rec.Script);
        Assert.Contains("OPTIMIZE_FOR_SEQUENTIAL_KEY = ON", rec.Script);
    }

    // ── Reclaimable roll-up ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rollup_CombinesDisabledFullSizeAndCompressionBand()
    {
        var unused = Idx("IX_Unused", "[b]", indexId: 2, compression: "PAGE", reservedMb: 5120m);          // 5 GB, disabled → full reclaim
        var keptCompressible = Idx("CX_T", "[a]", indexId: 1, type: "CLUSTERED", compression: "NONE", reservedMb: 10240m, seeks: 10); // 10 GB kept → 2-6 GB

        var r = IndexCleanupAnalyzer.Analyze(new[] { unused, keptCompressible }, Opts());

        var roll = r.OverallRollup;
        Assert.Equal(1, roll.UnusedIndexes);
        Assert.Equal(1, roll.IndexesToDisable);
        Assert.Equal(5m, Math.Round(roll.UnusedSizeGb, 4));
        Assert.Equal(1, roll.CompressableIndexes);
        Assert.Equal(2m, Math.Round(roll.CompressionMinSavingsGb, 4));
        Assert.Equal(6m, Math.Round(roll.CompressionMaxSavingsGb, 4));
        Assert.Equal(7m, Math.Round(roll.TotalMinSavingsGb, 4));   // 5 + 2
        Assert.Equal(11m, Math.Round(roll.TotalMaxSavingsGb, 4));  // 5 + 6
    }

    // ── Reconstructed T-SQL ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OriginalDefinition_ReconstructsCreateFromCapturedStrings()
    {
        var ix = Idx("IX_x", "[a], [b] DESC", indexId: 2, includes: "[c], [d]", filter: "([a]>(0))");

        var r = IndexCleanupAnalyzer.Analyze(new[] { ix }, Opts());
        var rec = Assert.Single(r.Recommendations);

        Assert.Equal(
            "CREATE NONCLUSTERED INDEX [IX_x] ON [TestDb].[dbo].[T] ([a], [b] DESC) INCLUDE ([c], [d]) WHERE ([a]>(0));",
            rec.OriginalIndexDefinition);
    }

    [Fact]
    public void MergeScript_HasCanonicalDropExistingForm()
    {
        var keeper = Idx("IX_Keep", "[a]", indexId: 2, includes: "[b]", seeks: 10, scans: 5);
        var loser = Idx("IX_Drop", "[a]", indexId: 3, includes: "[c]", seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { keeper, loser }, Opts());

        Assert.Equal(
            "CREATE INDEX [IX_Keep] ON [TestDb].[dbo].[T] ([a]) INCLUDE ([b], [c]) WITH (DROP_EXISTING = ON, FILLFACTOR = 100, SORT_IN_TEMPDB = ON, ONLINE = OFF);",
            Rec(r, "IX_Keep")!.Script);
    }

    [Fact]
    public void QuoteName_EscapesClosingBracketsInObjectNames()
    {
        var ix = Idx("IX]evil", "[a]", indexId: 2, table: "Weird]Table");
        var r = IndexCleanupAnalyzer.Analyze(new[] { ix }, Opts());
        var rec = Assert.Single(r.Recommendations);
        Assert.Equal("ALTER INDEX [IX]]evil] ON [TestDb].[dbo].[Weird]]Table] DISABLE;", rec.Script);
    }

    // ── Input shaping ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NonRowstoreAndDisabled_AreExcludedFromAnalysis()
    {
        var heap = Idx("Heap", null, indexId: 0, type: "HEAP");
        var columnstore = Idx("CS", "[a]", indexId: 2, type: "NONCLUSTERED COLUMNSTORE", compression: "COLUMNSTORE");
        var disabled = new IndexCleanupIndexInput
        {
            DatabaseName = "TestDb", SchemaName = "dbo", TableName = "T", ObjectId = 1, IndexId = 4,
            IndexName = "IX_Disabled", IndexTypeDesc = "NONCLUSTERED", KeyColumns = "[a]", IsDisabled = true,
        };

        var r = IndexCleanupAnalyzer.Analyze(new[] { heap, columnstore, disabled }, Opts());

        Assert.Empty(r.Recommendations);
        Assert.Equal(0, r.OverallRollup.IndexCount);
    }

    [Fact]
    public void MultipleDatabases_ProduceSeparateRollups()
    {
        var db1 = Idx("IX_1", "[a]", indexId: 2, database: "DbOne", objectId: 1);
        var db2 = Idx("IX_2", "[a]", indexId: 2, database: "DbTwo", objectId: 2);

        var r = IndexCleanupAnalyzer.Analyze(new[] { db1 with { DatabaseId = 1 }, db2 with { DatabaseId = 2 } }, Opts());

        Assert.Equal(2, r.DatabaseRollups.Count);
        Assert.Contains(r.DatabaseRollups, x => x.DatabaseName == "DbOne");
        Assert.Contains(r.DatabaseRollups, x => x.DatabaseName == "DbTwo");
        Assert.Equal(2, r.OverallRollup.IndexesToDisable);
    }

    [Fact]
    public void CrossTableIsolation_IdenticalIndexesOnDifferentObjects_NotFlagged()
    {
        // Identical indexes on DIFFERENT tables (objects) must never dedupe each other (mirrors test 7a).
        var t1 = Idx("IX_same", "[a]", indexId: 2, objectId: 10, table: "T1", seeks: 10);
        var t2 = Idx("IX_same", "[a]", indexId: 2, objectId: 20, table: "T2", seeks: 10);

        var r = IndexCleanupAnalyzer.Analyze(new[] { t1, t2 }, Opts());
        Assert.DoesNotContain(r.Recommendations, x => x.Action == IndexCleanupAction.Disable);
    }

    [Fact]
    public void MinRows_SmallTableIsSkipped()
    {
        var ix = Idx("IX_Small", "[a]", indexId: 2, rows: 50);
        var r = IndexCleanupAnalyzer.Analyze(new[] { ix }, Opts(minRows: 1000));
        Assert.Empty(r.Recommendations);
    }

    [Fact]
    public void Notes_SurfaceOutOfScopeRules()
    {
        var r = IndexCleanupAnalyzer.Analyze(new[] { Idx("IX_Unused", "[a]") }, Opts());
        Assert.Contains(r.Notes, n => n.Contains("Unique Constraint Replacement") && n.Contains("Same Keys Different Order"));
    }
}

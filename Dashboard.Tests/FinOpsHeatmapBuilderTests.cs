/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using PerformanceMonitor.Common;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// Tests the shared, app-agnostic FinOps heatmap shaping (#1138): the top-N-by-growth ranking and the
/// long→matrix pivot that both Dashboard and Lite depend on. (Phase-2 color-scale math is exercised by the
/// ColumnLogIntensities tests below.) Pure functions, no database.
/// </summary>
public class FinOpsHeatmapBuilderTests
{
    private static readonly DateTime D1 = new(2026, 6, 1);
    private static readonly DateTime D2 = new(2026, 6, 2);
    private static readonly DateTime D3 = new(2026, 6, 3);

    // ── RankTopGrowers ──

    [Fact]
    public void RankTopGrowers_OrdersByGrowthDescending_TakesTopN()
    {
        var ranked = FinOpsHeatmapBuilder.RankTopGrowers(
            new[] { ("a", 10.0), ("b", 50.0), ("c", 30.0), ("d", 5.0) }, topN: 2);

        Assert.Equal(new[] { "b", "c" }, ranked);
    }

    [Fact]
    public void RankTopGrowers_TieBreaksOnKeyOrdinal_ForDeterminism()
    {
        var ranked = FinOpsHeatmapBuilder.RankTopGrowers(
            new[] { ("z", 10.0), ("a", 10.0), ("m", 10.0) }, topN: 2);

        Assert.Equal(new[] { "a", "m" }, ranked);
    }

    [Fact]
    public void RankTopGrowers_TopNExceedsCount_ReturnsAll()
    {
        var ranked = FinOpsHeatmapBuilder.RankTopGrowers(
            new[] { ("a", 1.0), ("b", 2.0) }, topN: 10);

        Assert.Equal(new[] { "b", "a" }, ranked);
    }

    // ── BuildMatrix (long → dense pivot) ──

    [Fact]
    public void BuildMatrix_PivotsToRowsByDays_WithMissingCellsZero()
    {
        var samples = new[]
        {
            new FinOpsObjectDaySample("big", D1, 100),
            new FinOpsObjectDaySample("big", D2, 200),
            new FinOpsObjectDaySample("small", D1, 10),
            // "small" has no D2 sample — that cell must stay 0.
        };

        // Rows bottom-to-top: index 0 = "small" (bottom), index 1 = "big" (top).
        var matrix = FinOpsHeatmapBuilder.BuildMatrix(new[] { "small", "big" }, samples);

        Assert.Equal(new[] { "small", "big" }, matrix.RowLabels);
        Assert.Equal(new[] { D1, D2 }, matrix.Days); // ascending
        Assert.Equal(10, matrix.Intensities[0, 0]);  // small / D1
        Assert.Equal(0, matrix.Intensities[0, 1]);   // small / D2 (missing)
        Assert.Equal(100, matrix.Intensities[1, 0]); // big / D1
        Assert.Equal(200, matrix.Intensities[1, 1]); // big / D2
    }

    [Fact]
    public void BuildMatrix_IgnoresSamplesNotInRowKeys()
    {
        var samples = new[]
        {
            new FinOpsObjectDaySample("a", D1, 5),
            new FinOpsObjectDaySample("ghost", D1, 999),
        };

        var matrix = FinOpsHeatmapBuilder.BuildMatrix(new[] { "a" }, samples);

        Assert.Single(matrix.RowLabels);
        Assert.Equal(5, matrix.Intensities[0, 0]);
        // "ghost" introduced no extra row; only its day survives as a column.
        Assert.Equal(1, matrix.Intensities.GetLength(0));
    }

    [Fact]
    public void BuildMatrix_SumsDuplicateKeyDayPairs()
    {
        var samples = new[]
        {
            new FinOpsObjectDaySample("a", D1, 5),
            new FinOpsObjectDaySample("a", D1, 7),
        };

        var matrix = FinOpsHeatmapBuilder.BuildMatrix(new[] { "a" }, samples);

        Assert.Equal(12, matrix.Intensities[0, 0]);
    }

    [Fact]
    public void BuildMatrix_OrdersDaysAscendingRegardlessOfInputOrder()
    {
        var samples = new[]
        {
            new FinOpsObjectDaySample("a", D3, 3),
            new FinOpsObjectDaySample("a", D1, 1),
            new FinOpsObjectDaySample("a", D2, 2),
        };

        var matrix = FinOpsHeatmapBuilder.BuildMatrix(new[] { "a" }, samples);

        Assert.Equal(new[] { D1, D2, D3 }, matrix.Days);
        Assert.Equal(1, matrix.Intensities[0, 0]);
        Assert.Equal(2, matrix.Intensities[0, 1]);
        Assert.Equal(3, matrix.Intensities[0, 2]);
    }

    [Fact]
    public void BuildMatrix_NoSamples_IsEmpty()
    {
        var matrix = FinOpsHeatmapBuilder.BuildMatrix(new[] { "a" }, Array.Empty<FinOpsObjectDaySample>());
        Assert.True(matrix.IsEmpty);
    }
}

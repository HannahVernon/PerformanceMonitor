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
using PerformanceMonitor.Ui;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins <see cref="ServerOverviewSort"/> — the pure, total-order comparer that orders the NOC Overview tiles
/// in BOTH apps (Lite and the Darling viewer). Mirror of Lite.Tests' copy (the ChartStyle precedent: the
/// shared pure function is tested identically in each suite). Uses a local <c>Row</c> test-double because
/// each app binds its own unrelated ServerSummaryItem type; only the three sorted members matter.
/// </summary>
public class ServerOverviewSortTests
{
    /// <summary>Stand-in for each app's ServerSummaryItem — CPU% (the alert-band value), display name, id.</summary>
    private sealed record Row(double? Cpu, string Name, int Id);

    private static List<Row> Order(IEnumerable<Row> rows, ServerOverviewSortMode mode) =>
        ServerOverviewSort.Order(rows, mode, r => r.Cpu, r => r.Name, r => r.Id);

    [Fact]
    public void Cpu_OrdersByCpuDescending()
    {
        var rows = new[]
        {
            new Row(12.0, "b", 1),
            new Row(90.0, "a", 2),
            new Row(45.0, "c", 3),
        };

        var ordered = Order(rows, ServerOverviewSortMode.Cpu);

        Assert.Equal(new[] { 2, 3, 1 }, ordered.Select(r => r.Id));
    }

    [Fact]
    public void Cpu_NoSampleServersSinkLast_ThenOrderedByName()
    {
        var rows = new[]
        {
            new Row(null, "zeta", 1),
            new Row(5.0, "busy", 2),
            new Row(null, "alpha", 3),
        };

        var ordered = Order(rows, ServerOverviewSortMode.Cpu);

        // Has-sample first (busy), then the two null-CPU servers by name (alpha before zeta).
        Assert.Equal(new[] { 2, 3, 1 }, ordered.Select(r => r.Id));
    }

    [Fact]
    public void Cpu_RealZeroPercent_OutranksNoSample()
    {
        // A genuine 0.0% has a sample and must sort ABOVE a null (no-sample) server, proving the `?? 0.0`
        // fallback only ever bites inside the already-sunk no-sample partition — a real 0.0% is unaffected.
        var rows = new[]
        {
            new Row(null, "nosample", 1),
            new Row(0.0, "idle", 2),
        };

        var ordered = Order(rows, ServerOverviewSortMode.Cpu);

        Assert.Equal(new[] { 2, 1 }, ordered.Select(r => r.Id));
    }

    [Fact]
    public void Cpu_TiesBreakByName_CaseInsensitive()
    {
        var rows = new[]
        {
            new Row(50.0, "Bravo", 1),
            new Row(50.0, "alpha", 2),
            new Row(50.0, "Charlie", 3),
        };

        var ordered = Order(rows, ServerOverviewSortMode.Cpu);

        // Equal CPU -> case-insensitive name asc: alpha, Bravo, Charlie.
        Assert.Equal(new[] { 2, 1, 3 }, ordered.Select(r => r.Id));
    }

    [Fact]
    public void Cpu_SameCpuAndName_BreaksByStableId()
    {
        var rows = new[]
        {
            new Row(50.0, "same", 30),
            new Row(50.0, "same", 10),
            new Row(50.0, "SAME", 20),
        };

        var ordered = Order(rows, ServerOverviewSortMode.Cpu);

        // Equal CPU, name equal case-insensitively -> id ascending.
        Assert.Equal(new[] { 10, 20, 30 }, ordered.Select(r => r.Id));
    }

    [Fact]
    public void Cpu_IsATotalOrder_IndependentOfInputEnumeration()
    {
        var rows = new[]
        {
            new Row(50.0, "same", 10),
            new Row(50.0, "same", 20),
            new Row(null, "x", 30),
            new Row(70.0, "hot", 40),
        };

        var forward = Order(rows, ServerOverviewSortMode.Cpu).Select(r => r.Id).ToList();
        var reversed = Order(rows.Reverse(), ServerOverviewSortMode.Cpu).Select(r => r.Id).ToList();

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void Name_OrdersByNameAscending_IgnoringCpu()
    {
        var rows = new[]
        {
            new Row(99.0, "charlie", 1),
            new Row(1.0, "Alpha", 2),
            new Row(null, "bravo", 3),
        };

        var ordered = Order(rows, ServerOverviewSortMode.Name);

        // Name mode: case-insensitive name asc, regardless of CPU value or a null CPU sample.
        Assert.Equal(new[] { 2, 3, 1 }, ordered.Select(r => r.Id));
    }

    [Fact]
    public void Name_TiesBreakByStableId()
    {
        var rows = new[]
        {
            new Row(10.0, "dup", 3),
            new Row(20.0, "dup", 1),
            new Row(30.0, "dup", 2),
        };

        var ordered = Order(rows, ServerOverviewSortMode.Name);

        Assert.Equal(new[] { 1, 2, 3 }, ordered.Select(r => r.Id));
    }

    [Fact]
    public void Order_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(Order(Array.Empty<Row>(), ServerOverviewSortMode.Cpu));
        Assert.Empty(Order(Array.Empty<Row>(), ServerOverviewSortMode.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    [InlineData("5")]
    public void ParseMode_UnrecognisedOrEmpty_FallsBackToCpu(string? token)
    {
        Assert.Equal(ServerOverviewSortMode.Cpu, ServerOverviewSort.ParseMode(token));
    }

    [Theory]
    [InlineData("Cpu", ServerOverviewSortMode.Cpu)]
    [InlineData("cpu", ServerOverviewSortMode.Cpu)]
    [InlineData("Name", ServerOverviewSortMode.Name)]
    [InlineData("name", ServerOverviewSortMode.Name)]
    [InlineData("NAME", ServerOverviewSortMode.Name)]
    public void ParseMode_RecognisedTokens_AreCaseInsensitive(string token, ServerOverviewSortMode expected)
    {
        Assert.Equal(expected, ServerOverviewSort.ParseMode(token));
    }

    [Theory]
    [InlineData(ServerOverviewSortMode.Cpu)]
    [InlineData(ServerOverviewSortMode.Name)]
    public void ToToken_RoundTripsThroughParseMode(ServerOverviewSortMode mode)
    {
        Assert.Equal(mode, ServerOverviewSort.ParseMode(ServerOverviewSort.ToToken(mode)));
    }

    [Fact]
    public void Default_IsCpu()
    {
        Assert.Equal(ServerOverviewSortMode.Cpu, ServerOverviewSort.Default);
    }
}

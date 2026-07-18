/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// #1556: the shared enumeration driver's control flow, via fake delegates (no SQL, no store). Pins the
/// properties the field incident hinged on: each item is FLUSHED separately (no cross-item accumulation),
/// empty batches open no write, a per-item failure is skipped while the rest continue, an OutOfMemoryException
/// is rethrown rather than swallowed, cancellation propagates, and the warn hook sees each item's batch count.
/// </summary>
public sealed class EnumeratedCollectorDriverTests
{
    [Fact]
    public async Task RunAsync_FlushesEachItemSeparately_WithNoCrossItemAccumulation()
    {
        var items = new[] { "a", "b", "c" };
        var writtenBatches = new List<List<int>>();

        var result = await EnumeratedCollectorDriver.RunAsync<int>(
            items,
            perItemWatermark: null,
            readItem: (item, ct) => Task.FromResult(new List<int> { item[0] }),
            writeBatch: (batch, ct) => { writtenBatches.Add(batch.ToList()); return Task.CompletedTask; },
            onItemComplete: (item, count, sqlMs, storageMs) => { },
            onItemError: (item, ex) => { },
            CancellationToken.None);

        /* One flush per item, and each flushed batch is exactly that item's rows — never an accumulation
           of the ones before it (the byte-blow-up the driver exists to prevent). */
        Assert.Equal(3, writtenBatches.Count);
        Assert.All(writtenBatches, b => Assert.Single(b));
        Assert.Equal(new[] { (int)'a', (int)'b', (int)'c' }, writtenBatches.Select(b => b[0]));
        Assert.Equal(3, result.Rows);
    }

    [Fact]
    public async Task RunAsync_EmptyBatch_OpensNoWrite_ButStillWarns()
    {
        var items = new[] { "a", "b", "c" };
        var writeCount = 0;
        var completed = new List<(string Item, int Count)>();

        var result = await EnumeratedCollectorDriver.RunAsync<int>(
            items,
            perItemWatermark: null,
            readItem: (item, ct) => Task.FromResult(item == "b" ? new List<int>() : new List<int> { 1 }),
            writeBatch: (batch, ct) => { writeCount++; return Task.CompletedTask; },
            onItemComplete: (item, count, sqlMs, storageMs) => completed.Add((item, count)),
            onItemError: (item, ex) => { },
            CancellationToken.None);

        /* Empty "b" contributes no COPY/appender and no rows, but the warn hook still ran for it (count 0). */
        Assert.Equal(2, writeCount);
        Assert.Equal(2, result.Rows);
        Assert.Equal(3, completed.Count);
        Assert.Equal(0, completed.Single(c => c.Item == "b").Count);
    }

    [Fact]
    public async Task RunAsync_PerItemError_IsSkipped_AndTheRestContinue()
    {
        var items = new[] { "a", "b", "c" };
        var errors = new List<string>();
        var completed = new List<string>();
        var writeCount = 0;

        var result = await EnumeratedCollectorDriver.RunAsync<int>(
            items,
            perItemWatermark: null,
            readItem: (item, ct) => item == "b"
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(new List<int> { 1 }),
            writeBatch: (batch, ct) => { writeCount++; return Task.CompletedTask; },
            onItemComplete: (item, count, sqlMs, storageMs) => completed.Add(item),
            onItemError: (item, ex) => errors.Add(item),
            CancellationToken.None);

        /* "b" is skipped (logged via onItemError, never onItemComplete, never written); "a"+"c" still land. */
        Assert.Equal(new[] { "b" }, errors);
        Assert.Equal(new[] { "a", "c" }, completed);
        Assert.Equal(2, writeCount);
        Assert.Equal(2, result.Rows);
    }

    [Fact]
    public async Task RunAsync_OutOfMemory_IsRethrown_NotSwallowedAsASkip()
    {
        var items = new[] { "a", "b", "c" };
        var errors = new List<string>();
        var writeCount = 0;

        await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
            await EnumeratedCollectorDriver.RunAsync<int>(
                items,
                perItemWatermark: null,
                readItem: (item, ct) => item == "b"
                    ? throw new OutOfMemoryException()
                    : Task.FromResult(new List<int> { 1 }),
                writeBatch: (batch, ct) => { writeCount++; return Task.CompletedTask; },
                onItemComplete: (item, count, sqlMs, storageMs) => { },
                onItemError: (item, ex) => errors.Add(item),
                CancellationToken.None));

        /* OOM is fatal, not a per-item skip: it is NOT filed through onItemError, and "c" is never reached.
           "a" was flushed before "b" blew up (commit-1..N-1). */
        Assert.Empty(errors);
        Assert.Equal(1, writeCount);
    }

    [Fact]
    public async Task RunAsync_ReadItemThrowsOperationCanceled_Propagates()
    {
        var items = new[] { "a" };
        var errors = new List<string>();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await EnumeratedCollectorDriver.RunAsync<int>(
                items,
                perItemWatermark: null,
                readItem: (item, ct) => throw new OperationCanceledException(),
                writeBatch: (batch, ct) => Task.CompletedTask,
                onItemComplete: (item, count, sqlMs, storageMs) => { },
                onItemError: (item, ex) => errors.Add(item),
                CancellationToken.None));

        /* Cancellation is not a per-item skip either — the filtered catch lets it through. */
        Assert.Empty(errors);
    }

    [Fact]
    public async Task RunAsync_AlreadyCancelledToken_ThrowsBeforeAnyItem()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reads = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await EnumeratedCollectorDriver.RunAsync<int>(
                new[] { "a", "b" },
                perItemWatermark: null,
                readItem: (item, ct) => { reads++; return Task.FromResult(new List<int> { 1 }); },
                writeBatch: (batch, ct) => Task.CompletedTask,
                onItemComplete: (item, count, sqlMs, storageMs) => { },
                onItemError: (item, ex) => { },
                cts.Token));

        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task RunAsync_PerItemWatermark_RunsBeforeEachRead()
    {
        var order = new List<string>();

        await EnumeratedCollectorDriver.RunAsync<int>(
            new[] { "a", "b" },
            perItemWatermark: (item, ct) => { order.Add($"wm:{item}"); return Task.CompletedTask; },
            readItem: (item, ct) => { order.Add($"read:{item}"); return Task.FromResult(new List<int>()); },
            writeBatch: (batch, ct) => Task.CompletedTask,
            onItemComplete: (item, count, sqlMs, storageMs) => { },
            onItemError: (item, ex) => { },
            CancellationToken.None);

        /* The per-database cutoff (watermark + clamp) is computed before that database's query is built. */
        Assert.Equal(new[] { "wm:a", "read:a", "wm:b", "read:b" }, order);
    }

    [Fact]
    public async Task RunAsync_WarnHook_ReceivesEachItemsBatchCount()
    {
        var completed = new List<(string Item, int Count)>();

        var result = await EnumeratedCollectorDriver.RunAsync<int>(
            new[] { "a", "b" },
            perItemWatermark: null,
            readItem: (item, ct) => Task.FromResult(item == "a"
                ? new List<int> { 1, 2, 3 }
                : new List<int> { 9 }),
            writeBatch: (batch, ct) => Task.CompletedTask,
            onItemComplete: (item, count, sqlMs, storageMs) => completed.Add((item, count)),
            onItemError: (item, ex) => { },
            CancellationToken.None);

        /* The count is the per-database delta the host compares to the row cap; under per-item flush it is
           exactly the batch size. */
        Assert.Equal((3, 1), (completed.Single(c => c.Item == "a").Count, completed.Single(c => c.Item == "b").Count));
        Assert.Equal(4, result.Rows);
    }
}

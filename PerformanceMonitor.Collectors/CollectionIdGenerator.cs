/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// Generates unique collection ids: a per-process counter seeded from the startup timestamp's
/// ticks, atomically incremented — monotonic within a process and jumping forward across
/// restarts so ids never collide in practice. The shared idiom both hosts stamp prefix ids with
/// (extracted verbatim from Lite's RemoteCollectorService.GenerateCollectionId).
/// </summary>
public static class CollectionIdGenerator
{
    private static long s_idCounter = DateTime.UtcNow.Ticks;

    public static long Next()
    {
        return Interlocked.Increment(ref s_idCounter);
    }
}

/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitor.Common;

/// <summary>
/// One per-snapshot CPU-scheduler trend point the pressure chart plots (Runnable / Blocked / Queued task
/// counts over the window). Implemented by both apps' trend types — Lite's <c>class</c> and Darling's
/// positional <c>record</c> — so the shared <c>CpuSchedulerChartRenderer</c> reads them uniformly.
/// </summary>
public interface ICpuSchedulerTrendPoint
{
    DateTime CollectionTime { get; }
    int RunnableTasks { get; }
    int BlockedTasks { get; }
    int QueuedRequests { get; }
}

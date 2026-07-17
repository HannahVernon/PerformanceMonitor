/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// #1556: the 24h catch-up clamp boundaries. A stale query_store watermark (a service down for days) must
/// not point its cutoff days into the past — that one cycle would try to pull the whole retained backlog and
/// drive the commit-limit blowout. The clamp floors a &gt;24h-stale watermark to now-24h; a fresh watermark
/// and a null watermark pass through untouched.
/// </summary>
public sealed class WatermarkPolicyTests
{
    private static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ClampCatchup_Null_StaysNull()
    {
        /* Null = nothing collected yet; the definition's documented first-run window applies, not a clamp. */
        Assert.Null(WatermarkPolicy.ClampCatchup(null, Now));
    }

    [Fact]
    public void ClampCatchup_WithinHorizon_ReturnedUnchanged()
    {
        /* A routine restart / brief outage: the watermark is minutes-to-hours old and never clamps. */
        var oneHourAgo = Now.AddHours(-1);
        Assert.Equal(oneHourAgo, WatermarkPolicy.ClampCatchup(oneHourAgo, Now));

        var justInside = Now - WatermarkPolicy.MaxCatchup + TimeSpan.FromSeconds(1);
        Assert.Equal(justInside, WatermarkPolicy.ClampCatchup(justInside, Now));
    }

    [Fact]
    public void ClampCatchup_ExactlyAtHorizon_NotClamped()
    {
        /* The floor is strict (< floor clamps): a watermark exactly 24h old is at the horizon, not past it. */
        var atHorizon = Now - WatermarkPolicy.MaxCatchup;
        Assert.Equal(atHorizon, WatermarkPolicy.ClampCatchup(atHorizon, Now));
    }

    [Fact]
    public void ClampCatchup_StalerThanHorizon_FlooredToNowMinus24h()
    {
        /* The field incident: a multi-day-old watermark is floored to now-24h so catch-up is bounded. */
        var floor = Now - WatermarkPolicy.MaxCatchup;

        Assert.Equal(floor, WatermarkPolicy.ClampCatchup(Now.AddDays(-3), Now));
        Assert.Equal(floor, WatermarkPolicy.ClampCatchup(Now.AddHours(-30), Now));

        var justPast = Now - WatermarkPolicy.MaxCatchup - TimeSpan.FromSeconds(1);
        Assert.Equal(floor, WatermarkPolicy.ClampCatchup(justPast, Now));
    }

    [Fact]
    public void ClampCatchup_FutureWatermark_ReturnedUnchanged()
    {
        /* A watermark ahead of now (clock skew) is not older than the floor, so it is left alone. */
        var future = Now.AddHours(1);
        Assert.Equal(future, WatermarkPolicy.ClampCatchup(future, Now));
    }

    [Fact]
    public void MaxCatchup_IsTwentyFourHours()
    {
        /* Drift tripwire: the horizon is a deliberate choice (routine outages never clamp; multi-day ones
           survive with a bounded, logged hole). */
        Assert.Equal(TimeSpan.FromHours(24), WatermarkPolicy.MaxCatchup);
    }
}

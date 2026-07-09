/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Darling.Viewer;
using PerformanceMonitor.Notifications;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the one-click "Silence This Server" / "Unsilence" logic (the sidebar shortcut over the multi-step
/// Manage Mute Rules dialog): the whole-server silence rule it writes and the predicate "Unsilence" uses to find
/// the rule(s) to remove. Pure domain logic (no WPF, no live Postgres) — the same discipline as the SQL pins.
/// The rule is keyed on the server's DISPLAY name because that's what the alert engine's mute context and the
/// alert rows carry (so the rule actually suppresses the alerts, matching the existing "Mute This Server").
/// </summary>
public sealed class ViewerServerSilenceTests
{
    [Fact]
    public void BuildServerSilenceRule_ScopesToServer_WithNoNarrowingPatterns_AndNoExpiry()
    {
        var rule = ViewerDataService.BuildServerSilenceRule("Prod SQL 1");

        Assert.Equal("Prod SQL 1", rule.ServerName);
        Assert.True(rule.Enabled);
        Assert.Null(rule.ExpiresAtUtc); /* indefinite — stays until Unsilence, mirroring Lite's per-server silence */
        Assert.Equal(ViewerDataService.ServerSilenceReason, rule.Reason);

        /* Every other pattern is null so the rule matches EVERY alert for the server. */
        Assert.Null(rule.MetricName);
        Assert.Null(rule.DatabasePattern);
        Assert.Null(rule.QueryTextPattern);
        Assert.Null(rule.WaitTypePattern);
        Assert.Null(rule.JobNamePattern);
    }

    [Fact]
    public void BuildServerSilenceRule_MatchesEveryAlertForThatServer_ButNotOtherServers()
    {
        var rule = ViewerDataService.BuildServerSilenceRule("Prod SQL 1");

        Assert.True(rule.Matches(new AlertMuteContext { ServerName = "Prod SQL 1", MetricName = "High CPU" }));
        Assert.True(rule.Matches(new AlertMuteContext { ServerName = "Prod SQL 1", MetricName = "Deadlocks Detected" }));
        Assert.False(rule.Matches(new AlertMuteContext { ServerName = "Prod SQL 2", MetricName = "High CPU" }));
    }

    [Fact]
    public void IsWholeServerSilence_TrueForABlanketServerRule_CaseInsensitiveOnTheName()
    {
        var rule = ViewerDataService.BuildServerSilenceRule("Prod SQL 1");

        Assert.True(ViewerDataService.IsWholeServerSilence(rule, "Prod SQL 1"));
        Assert.True(ViewerDataService.IsWholeServerSilence(rule, "prod sql 1")); /* name compare is case-insensitive */
    }

    [Fact]
    public void IsWholeServerSilence_FalseForADifferentServer()
    {
        var rule = ViewerDataService.BuildServerSilenceRule("Prod SQL 1");

        Assert.False(ViewerDataService.IsWholeServerSilence(rule, "Prod SQL 2"));
    }

    [Fact]
    public void IsWholeServerSilence_FalseForANarrowedRule_SoUnsilenceLeavesSpecificMutesAlone()
    {
        /* A rule the operator authored to mute only ONE metric on the server is NOT a whole-server silence, so
           "Unsilence" must not remove it. */
        var narrowed = new MuteRule { ServerName = "Prod SQL 1", MetricName = "High CPU" };

        Assert.False(ViewerDataService.IsWholeServerSilence(narrowed, "Prod SQL 1"));
    }

    [Fact]
    public void IsWholeServerSilence_FalseForAMetricOnlyRule_WithNoServerScope()
    {
        /* A metric-only (all-servers) mute has no ServerName, so it's never a per-server silence. */
        var metricOnly = new MuteRule { MetricName = "High CPU" };

        Assert.False(ViewerDataService.IsWholeServerSilence(metricOnly, "Prod SQL 1"));
    }
}

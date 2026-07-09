/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The opt-in network-endpoint primitives (darling-network-endpoints), all PURE + ungated: the
/// non-loopback classifier, the role normalizer, the pg_ctl <c>-o</c> arg-gen (listen/ssl), the
/// <c>ReconcilePgHba</c> marked-block reconcile, the firewall command builders, and the store
/// loopback-degrade decision (<c>ResolveNetworkExposure</c>). The live round-trips (an exposed
/// cluster actually accepts a TLS verify-full client, an off-CIDR client is refused, the mcp role is
/// denied the secret columns) are validated on DARLING01 and in the gated
/// <see cref="DarlingSecuritySplitLiveTests"/>.
/// </summary>
public sealed class DarlingNetworkTests
{
    /* ---- non-loopback classifier (DarlingNetwork.IsExposedListenAddress) ---- */

    [Theory]
    [InlineData("192.168.1.205", true)]  // a real LAN IP
    [InlineData("10.0.0.5", true)]
    [InlineData("0.0.0.0", true)]         // all interfaces
    [InlineData("::", true)]              // IPv6 all interfaces
    [InlineData("::1", true)]             // IPv6 loopback — the store's loopback handling is IPv4-127 only
    [InlineData("*", true)]               // not an IP -> exposed (store bind will degrade)
    [InlineData("localhost", true)]       // a name, not an IP -> exposed
    [InlineData("db.lan", true)]          // a hostname -> exposed
    [InlineData("127.0.0.1", false)]      // the canonical loopback = NOT exposed
    [InlineData("127.0.0.5", false)]      // anywhere in 127.0.0.0/8 = NOT exposed
    [InlineData("", false)]               // absent = the default loopback-only store
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsExposedListenAddress_TreatsAnythingButIpv4Loopback_AsExposed(string? listen, bool expected)
        => Assert.Equal(expected, DarlingNetwork.IsExposedListenAddress(listen));

    /* ---- role normalizer (DarlingNetwork.NormalizeNetworkRole) ---- */

    [Theory]
    [InlineData(null, "viewer")]   // absent -> the secure read-only default (D7)
    [InlineData("", "viewer")]
    [InlineData("   ", "viewer")]
    [InlineData("viewer", "viewer")]
    [InlineData("VIEWER", "viewer")]
    [InlineData(" Admin ", "admin")]
    [InlineData("admin", "admin")]
    public void NormalizeNetworkRole_DefaultsViewer_AcceptsAdminCaseInsensitively(string? role, string expected)
        => Assert.Equal(expected, DarlingNetwork.NormalizeNetworkRole(role));

    [Theory]
    [InlineData("darling")]     // NEVER the superuser
    [InlineData("postgres")]
    [InlineData("superuser")]
    [InlineData("root")]
    public void NormalizeNetworkRole_RejectsAnythingElse_ToNull(string role)
        => Assert.Null(DarlingNetwork.NormalizeNetworkRole(role));

    /* ---- listen/ssl -o arg-gen (DarlingManagedPostgres.BuildServerRuntimeOptions) ---- */

    [Fact]
    public void BuildServerRuntimeOptions_LoopbackOnly_WhenNoNetworkIp()
    {
        var opts = DarlingManagedPostgres.BuildServerRuntimeOptions(5641, null, null, null);

        /* Always the port + loopback listen; NO ssl, NO single quotes (Windows CRT arg hazard). */
        Assert.Equal("-p 5641 -c listen_addresses=127.0.0.1", opts);
        Assert.DoesNotContain("ssl", opts, StringComparison.Ordinal);
        Assert.DoesNotContain("'", opts, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildServerRuntimeOptions_Exposed_AddsNetworkIpAndSslTrio_ForwardSlashed_NoQuotes()
    {
        var opts = DarlingManagedPostgres.BuildServerRuntimeOptions(
            5641,
            "192.168.1.205",
            @"C:\ProgramData\PerformanceMonitorDarling\server.crt",
            @"C:\ProgramData\PerformanceMonitorDarling\server.key");

        /* Loopback FIRST, then the network IP appended (no space in the value). */
        Assert.Contains("-c listen_addresses=127.0.0.1,192.168.1.205", opts, StringComparison.Ordinal);

        /* The ssl trio, cert paths forward-slashed. */
        Assert.Contains("-c ssl=on", opts, StringComparison.Ordinal);
        Assert.Contains("-c ssl_cert_file=C:/ProgramData/PerformanceMonitorDarling/server.crt", opts, StringComparison.Ordinal);
        Assert.Contains("-c ssl_key_file=C:/ProgramData/PerformanceMonitorDarling/server.key", opts, StringComparison.Ordinal);

        /* Windows CRT hazards: only double quotes are metacharacters (single quotes corrupt a GUC), a
           space inside a value would make postgres re-split it, and backslashes must not survive. */
        Assert.DoesNotContain("'", opts, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", opts, StringComparison.Ordinal);

        /* A specific IP keeps the loopback prefix (the service connects over 127.0.0.1). */
        Assert.Contains("listen_addresses=127.0.0.1,192.168.1.205", opts, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void BuildServerRuntimeOptions_Wildcard_EmittedAlone_NoLoopbackPrefix(string wildcard)
    {
        /* A wildcard binds every interface; on Windows PG cannot ALSO bind 127.0.0.1 on the same port
           (WSAEADDRINUSE), so it must be emitted ALONE — never "127.0.0.1,<wildcard>". */
        var opts = DarlingManagedPostgres.BuildServerRuntimeOptions(5641, wildcard, null, null);

        Assert.Equal($"-p 5641 -c listen_addresses={wildcard}", opts);
        Assert.DoesNotContain($"127.0.0.1,{wildcard}", opts, StringComparison.Ordinal);
    }

    /* ---- pg_hba reconcile (DarlingManagedPostgres.ReconcilePgHba / BuildNetworkPgHbaLine) ---- */

    private const string SampleHba =
        "# TYPE  DATABASE  USER  ADDRESS  METHOD\n" +
        "host  all  all  127.0.0.1/32  scram-sha-256\n" +
        "host  all  all  ::1/128  scram-sha-256\n";

    [Fact]
    public void BuildNetworkPgHbaLine_IsHostssl_DarlingDb_ExactRole_NeverAllOrSuperuser()
    {
        Assert.Equal("hostssl darling viewer 192.168.1.0/24 scram-sha-256",
            DarlingManagedPostgres.BuildNetworkPgHbaLine("viewer", "192.168.1.0/24"));
        Assert.Equal("hostssl darling admin 10.0.0.0/8 scram-sha-256",
            DarlingManagedPostgres.BuildNetworkPgHbaLine("admin", "10.0.0.0/8"));

        var line = DarlingManagedPostgres.BuildNetworkPgHbaLine("viewer", "192.168.1.0/24");
        Assert.StartsWith("hostssl ", line, StringComparison.Ordinal);   // TLS-required, never plain host
        Assert.DoesNotContain(" all ", line, StringComparison.Ordinal);   // never database=all / user=all
    }

    [Fact]
    public void ReconcilePgHba_AppendsBlock_WhenAbsent_PreservingExistingLines()
    {
        var result = DarlingManagedPostgres.ReconcilePgHba(
            SampleHba, DarlingManagedPostgres.BuildNetworkPgHbaLine("viewer", "192.168.1.0/24"));

        /* The initdb loopback rules are preserved verbatim. */
        Assert.Contains("host  all  all  127.0.0.1/32  scram-sha-256", result, StringComparison.Ordinal);
        Assert.Contains("host  all  all  ::1/128  scram-sha-256", result, StringComparison.Ordinal);

        /* The marked block carries exactly the hostssl rule. */
        Assert.Contains(DarlingManagedPostgres.PgHbaBeginMarker, result, StringComparison.Ordinal);
        Assert.Contains(DarlingManagedPostgres.PgHbaEndMarker, result, StringComparison.Ordinal);
        Assert.Contains("hostssl darling viewer 192.168.1.0/24 scram-sha-256", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconcilePgHba_NarrowingReplacesTheBlock_NotAppends()
    {
        var wide = DarlingManagedPostgres.ReconcilePgHba(
            SampleHba, DarlingManagedPostgres.BuildNetworkPgHbaLine("viewer", "192.168.0.0/16"));
        var narrowed = DarlingManagedPostgres.ReconcilePgHba(
            wide, DarlingManagedPostgres.BuildNetworkPgHbaLine("viewer", "192.168.1.0/24"));

        Assert.Contains("hostssl darling viewer 192.168.1.0/24 scram-sha-256", narrowed, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.0.0/16", narrowed, StringComparison.Ordinal);

        /* Exactly ONE managed block after narrowing — replaced, not accumulated. */
        Assert.Equal(1, CountOccurrences(narrowed, DarlingManagedPostgres.PgHbaBeginMarker));
        Assert.Equal(1, CountOccurrences(narrowed, DarlingManagedPostgres.PgHbaEndMarker));
    }

    [Fact]
    public void ReconcilePgHba_RemovesBlock_WhenDisabled_PreservingNonMarkedLines()
    {
        var withBlock = DarlingManagedPostgres.ReconcilePgHba(
            SampleHba, DarlingManagedPostgres.BuildNetworkPgHbaLine("admin", "10.0.0.0/8"));
        var disabled = DarlingManagedPostgres.ReconcilePgHba(withBlock, null);

        /* The managed block (and its hostssl rule) is gone; the operator's own lines survive. */
        Assert.DoesNotContain(DarlingManagedPostgres.PgHbaBeginMarker, disabled, StringComparison.Ordinal);
        Assert.DoesNotContain("hostssl", disabled, StringComparison.Ordinal);
        Assert.Contains("host  all  all  127.0.0.1/32  scram-sha-256", disabled, StringComparison.Ordinal);
        Assert.Contains("host  all  all  ::1/128  scram-sha-256", disabled, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconcilePgHba_Idempotent_SameDesiredYieldsSameText()
    {
        var line = DarlingManagedPostgres.BuildNetworkPgHbaLine("viewer", "192.168.1.0/24");
        var once = DarlingManagedPostgres.ReconcilePgHba(SampleHba, line);
        var twice = DarlingManagedPostgres.ReconcilePgHba(once, line);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void ReconcilePgHba_DisableIsIdempotent_AndPreservesAManuallyAddedNonMarkedLine()
    {
        /* An operator's own hostssl rule OUTSIDE the marked block must survive a disable. */
        var operatorLine = "hostssl otherdb reporting 10.9.0.0/16 scram-sha-256\n";
        var withBoth = DarlingManagedPostgres.ReconcilePgHba(
            SampleHba + operatorLine, DarlingManagedPostgres.BuildNetworkPgHbaLine("viewer", "192.168.1.0/24"));

        var disabledOnce = DarlingManagedPostgres.ReconcilePgHba(withBoth, null);
        var disabledTwice = DarlingManagedPostgres.ReconcilePgHba(disabledOnce, null);

        Assert.Equal(disabledOnce, disabledTwice);
        Assert.Contains("hostssl otherdb reporting 10.9.0.0/16 scram-sha-256", disabledOnce, StringComparison.Ordinal);
        Assert.DoesNotContain("darling viewer", disabledOnce, StringComparison.Ordinal);
    }

    [Fact]
    public void NeedsPgHbaReconcile_SkipsUntouchedStore_ReconcilesDisableEdgeAndExposure()
    {
        /* Never-exposed store, not exposing now -> NO reconcile (don't rewrite an untouched operator file). */
        Assert.False(DarlingManagedPostgres.NeedsPgHbaReconcile(SampleHba, null));

        /* A disable edge: the managed block is still present -> reconcile (to remove it), even with desired=null. */
        var withBlock = DarlingManagedPostgres.ReconcilePgHba(
            SampleHba, DarlingManagedPostgres.BuildNetworkPgHbaLine("viewer", "192.168.1.0/24"));
        Assert.True(DarlingManagedPostgres.NeedsPgHbaReconcile(withBlock, null));

        /* Exposing (a rule to apply) -> always reconcile, even with no prior block. */
        Assert.True(DarlingManagedPostgres.NeedsPgHbaReconcile(
            SampleHba, DarlingManagedPostgres.BuildNetworkPgHbaLine("viewer", "192.168.1.0/24")));
    }

    /* ---- firewall command builders (idempotent named shape) ---- */

    [Fact]
    public void BuildFirewallEnableCommand_RemovesThenAdds_ScopedToPortAndCidr()
    {
        const string ruleName = "PerformanceMonitor Darling store (port 5641)";
        var cmd = DarlingManagedPostgres.BuildFirewallEnableCommand(ruleName, 5641, "192.168.1.0/24");

        var removeIdx = cmd.IndexOf("Remove-NetFirewallRule", StringComparison.Ordinal);
        var newIdx = cmd.IndexOf("New-NetFirewallRule", StringComparison.Ordinal);
        Assert.True(removeIdx >= 0 && newIdx > removeIdx, "enable must remove-by-name (idempotent) then add");

        Assert.Contains($"-DisplayName '{ruleName}'", cmd, StringComparison.Ordinal);
        Assert.Contains("-Direction Inbound", cmd, StringComparison.Ordinal);
        Assert.Contains("-Action Allow", cmd, StringComparison.Ordinal);
        Assert.Contains("-Protocol TCP", cmd, StringComparison.Ordinal);
        Assert.Contains("-LocalPort 5641", cmd, StringComparison.Ordinal);
        Assert.Contains("-RemoteAddress 192.168.1.0/24", cmd, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFirewallDisableCommand_RemovesByName_Only()
    {
        const string ruleName = "PerformanceMonitor Darling store (port 5641)";
        var cmd = DarlingManagedPostgres.BuildFirewallDisableCommand(ruleName);

        Assert.Contains($"Remove-NetFirewallRule -DisplayName '{ruleName}'", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain("New-NetFirewallRule", cmd, StringComparison.Ordinal);
    }

    /* ---- store loopback-degrade decision (DarlingManagedPostgres.ResolveNetworkExposure) ---- */

    private const string CertPath = @"C:\ProgramData\PerformanceMonitorDarling\server.crt";
    private const string KeyPath = @"C:\ProgramData\PerformanceMonitorDarling\server.key";

    [Fact]
    public void ResolveNetworkExposure_NoNetwork_IsLoopbackByDefault_NoDegradeReason()
    {
        var decision = DarlingManagedPostgres.ResolveNetworkExposure(null, CertPath, KeyPath);
        Assert.False(decision.Exposed);
        Assert.Null(decision.DegradeReason);
    }

    [Fact]
    public void ResolveNetworkExposure_LoopbackListen_IsNotExposed()
    {
        var decision = DarlingManagedPostgres.ResolveNetworkExposure(
            new PostgresNetworkConfig { Listen = "127.0.0.1", AllowFrom = "192.168.1.0/24", Role = "viewer" }, CertPath, KeyPath);
        Assert.False(decision.Exposed);
        Assert.Null(decision.DegradeReason);
    }

    [Fact]
    public void ResolveNetworkExposure_ValidExposure_IsExposed_WithCanonicalCidrAndRole()
    {
        var decision = DarlingManagedPostgres.ResolveNetworkExposure(
            new PostgresNetworkConfig { Listen = "192.168.1.205", AllowFrom = "192.168.1.0/24", Role = "admin" }, CertPath, KeyPath);

        Assert.True(decision.Exposed);
        Assert.Equal("192.168.1.205", decision.ListenIp);
        Assert.Equal("192.168.1.0/24", decision.Cidr);
        Assert.Equal("admin", decision.Role);
        Assert.Null(decision.DegradeReason);
    }

    [Fact]
    public void ResolveNetworkExposure_DefaultsRoleToViewer_WhenOmitted()
    {
        var decision = DarlingManagedPostgres.ResolveNetworkExposure(
            new PostgresNetworkConfig { Listen = "192.168.1.205", AllowFrom = "192.168.1.0/24" }, CertPath, KeyPath);
        Assert.True(decision.Exposed);
        Assert.Equal("viewer", decision.Role);
    }

    [Fact]
    public void ResolveNetworkExposure_Degrades_WhenAllowFromMissing()
        => AssertDegraded(new PostgresNetworkConfig { Listen = "192.168.1.205", Role = "viewer" });

    [Fact]
    public void ResolveNetworkExposure_Degrades_WhenAllowFromInvalidCidr()
        => AssertDegraded(new PostgresNetworkConfig { Listen = "192.168.1.205", AllowFrom = "not-a-cidr", Role = "viewer" });

    [Fact]
    public void ResolveNetworkExposure_Degrades_WhenAddressFamilyMismatch()
        => AssertDegraded(new PostgresNetworkConfig { Listen = "192.168.1.205", AllowFrom = "2001:db8::/32", Role = "viewer" });

    [Fact]
    public void ResolveNetworkExposure_Degrades_WhenRoleInvalid()
        => AssertDegraded(new PostgresNetworkConfig { Listen = "192.168.1.205", AllowFrom = "192.168.1.0/24", Role = "darling" });

    [Fact]
    public void ResolveNetworkExposure_Degrades_WhenListenNotAnIp()
        => AssertDegraded(new PostgresNetworkConfig { Listen = "db.lan", AllowFrom = "192.168.1.0/24", Role = "viewer" });

    [Fact]
    public void ResolveNetworkExposure_Degrades_WhenCertPathHasWhitespace()
    {
        var decision = DarlingManagedPostgres.ResolveNetworkExposure(
            new PostgresNetworkConfig { Listen = "192.168.1.205", AllowFrom = "192.168.1.0/24", Role = "viewer" },
            @"C:\Program Files\PM\server.crt", @"C:\Program Files\PM\server.key");

        Assert.False(decision.Exposed);
        Assert.NotNull(decision.DegradeReason);
        Assert.Contains("whitespace", decision.DegradeReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DegradedExposure_ProducesLoopbackOnlyRuntimeOptions_NoSsl()
    {
        /* Ties the pieces together: an invalid exposure degrades, and a degraded plan binds loopback-only
           with no ssl — the FULL disable posture, never ssl=off while exposed. */
        var decision = DarlingManagedPostgres.ResolveNetworkExposure(
            new PostgresNetworkConfig { Listen = "192.168.1.205", Role = "viewer" /* no allowFrom */ }, CertPath, KeyPath);
        Assert.False(decision.Exposed);

        var opts = DarlingManagedPostgres.BuildServerRuntimeOptions(5641, decision.Exposed ? decision.ListenIp : null, null, null);
        Assert.Equal("-p 5641 -c listen_addresses=127.0.0.1", opts);
    }

    private static void AssertDegraded(PostgresNetworkConfig network)
    {
        var decision = DarlingManagedPostgres.ResolveNetworkExposure(network, CertPath, KeyPath);
        Assert.False(decision.Exposed);          // never bound to the network
        Assert.NotNull(decision.DegradeReason);  // and it says WHY (logged critical, not a fatal Validate)
        Assert.Null(decision.ListenIp);
        Assert.Null(decision.Cidr);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}

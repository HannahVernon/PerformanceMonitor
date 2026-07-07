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
/// The <c>--test-connection</c>/<c>--validate-config</c> CLI verb. The connectivity probe itself needs a live
/// SQL Server, but the verb recognition and the pure per-server PASS/FAIL formatting are unit-tested here; the
/// probe is shared with the <c>test_connect</c> command (<see cref="DarlingServerConnector.ProbeAsync"/>), so
/// what validates from the CLI connects identically under the running service.
/// </summary>
public sealed class DarlingCliCommandsTests
{
    [Theory]
    [InlineData("--test-connection", true)]
    [InlineData("--validate-config", true)]
    [InlineData("--TEST-CONNECTION", true)]
    [InlineData("--encrypt-password", false)]
    [InlineData("--nonsense", false)]
    public void IsValidateConfigVerb_RecognizesBothAliases_CaseInsensitive(string arg, bool expected)
    {
        Assert.Equal(expected, DarlingCliCommands.IsValidateConfigVerb(arg));
    }

    [Fact]
    public void FormatProbeLine_Success_ShowsVersionEditionAndMsdb()
    {
        var probe = new ConnectionProbeResult(
            Success: true, MajorVersion: 16, EngineEdition: 3, EngineEditionDescription: "Enterprise",
            IsAzureSqlDb: false, IsAzureManagedInstance: false, IsAwsRds: false, HasMsdbAccess: true, Error: null);

        var line = DarlingCliCommands.FormatProbeLine("SQL01", probe);

        Assert.Contains("[PASS]", line, StringComparison.Ordinal);
        Assert.Contains("SQL01", line, StringComparison.Ordinal);
        Assert.Contains("SQL major version 16", line, StringComparison.Ordinal);
        Assert.Contains("Enterprise", line, StringComparison.Ordinal);
        Assert.Contains("msdb access: yes", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatProbeLine_Success_NoMsdb_WarnsFailedJobsUnavailable()
    {
        var probe = new ConnectionProbeResult(
            Success: true, MajorVersion: 15, EngineEdition: 2, EngineEditionDescription: "Standard",
            IsAzureSqlDb: false, IsAzureManagedInstance: false, IsAwsRds: false, HasMsdbAccess: false, Error: null);

        var line = DarlingCliCommands.FormatProbeLine("SQL02", probe);

        Assert.Contains("[PASS]", line, StringComparison.Ordinal);
        Assert.Contains("msdb access: NO", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatProbeLine_MissingDescription_FallsBackToEditionDescriber()
    {
        var probe = new ConnectionProbeResult(
            Success: true, MajorVersion: 16, EngineEdition: 8, EngineEditionDescription: null,
            IsAzureSqlDb: false, IsAzureManagedInstance: true, IsAwsRds: false, HasMsdbAccess: true, Error: null);

        var line = DarlingCliCommands.FormatProbeLine("MI01", probe);

        /* Edition 8 -> Managed Instance, resolved by the shared describer when the probe carries no text. */
        Assert.Contains("Azure SQL Managed Instance", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatProbeLine_Failure_ShowsError()
    {
        var probe = new ConnectionProbeResult(
            Success: false, MajorVersion: 0, EngineEdition: 0, EngineEditionDescription: null,
            IsAzureSqlDb: false, IsAzureManagedInstance: false, IsAwsRds: false, HasMsdbAccess: false,
            Error: "Login failed for user 'monitor'.");

        var line = DarlingCliCommands.FormatProbeLine("SQL03", probe);

        Assert.Contains("[FAIL]", line, StringComparison.Ordinal);
        Assert.Contains("SQL03", line, StringComparison.Ordinal);
        Assert.Contains("Login failed", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeEngineEdition_MapsKnownEditions()
    {
        Assert.Equal("Enterprise", DarlingServerConnector.DescribeEngineEdition(3));
        Assert.Equal("Azure SQL Database", DarlingServerConnector.DescribeEngineEdition(5));
        Assert.Equal("Azure SQL Managed Instance", DarlingServerConnector.DescribeEngineEdition(8));
        Assert.Contains("Unknown", DarlingServerConnector.DescribeEngineEdition(999), StringComparison.Ordinal);
    }
}

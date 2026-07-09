/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

/// <summary>
/// The <c>--print-viewer-connection</c> verb (darling-network-endpoints D8): the pure connection-string /
/// host builders (unit-testable without DPAPI or a store), plus a Windows-gated end-to-end that decrypts a
/// temp <c>viewer</c> credential + emits the cert, asserting the paste-ready shape and the live-secret warning.
/// </summary>
public sealed class DarlingPrintViewerConnectionTests
{
    [Theory]
    [InlineData("--print-viewer-connection", true)]
    [InlineData("--PRINT-VIEWER-CONNECTION", true)]
    [InlineData("--validate-config", false)]
    [InlineData("--encrypt-password", false)]
    [InlineData("--nonsense", false)]
    public void IsPrintViewerConnectionVerb_RecognizesTheVerb_CaseInsensitive(string arg, bool expected)
    {
        Assert.Equal(expected, DarlingCliCommands.IsPrintViewerConnectionVerb(arg));
    }

    [Fact]
    public void BuildViewerConnectionString_CarriesSearchPath_VerifyFull_RootCert_Role_HostAndPort()
    {
        var cs = DarlingCliCommands.BuildViewerConnectionString(
            "192.168.1.205", 5641, "viewer", "s3cretPW", "server.crt");

        Assert.Contains("Host=192.168.1.205", cs, StringComparison.Ordinal);
        Assert.Contains("Port=5641", cs, StringComparison.Ordinal);
        Assert.Contains("Username=viewer", cs, StringComparison.Ordinal);
        Assert.Contains("Password=s3cretPW", cs, StringComparison.Ordinal);
        Assert.Contains("Database=darling", cs, StringComparison.Ordinal);
        Assert.Contains("Search Path=collect,config,public", cs, StringComparison.Ordinal);
        Assert.Contains("SSL Mode=VerifyFull", cs, StringComparison.Ordinal);
        Assert.Contains("Root Certificate=server.crt", cs, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildViewerConnectionString_NamesTheSelectedRole()
    {
        /* role:admin flows through to Username= so the admin opt-in prints an admin connection. */
        var cs = DarlingCliCommands.BuildViewerConnectionString("host", 1, "admin", "pw", "c.crt");
        Assert.Contains("Username=admin", cs, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveViewerHost_ConcreteIp_ReturnsTheIp()
    {
        /* A concrete bind IP is used verbatim — verify-full validates it against the cert's iPAddress SAN. */
        Assert.Equal("192.168.1.205", DarlingCliCommands.ResolveViewerHost("192.168.1.205"));
        Assert.Equal("10.0.0.7", DarlingCliCommands.ResolveViewerHost("  10.0.0.7  "));
    }

    [Theory]
    [InlineData("0.0.0.0")]     // IPv4 wildcard — can't be dialed
    [InlineData("::")]          // IPv6 wildcard
    [InlineData("127.0.0.1")]   // loopback
    [InlineData("localhost")]   // not an IP
    [InlineData("")]            // unset
    [InlineData(null)]
    public void ResolveViewerHost_WildcardLoopbackOrHostname_FallsBackToTheMachineDnsSan(string? listen)
    {
        /* The fallback is the machine hostname, which the cert carries as a dnsName SAN. */
        Assert.Equal(Environment.MachineName, DarlingCliCommands.ResolveViewerHost(listen));
    }

    [Fact]
    public async Task PrintViewerConnectionAsync_ByoMode_ReturnsError_WithoutTouchingDpapi()
    {
        var root = Directory.CreateTempSubdirectory("darling-printconn-byo-");
        try
        {
            var configPath = Path.Combine(root.FullName, "darling.json");
            await File.WriteAllTextAsync(configPath,
                """{ "postgres": { "connectionString": "Host=localhost;Database=darling" } }""");

            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await DarlingCliCommands.PrintViewerConnectionAsync(configPath, output, error, CancellationToken.None);

            Assert.Equal(1, exit);
            Assert.Contains("bring-your-own", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("", output.ToString());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PrintViewerConnectionAsync_InvalidRole_ReturnsError()
    {
        var root = Directory.CreateTempSubdirectory("darling-printconn-role-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var configPath = Path.Combine(root.FullName, "darling.json");
            var json = $$"""
                {
                  "postgres": {
                    "managed": true,
                    "port": 5641,
                    "dataDirectory": {{JsonSerializer.Serialize(dataDirectory)}},
                    "network": { "listen": "192.168.1.205", "allowFrom": "192.168.1.0/24", "role": "superadmin" }
                  }
                }
                """;
            await File.WriteAllTextAsync(configPath, json);

            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await DarlingCliCommands.PrintViewerConnectionAsync(configPath, output, error, CancellationToken.None);

            Assert.Equal(1, exit);
            Assert.Contains("role", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PrintViewerConnectionAsync_ManagedViewer_PrintsConnection_Cert_AndSecretWarning()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-printconn-");
        try
        {
            /* Lay down the managed layout the verb reads on the store host: the viewer role's DPAPI credential
               and the generated server cert, both beside the data directory. */
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var viewerCredential = PerformanceMonitor.Darling.Service.DarlingManagedPostgres.ViewerCredentialPathFor(dataDirectory);
            File.WriteAllText(viewerCredential, PerformanceMonitor.Darling.Service.DarlingSecrets.Protect("viewer-secret-pw"));

            var certPath = Path.Combine(
                Path.GetDirectoryName(viewerCredential)!,
                PerformanceMonitor.Darling.Service.DarlingManagedPostgres.ServerCertFileName);
            const string pem = "-----BEGIN CERTIFICATE-----\nMIIBTESTCERTPEM\n-----END CERTIFICATE-----";
            File.WriteAllText(certPath, pem);

            var configPath = Path.Combine(root.FullName, "darling.json");
            var json = $$"""
                {
                  "postgres": {
                    "managed": true,
                    "port": 5641,
                    "dataDirectory": {{JsonSerializer.Serialize(dataDirectory)}},
                    "network": { "listen": "192.168.1.205", "allowFrom": "192.168.1.0/24", "role": "viewer" }
                  },
                  "servers": [ { "name": "SQL2022", "host": "SQL2022" } ]
                }
                """;
            await File.WriteAllTextAsync(configPath, json);

            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await DarlingCliCommands.PrintViewerConnectionAsync(configPath, output, error, CancellationToken.None);
            var stdout = output.ToString();
            var stderr = error.ToString();

            Assert.Equal(0, exit);

            /* The paste-ready connection string on STDOUT: verify-full, the search path, the client cert path,
               the viewer role, the decrypted password, and Host=the IP (Round 4 #12). */
            Assert.Contains("Host=192.168.1.205", stdout, StringComparison.Ordinal);
            Assert.Contains("Username=viewer", stdout, StringComparison.Ordinal);
            Assert.Contains("Password=viewer-secret-pw", stdout, StringComparison.Ordinal);
            Assert.Contains("Search Path=collect,config,public", stdout, StringComparison.Ordinal);
            Assert.Contains("SSL Mode=VerifyFull", stdout, StringComparison.Ordinal);
            Assert.Contains("Root Certificate=server.crt", stdout, StringComparison.Ordinal);

            /* The server cert PEM is emitted so the operator can place it on the client. */
            Assert.Contains(pem, stdout, StringComparison.Ordinal);

            /* The live-secret warning is printed (to STDERR, so a STDOUT redirect keeps it visible). */
            Assert.Contains("LIVE database password", stderr, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}

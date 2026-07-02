/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Linq;
using Microsoft.Data.SqlClient;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the Darling config plane (M2 slice B): the sample file parses (comments allowed),
/// validation catches the real misconfigurations, the connection string mirrors Lite's posture
/// (MARS, 15s connect, Encrypt fail-closed to Mandatory), and DPAPI password protection
/// round-trips with the blob preferred over plaintext.
/// </summary>
public sealed class DarlingConfigTests
{
    private static MonitoredServer Server(Action<MonitoredServer>? mutate = null)
    {
        var server = new MonitoredServer { Name = "S1", Host = "SQL2022" };
        mutate?.Invoke(server);
        return server;
    }

    private static DarlingConfig ValidConfig(Action<DarlingConfig>? mutate = null)
    {
        var config = new DarlingConfig
        {
            Postgres = new PostgresConfig { ConnectionString = "Host=localhost;Database=darling" },
            Servers = { Server() },
        };
        mutate?.Invoke(config);
        return config;
    }

    [Fact]
    public void SampleConfig_ParsesWithComments()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "darling.sample.json");
        var config = DarlingConfig.Parse(File.ReadAllText(samplePath));

        Assert.Equal(2, config.Servers.Count);
        Assert.False(config.Servers[0].UsesSqlAuth);
        Assert.True(config.Servers[1].UsesSqlAuth);
        Assert.Equal("monitor", config.Servers[1].Username);
        Assert.Equal(new[] { "StageDb" }, config.Servers[1].ExcludedDatabases);
        Assert.NotEmpty(config.Postgres.ConnectionString);
    }

    [Fact]
    public void Validate_CatchesRealMisconfigurations()
    {
        Assert.Empty(ValidConfig().Validate());

        Assert.Contains(ValidConfig(c => c.Postgres.ConnectionString = "").Validate(),
            p => p.Contains("postgres.connectionString", StringComparison.Ordinal));
        Assert.Contains(ValidConfig(c => c.Servers.Clear()).Validate(),
            p => p.Contains("at least one", StringComparison.Ordinal));
        Assert.Contains(ValidConfig(c => c.Servers[0].Host = "").Validate(),
            p => p.Contains("host is required", StringComparison.Ordinal));
        Assert.Contains(ValidConfig(c => c.Servers[0].Auth = "kerberos").Validate(),
            p => p.Contains("auth must be", StringComparison.Ordinal));

        var sqlNoCreds = ValidConfig(c => c.Servers[0].Auth = "sql").Validate();
        Assert.Contains(sqlNoCreds, p => p.Contains("requires username", StringComparison.Ordinal));
        Assert.Contains(sqlNoCreds, p => p.Contains("encryptedPassword", StringComparison.Ordinal));
    }

    [Fact]
    public void ConnectionString_MirrorsLitePosture_Integrated()
    {
        var connectionString = MonitoredServerConnection.BuildConnectionString(Server());
        var parsed = new SqlConnectionStringBuilder(connectionString);

        Assert.Equal("SQL2022", parsed.DataSource);
        Assert.Equal("master", parsed.InitialCatalog);
        Assert.Equal("PerformanceMonitorDarling", parsed.ApplicationName);
        Assert.Equal(15, parsed.ConnectTimeout);
        Assert.Equal(60, parsed.CommandTimeout);
        Assert.True(parsed.MultipleActiveResultSets);
        Assert.True(parsed.IntegratedSecurity);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, parsed.Encrypt);
        Assert.Equal(ApplicationIntent.ReadWrite, parsed.ApplicationIntent);
    }

    [Fact]
    public void ConnectionString_SqlAuth_AzureDatabase_ReadOnly_StrictAndFailClosed()
    {
        var server = Server(s =>
        {
            s.Auth = "sql";
            s.Username = "monitor";
            s.Database = "app1";
            s.ReadOnlyIntent = true;
            s.EncryptMode = "Strict";
        });
        var parsed = new SqlConnectionStringBuilder(MonitoredServerConnection.BuildConnectionString(server, "pw"));

        Assert.Equal("app1", parsed.InitialCatalog);
        Assert.Equal("monitor", parsed.UserID);
        Assert.Equal("pw", parsed.Password);
        Assert.False(parsed.IntegratedSecurity);
        Assert.Equal(ApplicationIntent.ReadOnly, parsed.ApplicationIntent);
        Assert.Equal(SqlConnectionEncryptOption.Strict, parsed.Encrypt);

        /* Unknown mode fails closed to Mandatory, matching Lite. */
        var weird = new SqlConnectionStringBuilder(
            MonitoredServerConnection.BuildConnectionString(Server(s => s.EncryptMode = "banana")));
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, weird.Encrypt);

        /* SQL auth without a resolved password is a hard error, not a silent empty password. */
        Assert.Throws<InvalidOperationException>(() =>
            MonitoredServerConnection.BuildConnectionString(Server(s => { s.Auth = "sql"; s.Username = "u"; })));
    }

    [Fact]
    public void StorageName_UsesSharedIdentityRule()
    {
        Assert.Equal("SQL2022", Server().StorageName);
        Assert.Equal("myserver:app1", Server(s => { s.Host = "myserver"; s.Database = "app1"; }).StorageName);
        Assert.Equal("myserver:app1:RO", Server(s => { s.Host = "myserver"; s.Database = "app1"; s.ReadOnlyIntent = true; }).StorageName);
    }

    [Fact]
    public void Secrets_DpapiRoundTrip_BlobPreferredOverPlaintext()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var blob = DarlingSecrets.Protect("s3cret!");
        Assert.NotEqual("s3cret!", blob);
        Assert.Equal("s3cret!", DarlingSecrets.Unprotect(blob));

        var server = Server(s => { s.Auth = "sql"; s.Username = "u"; s.EncryptedPassword = blob; s.Password = "wrong-plaintext"; });
        Assert.Equal("s3cret!", DarlingSecrets.ResolvePassword(server, out var usedPlaintext));
        Assert.False(usedPlaintext);

        var devServer = Server(s => { s.Auth = "sql"; s.Username = "u"; s.Password = "dev-pw"; });
        Assert.Equal("dev-pw", DarlingSecrets.ResolvePassword(devServer, out usedPlaintext));
        Assert.True(usedPlaintext);
    }
}

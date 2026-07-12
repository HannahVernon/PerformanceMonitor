using Installer.Core;
using Installer.Tests.Helpers;
using Microsoft.Data.SqlClient;

namespace Installer.Tests;

/// <summary>
/// Tests version detection logic — the #538 regression fix is the most critical test here.
///
/// Note: InstallationService.GetInstalledVersionAsync hardcodes the database name
/// "PerformanceMonitor", so these tests replicate the same SQL queries against a
/// test database (PerformanceMonitor_Test) to avoid touching real data.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Database")]
public class VersionDetectionTests : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await TestDatabaseHelper.DropTestDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await TestDatabaseHelper.DropTestDatabaseAsync();
    }

    [Fact]
    public async Task DatabaseDoesNotExist_ReturnsNull()
    {
        // Database was dropped in InitializeAsync
        var version = await GetInstalledVersionFromTestDbAsync();
        Assert.Null(version);
    }

    [Fact]
    public async Task DatabaseExists_WithSuccessRow_ReturnsVersion()
    {
        await TestDatabaseHelper.CreatePartialInstallationAsync("2.1.0");

        var version = await GetInstalledVersionFromTestDbAsync();
        Assert.Equal("2.1.0", version);
    }

    [Fact]
    public async Task DatabaseExists_NoHistoryTable_NoCollectObjects_ReturnsNull()
    {
        // An EMPTY database (someone pre-created it, e.g. to control the file paths). Nothing is
        // installed, so a clean install is correct -- attempting migrations here would fail against
        // tables that do not exist.
        await TestDatabaseHelper.CreateTestDatabaseAsync();

        var version = await GetInstalledVersionFromTestDbAsync();
        Assert.Null(version);
    }

    [Fact]
    public async Task DatabaseExists_NoHistoryTable_WithCollectObjects_ReturnsFallback()
    {
        // A PerformanceMonitor database whose LEDGER WAS LOST (someone dropped config.installation_history).
        // Answering null here would read as "fresh install": the install scripts run over the live schema,
        // every migration is skipped, and the target version is stamped SUCCESS -- stranding them
        // permanently. It must fall back to the "attempt every upgrade" sentinel instead.
        await TestDatabaseHelper.CreateTestDatabaseAsync();

        using (var connection = new SqlConnection(TestDatabaseHelper.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using var cmd = new SqlCommand(@"
                USE PerformanceMonitor_Test;
                IF SCHEMA_ID(N'collect') IS NULL
                BEGIN
                    EXECUTE(N'CREATE SCHEMA collect;');
                END;
                IF OBJECT_ID(N'collect.wait_stats', N'U') IS NULL
                BEGIN
                    CREATE TABLE collect.wait_stats (id integer NOT NULL);
                END;", connection);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var version = await GetInstalledVersionFromTestDbAsync();
        Assert.Equal(InstallationService.UnknownVersionSentinel, version);
    }

    [Fact]
    public async Task DatabaseExists_EmptyHistoryTable_ReturnsFallback_Regression538()
    {
        // This is the #538 regression test.
        // When installation_history exists but has NO SUCCESS rows,
        // the installer must return "1.0.0" (not null), so it attempts
        // upgrades rather than treating the existing database as a fresh install.
        await TestDatabaseHelper.CreateInstallationWithNoSuccessRowsAsync();

        var version = await GetInstalledVersionFromTestDbAsync();
        Assert.Equal("1.0.0", version);
    }

    [Fact]
    public async Task DatabaseExists_OnlyFailedRows_ReturnsFallback()
    {
        // All rows are FAILED — same fallback behavior as empty table
        await TestDatabaseHelper.CreateInstallationWithOnlyFailedRowsAsync();

        var version = await GetInstalledVersionFromTestDbAsync();
        Assert.Equal("1.0.0", version);
    }

    [Fact]
    public async Task MultipleSuccessRows_ReturnsLatest()
    {
        await TestDatabaseHelper.CreatePartialInstallationAsync("1.3.0");

        // Add a newer success row
        using var connection = new SqlConnection(TestDatabaseHelper.GetTestDbConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var cmd = new SqlCommand(@"
            -- Use explicit future date to ensure this row sorts first
            INSERT INTO config.installation_history
                (installer_version, installation_status, installation_type, sql_server_version, sql_server_edition, installation_date)
            VALUES
                (N'2.2.0', N'SUCCESS', N'UPGRADE', @@VERSION, N'Test', DATEADD(HOUR, 1, SYSDATETIME()));",
            connection);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        var version = await GetInstalledVersionFromTestDbAsync();
        Assert.Equal("2.2.0", version);
    }

    /// <summary>
    /// Replicates the same SQL logic as InstallationService.GetInstalledVersionAsync
    /// but queries PerformanceMonitor_Test instead of the hardcoded PerformanceMonitor.
    /// </summary>
    private static async Task<string?> GetInstalledVersionFromTestDbAsync()
    {
        const string testDbName = "PerformanceMonitor_Test";

        try
        {
            using var connection = new SqlConnection(TestDatabaseHelper.GetConnectionString());
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            // Check if database exists
            using var dbCheckCmd = new SqlCommand($@"
                SELECT database_id FROM sys.databases WHERE name = N'{testDbName}';", connection);
            var dbExists = await dbCheckCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            if (dbExists == null || dbExists == DBNull.Value)
                return null;

            // Check if installation_history table exists
            using var tableCheckCmd = new SqlCommand($@"
                SELECT OBJECT_ID(N'{testDbName}.config.installation_history', N'U');", connection);
            var tableExists = await tableCheckCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            if (tableExists == null || tableExists == DBNull.Value)
            {
                // No history table. A PerformanceMonitor database whose ledger was LOST must not read as
                // "fresh install" -- that skips every migration and then stamps the target version SUCCESS,
                // stranding them permanently. Tell it apart from an empty pre-created database by whether
                // it actually holds collect objects, and fall back to the #538 "attempt every upgrade"
                // sentinel for the damaged case. Mirrors InstallationService.GetInstalledVersionAsync.
                using var collectCheckCmd = new SqlCommand($@"
                    SELECT
                        COUNT_BIG(*)
                    FROM {testDbName}.sys.tables AS t
                    JOIN {testDbName}.sys.schemas AS s
                      ON s.schema_id = t.schema_id
                    WHERE s.name = N'collect';", connection);

                var collectTables = await collectCheckCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                long collectCount =
                    collectTables == null || collectTables == DBNull.Value
                        ? 0L
                        : Convert.ToInt64(collectTables);

                return collectCount > 0 ? InstallationService.UnknownVersionSentinel : null;
            }

            // Get most recent successful version
            using var versionCmd = new SqlCommand($@"
                SELECT TOP 1 installer_version
                FROM {testDbName}.config.installation_history
                WHERE installation_status = 'SUCCESS'
                ORDER BY installation_date DESC;", connection);
            var version = await versionCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            if (version != null && version != DBNull.Value)
                return version.ToString();

            // Fallback: database + table exist but no SUCCESS rows → return "1.0.0"
            return "1.0.0";
        }
        catch
        {
            return null;
        }
    }
}

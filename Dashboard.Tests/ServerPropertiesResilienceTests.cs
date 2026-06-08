using PerformanceMonitorDashboard.Analysis;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// WS5 version-skew resilience: the server_properties SELECT references the server-health columns
/// only when the DB has them, so a not-yet-upgraded server (Dashboard updated before that server's
/// PerformanceMonitor DB got the WS5 upgrade) reads the core hardware columns without a bind error
/// — keeping SERVER_HARDWARE flowing — instead of the whole collector throwing.
/// </summary>
public class ServerPropertiesResilienceTests
{
    [Fact]
    public void Query_WithHealthColumns_SelectsThemPlusCore()
    {
        var sql = SqlServerFactCollector.BuildServerPropertiesQuery(includeHealthColumns: true);

        Assert.Contains("lock_pages_in_memory", sql);
        Assert.Contains("instant_file_initialization_enabled", sql);
        Assert.Contains("memory_dump_count", sql);
        Assert.Contains("cpu_count", sql);
        Assert.Contains("FROM collect.server_properties", sql);
    }

    [Fact]
    public void Query_WithoutHealthColumns_OmitsThem_ButKeepsCore()
    {
        var sql = SqlServerFactCollector.BuildServerPropertiesQuery(includeHealthColumns: false);

        // The WS5 columns must NOT be referenced — that is what avoids the bind error on a
        // not-yet-upgraded server.
        Assert.DoesNotContain("lock_pages_in_memory", sql);
        Assert.DoesNotContain("instant_file_initialization_enabled", sql);
        Assert.DoesNotContain("memory_dump_count", sql);

        // The core hardware columns are still selected, so SERVER_HARDWARE keeps flowing.
        Assert.Contains("cpu_count", sql);
        Assert.Contains("product_version", sql);
        Assert.Contains("FROM collect.server_properties", sql);
    }
}

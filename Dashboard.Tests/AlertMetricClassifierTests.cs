using PerformanceMonitor.Common;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// #1225: the shared metric-name classifier that both apps' Alert History grids and the Dashboard
/// sidebar Alert badge rely on. Locks in the resolution suffix set (Cleared/Resolved/Restored) —
/// the "Restored" cases (Capture/Server Restored) are the ones the old duplicated inline copies
/// missed — plus the critical (Deadlock/Poison) and warning buckets, over the metric names the
/// alert engines actually emit.
/// </summary>
public class AlertMetricClassifierTests
{
    [Theory]
    [InlineData("Blocking Cleared")]
    [InlineData("Deadlocks Cleared")]
    [InlineData("Poison Waits Cleared")]
    [InlineData("Long-Running Queries Cleared")]
    [InlineData("Long-Running Jobs Cleared")]
    [InlineData("CPU Resolved")]
    [InlineData("TempDB Space Resolved")]
    [InlineData("Volume Free Space Resolved")]
    [InlineData("Capture Restored")]
    [InlineData("Server Restored")]
    public void IsResolution_True_ForEveryResolutionNotice(string metric)
    {
        Assert.True(AlertMetricClassifier.IsResolution(metric));
        Assert.False(AlertMetricClassifier.IsWarning(metric)); // a resolution notice is never a warning
    }

    [Theory]
    [InlineData("Blocking Detected")]
    [InlineData("Deadlocks Detected")]
    [InlineData("High CPU")]
    [InlineData("Poison Wait")]
    [InlineData("Long-Running Query")]
    [InlineData("TempDB Space")]
    [InlineData("Volume Free Space")]
    [InlineData("Long-Running Job")]
    [InlineData("Failed Agent Job")]
    [InlineData("Capture Down")]
    [InlineData("Server Unreachable")]
    public void IsResolution_False_ForActionableAlerts(string metric)
    {
        Assert.False(AlertMetricClassifier.IsResolution(metric));
    }

    [Theory]
    [InlineData("Deadlocks Detected")]
    [InlineData("Poison Wait")]
    public void IsCritical_True_ForDeadlockAndPoison(string metric)
    {
        Assert.True(AlertMetricClassifier.IsCritical(metric));
    }

    [Theory]
    [InlineData("Blocking Detected")]
    [InlineData("High CPU")]
    [InlineData("Long-Running Query")]
    public void IsWarning_True_ForOrdinaryActionableAlerts(string metric)
    {
        Assert.True(AlertMetricClassifier.IsWarning(metric));
        Assert.False(AlertMetricClassifier.IsCritical(metric));
        Assert.False(AlertMetricClassifier.IsResolution(metric));
    }

    [Fact]
    public void Classifiers_AreFalse_ForNullOrEmpty()
    {
        Assert.False(AlertMetricClassifier.IsResolution(null));
        Assert.False(AlertMetricClassifier.IsResolution(""));
        Assert.False(AlertMetricClassifier.IsCritical(null));
        Assert.False(AlertMetricClassifier.IsCritical(""));
        // IsWarning is the complement of the two signals, so an absent name defaults to warning —
        // matching the long-standing display behavior this classifier replaced.
        Assert.True(AlertMetricClassifier.IsWarning(null));
        Assert.True(AlertMetricClassifier.IsWarning(""));
    }
}

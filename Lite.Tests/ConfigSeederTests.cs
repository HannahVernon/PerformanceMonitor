using System;
using System.IO;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// #1240: ConfigSeeder copies bundled config into the per-user dir on first run (so a fresh install's
/// ignored_wait_types.json exists and wait filtering isn't a silent no-op), without ever clobbering a
/// file the user has edited.
/// </summary>
public class ConfigSeederTests
{
    private static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pmlite_{tag}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SeedMissing_CopiesFile_WhenDestinationAbsent()
    {
        var bundled = NewTempDir("bundled");
        var user = NewTempDir("user");
        try
        {
            File.WriteAllText(Path.Combine(bundled, "ignored_wait_types.json"), "{\"ignored_waits\":[\"SOS_WORK_DISPATCHER\"]}");

            var seeded = ConfigSeeder.SeedMissing(bundled, user, new[] { "ignored_wait_types.json" });

            Assert.Equal(1, seeded);
            var dest = Path.Combine(user, "ignored_wait_types.json");
            Assert.True(File.Exists(dest));
            Assert.Contains("SOS_WORK_DISPATCHER", File.ReadAllText(dest));
        }
        finally
        {
            Directory.Delete(bundled, true);
            Directory.Delete(user, true);
        }
    }

    [Fact]
    public void SeedMissing_DoesNotOverwrite_ExistingUserFile()
    {
        var bundled = NewTempDir("bundled");
        var user = NewTempDir("user");
        try
        {
            File.WriteAllText(Path.Combine(bundled, "ignored_wait_types.json"), "{\"ignored_waits\":[\"BUNDLED\"]}");
            File.WriteAllText(Path.Combine(user, "ignored_wait_types.json"), "{\"ignored_waits\":[\"USER_EDITED\"]}");

            var seeded = ConfigSeeder.SeedMissing(bundled, user, new[] { "ignored_wait_types.json" });

            Assert.Equal(0, seeded);
            Assert.Contains("USER_EDITED", File.ReadAllText(Path.Combine(user, "ignored_wait_types.json")));
        }
        finally
        {
            Directory.Delete(bundled, true);
            Directory.Delete(user, true);
        }
    }

    [Fact]
    public void SeedMissing_SkipsName_NotInBundle()
    {
        var bundled = NewTempDir("bundled");
        var user = NewTempDir("user");
        try
        {
            var seeded = ConfigSeeder.SeedMissing(bundled, user, new[] { "missing.json" });

            Assert.Equal(0, seeded);
            Assert.False(File.Exists(Path.Combine(user, "missing.json")));
        }
        finally
        {
            Directory.Delete(bundled, true);
            Directory.Delete(user, true);
        }
    }

    [Fact]
    public void SeedMissing_NonexistentBundleDir_ReturnsZero()
    {
        var user = NewTempDir("user");
        try
        {
            var seeded = ConfigSeeder.SeedMissing(Path.Combine(user, "does_not_exist"), user, new[] { "ignored_wait_types.json" });

            Assert.Equal(0, seeded);
        }
        finally
        {
            Directory.Delete(user, true);
        }
    }
}

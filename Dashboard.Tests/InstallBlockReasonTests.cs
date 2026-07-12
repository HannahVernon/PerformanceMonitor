using PerformanceMonitorDashboard;
using Xunit;

namespace Dashboard.Tests
{
    /// <summary>
    /// Pins AddServerDialog.GetInstallBlockReason, the decision core for when installing is unsafe.
    ///
    /// Both of the worst defects found while building this feature lived here. A version we cannot
    /// compare, or a server NEWER than this build, produces ZERO upgrade hops and ZERO failures --
    /// so the migration-failure abort never fires and nothing downstream catches it. The install
    /// then runs this binary's older scripts over the newer database, reverting every CREATE OR
    /// ALTER procedure and view, and records the LOWER version as SUCCESS. Version detection reads
    /// back the most recent SUCCESS row, so that silently strands every migration in between.
    /// </summary>
    public class InstallBlockReasonTests
    {
        [Fact]
        public void NoDatabase_IsAllowed()
        {
            /* null means no installation found: a fresh install is safe. */
            Assert.Null(AddServerDialog.GetInstallBlockReason(null, "3.1.0"));
        }

        [Theory]
        [InlineData("3.1.0", "3.1.0")]
        [InlineData("3.0.0", "3.1.0")]
        [InlineData("3.1.0.0", "3.1.0")]
        [InlineData("2.9", "3.1.0")]
        [InlineData("3.0.0", "3.1.0+abc123")]
        [InlineData("3.0.0", "3.2.0-rc1")]
        public void SameOrOlderThanThisBuild_IsAllowed(string installed, string app)
        {
            Assert.Null(AddServerDialog.GetInstallBlockReason(installed, app));
        }

        [Theory]
        [InlineData("3.2.0", "3.1.0")]
        [InlineData("3.1.1", "3.1.0")]
        [InlineData("4.0.0", "3.1.0.0")]
        [InlineData("3.2.0", "3.1.0+abc123")]
        public void NewerThanThisBuild_IsBlocked(string installed, string app)
        {
            var reason = AddServerDialog.GetInstallBlockReason(installed, app);

            Assert.NotNull(reason);
            Assert.Contains("newer than this", reason);
        }

        [Theory]
        [InlineData("Unreachable")]
        [InlineData("Not installed")]
        [InlineData("")]
        [InlineData("   ")]
        public void UnreadableInstalledVersion_IsBlocked(string installed)
        {
            var reason = AddServerDialog.GetInstallBlockReason(installed, "3.1.0");

            Assert.NotNull(reason);
            Assert.Contains("Could not interpret the installed", reason);
        }

        [Fact]
        public void UnreadableAppVersion_IsBlocked_AndBlamesTheBuildNotTheServer()
        {
            /* Nothing the user does to the database fixes a malformed InformationalVersion. */
            var reason = AddServerDialog.GetInstallBlockReason("3.0.0", "not-a-version");

            Assert.NotNull(reason);
            Assert.Contains("This Dashboard reports its own version", reason);
        }
    }
}

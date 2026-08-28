using Wavee.Core;
using Xunit;

// The pure decisions behind the real updater (App/AppInstallerUpdateService.cs): "is the feed newer than me?" and
// "did this launch follow an update?". Both are engine-free static functions precisely so they can be pinned here;
// the HTTP/XML/shell half of the service is exercised by running the app, not by a test that fakes GitHub.
public class AppInstallerUpdateServiceTests
{
    // ── IsNewer: the ordinary ordering ──────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("0.1.2", "0.1.1")]
    [InlineData("0.2.0", "0.1.9")]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("0.1.1.1", "0.1.1.0")]
    [InlineData("0.1.1.42", "0.1.1.7")]        // 4th part compares numerically, not lexically
    [InlineData("0.1.10", "0.1.9")]            // ditto for the 3rd
    [InlineData("0.10.0", "0.9.0")]
    [InlineData("2.0.0.0", "1.99.99.99")]
    public void IsNewer_True_WhenRemoteAhead(string remote, string current)
        => Assert.True(AppUpdateVersion.IsNewer(remote, current));

    [Theory]
    [InlineData("0.1.1", "0.1.1")]
    [InlineData("0.1.1", "0.1.2")]
    [InlineData("0.1.1.0", "0.1.1")]           // a missing part is 0, so these are the SAME version
    [InlineData("0.1.1", "0.1.1.0")]
    [InlineData("0.1.1", "0.1.1.1")]
    [InlineData("0.9.9", "1.0.0")]
    public void IsNewer_False_WhenRemoteNotAhead(string remote, string current)
        => Assert.False(AppUpdateVersion.IsNewer(remote, current));

    // ── IsNewer: normalization ──────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("0.1.2", "0.1.1-dev")]         // a dev build still learns a release is out
    [InlineData("v0.1.2", "0.1.1")]            // a leading v is tolerated
    [InlineData("0.1.2+abc123", "0.1.1")]      // build metadata is not identity
    [InlineData("0.1.2-rc.1", "0.1.1")]        // pre-release suffix stripped: 0.1.2-rc.1 still beats 0.1.1
    [InlineData("  0.1.2  ", "0.1.1")]
    public void IsNewer_NormalizesBothSides(string remote, string current)
        => Assert.True(AppUpdateVersion.IsNewer(remote, current));

    [Fact]
    public void IsNewer_PreReleaseOfSameVersion_IsNotNewer()
    {
        // 0.1.1-dev normalizes to 0.1.1 -- equal, not ahead. A dev build of the version already shipped must not
        // prompt itself to "update" to the release it is a build of.
        Assert.False(AppUpdateVersion.IsNewer("0.1.1-dev", "0.1.1"));
        Assert.False(AppUpdateVersion.IsNewer("0.1.1", "0.1.1-dev"));
    }

    // ── IsNewer: anything unparsable is a refusal, never a prompt ───────────────────────────────────────────────
    [Theory]
    [InlineData("0.1.2", "dev")]               // the unstamped-build sentinel
    [InlineData("0.1.2", "")]
    [InlineData("0.1.2", null)]
    [InlineData("0.1.2", "not-a-version")]
    [InlineData("0.1.2", "1.x.3")]
    [InlineData("0.1.2", "1..2")]
    [InlineData("0.1.2", "1.2.3.4.5")]         // more parts than a version has
    [InlineData("dev", "0.1.1")]
    [InlineData("", "0.1.1")]
    [InlineData(null, "0.1.1")]
    [InlineData("garbage", "0.1.1")]
    [InlineData("1.2.3.", "0.1.1")]
    [InlineData("-1.2.3", "0.1.1")]            // the '-' strips to an empty version
    public void IsNewer_False_WhenEitherSideUnparsable(string? remote, string? current)
        => Assert.False(AppUpdateVersion.IsNewer(remote, current));

    [Fact]
    public void IsNewer_UnparsableBothSides_IsFalse()
        => Assert.False(AppUpdateVersion.IsNewer("dev", "dev"));

    // ── the startup "you were updated" rule ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void FirstRunAfterUpdate_True_WhenLastRunDiffers()
        => Assert.True(AppUpdateVersion.IsFirstRunAfterUpdate("0.1.1", "0.1.2"));

    [Fact]
    public void FirstRunAfterUpdate_TrueOnDowngradeToo()
        // A rollback is still "the version changed under you" -- the notice is about the change, not its direction.
        => Assert.True(AppUpdateVersion.IsFirstRunAfterUpdate("0.1.2", "0.1.1"));

    [Fact]
    public void FirstRunAfterUpdate_False_OnFirstEverLaunch()
    {
        // An empty LastRunVersion is a fresh install: greeting it with "Wavee was updated" would be a lie.
        Assert.False(AppUpdateVersion.IsFirstRunAfterUpdate("", "0.1.1"));
        Assert.False(AppUpdateVersion.IsFirstRunAfterUpdate(null, "0.1.1"));
    }

    [Fact]
    public void FirstRunAfterUpdate_False_WhenUnchanged()
        => Assert.False(AppUpdateVersion.IsFirstRunAfterUpdate("0.1.1", "0.1.1"));

    [Fact]
    public void FirstRunAfterUpdate_False_WhenCurrentUnknown()
        => Assert.False(AppUpdateVersion.IsFirstRunAfterUpdate("0.1.1", ""));

    [Fact]
    public void FirstRunAfterUpdate_IsExactStringCompare()
        // Not a version comparison: 0.1.1 and 0.1.1.0 are DIFFERENT stamps, and a build that changed stamp changed.
        => Assert.True(AppUpdateVersion.IsFirstRunAfterUpdate("0.1.1", "0.1.1.0"));

    // ── the release-notes tag ───────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("0.1.1.42", "0.1.1")]
    [InlineData("0.1.1", "0.1.1")]
    [InlineData("0.1.1-dev", "0.1.1")]
    [InlineData("v2.3.4+meta", "2.3.4")]
    [InlineData("1.2", "1.2.0")]               // missing parts are 0, so the tag is still three-part
    public void ReleaseTagVersion_TakesFirstThreeParts(string version, string expected)
        => Assert.Equal(expected, AppUpdateVersion.ReleaseTagVersion(version));

    [Theory]
    [InlineData("dev", "dev")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ReleaseTagVersion_FallsBackToNormalizedInput(string? version, string expected)
        => Assert.Equal(expected, AppUpdateVersion.ReleaseTagVersion(version));
}

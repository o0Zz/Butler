namespace Butler.Tests;

public class VersionTests
{
    [Theory]
    [InlineData("1.0.0.57+0149889e71a7c3eb23807d8a625c0c821178cc7", "1.0.0.57 (0149889)")]
    [InlineData("0.0.0.6", "0.0.0.6")]
    public void The_commit_is_shortened(string informational, string shown) => Assert.Equal(shown, Program.ShortVersion(informational));

    // X.Y.Z (0.0.0, or the tag's in CI) then the number of commits, and the commit.
    [Fact]
    public void The_build_carries_its_commit_count_and_commit()
    {
        var count = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "rev-list --count HEAD") { RedirectStandardOutput = true })!
            .StandardOutput.ReadToEnd().Trim();
        Assert.Matches($@"^\d+\.\d+\.\d+\.{count} \([0-9a-f]{{7}}\)$", Program.Version);
    }
}

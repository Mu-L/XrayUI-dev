using XrayUI.Helpers;
using XrayUI.Services;

namespace XrayUI.Tests;

public sealed class JumpListTests
{
    [Fact]
    public void NormalAndBootLaunchesHaveNoExplicitNode()
    {
        Assert.Null(JumpListRequest.Parse(["XrayUI.exe"]));
        Assert.Null(JumpListRequest.Parse(["XrayUI.exe", "--startup-minimized"]));
    }

    [Theory]
    [InlineData("68d553974e4942d08da4c5cfe2b53d87")]
    [InlineData("legacy id/含 空格\"&--tun=%25")]
    public void NodeIdSurvivesBothColdAndRedirectedCommandLines(string id)
    {
        var command = new JumpListRequest(id).ToArguments();
        Assert.DoesNotContain(' ', command);
        Assert.DoesNotContain('"', command);
        Assert.Equal(id, JumpListRequest.Parse(["C:\\Program Files\\XrayUI.exe", command])!.ServerId);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(id, JumpListRequest.Parse(CommandLineArguments.Split("\"C:\\Program Files\\XrayUI.exe\" " + command))!.ServerId);
            Assert.Equal(id, JumpListRequest.Parse(CommandLineArguments.Split(command))!.ServerId);
        }
    }

    [Fact]
    public void InvalidExplicitRequestsMustNeverBecomeOrdinaryLaunches()
    {
        Assert.Equal(string.Empty, JumpListRequest.Parse(["--connect-node="])!.ServerId);
        Assert.Equal(string.Empty, JumpListRequest.Parse(["--connect-node=a", "--connect-node=b"])!.ServerId);
        Assert.Equal(string.Empty, JumpListRequest.Parse(["--connect-node=%00"])!.ServerId);
        Assert.Equal(string.Empty, JumpListRequest.Parse(["--connect-node=" + new string('a', 3000)])!.ServerId);
    }

    [Fact]
    public void SuccessfulReconnectMovesToFrontWithoutDuplicatingAndKeepsFive()
    {
        string[] available = ["a", "b", "c", "d", "e", "f"];
        Assert.Equal(["c", "a", "b", "d", "e"],
            RecentConnectionHistory.Update(["a", "b", "c", "d", "e"], available, "c"));
        Assert.Equal(["f", "a", "b", "c", "d"],
            RecentConnectionHistory.Update(["a", "b", "c", "d", "e"], available, "f"));
    }

    [Fact]
    public void RefreshPrunesMissingNodesWithoutInventingHistoryFromSelection()
    {
        Assert.Equal(["b", "a"], RecentConnectionHistory.Update(["gone", "b", "b", "a"], ["a", "b", "selected"]));
        Assert.Empty(RecentConnectionHistory.Update([], ["selected"]));
        Assert.Empty(RecentConnectionHistory.Update(["a"], []));
    }

    [Fact]
    public void ShellRemovalSurvivesRefreshButANewConnectionCanReintroduceTheNode()
    {
        var persisted = RecentConnectionHistory.Remove(["a", "b"], new HashSet<string> { "a" });
        Assert.Equal(["b"], RecentConnectionHistory.Update(persisted, ["a", "b"]));
        Assert.Equal(["a", "b"], RecentConnectionHistory.Update(persisted, ["a", "b"], "a"));
    }
}

using System.Net;
using FalconLoad.Web.Services;
using Microsoft.Extensions.Configuration;

namespace FalconLoad.Web.Tests;

public class AgentModeTests
{
    private const string Token = "a-long-random-agent-token";

    private static readonly IPAddress OtherMachine = IPAddress.Parse("192.168.1.20");

    private static AgentMode Mode(string? token) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AgentMode.SettingName] = token })
            .Build());

    [Fact]
    public void AgentModeOff_AllowsEveryone()
    {
        var mode = Mode(null);

        Assert.True(mode.Allows(OtherMachine, null));
        Assert.True(mode.Allows(IPAddress.Loopback, null));
    }

    [Fact]
    public void AgentModeOn_TurnsAwayOtherMachinesWithoutTheToken()
    {
        var mode = Mode(Token);

        Assert.False(mode.Allows(OtherMachine, null));
        Assert.False(mode.Allows(OtherMachine, "Bearer wrong-token"));
        Assert.False(mode.Allows(OtherMachine, Token));
        Assert.False(mode.Allows(IPAddress.Parse("::ffff:192.168.1.20"), null));
    }

    [Fact]
    public void AgentModeOn_LetsOtherMachinesInWithTheToken()
    {
        Assert.True(Mode(Token).Allows(OtherMachine, $"Bearer {Token}"));
    }

    [Fact]
    public void AgentModeOn_KeepsTheUiOpenOnTheAgentMachine()
    {
        var mode = Mode(Token);

        Assert.True(mode.Allows(IPAddress.Loopback, null));
        Assert.True(mode.Allows(IPAddress.IPv6Loopback, null));
        Assert.True(mode.Allows(IPAddress.Parse("::ffff:127.0.0.1"), null));
        Assert.True(mode.Allows(null, null));
    }
}

using System.Net;
using FalconLoad.Core.Distributed;

namespace FalconLoad.Core.Tests;

public class AgentClientTests
{
    [Fact]
    public async Task Probe_SendsTheBearerToken_AndMeasuresTheClockOffset()
    {
        var agent = new FakeAgent("agent-1", "s3cret") { ClockSkew = TimeSpan.FromSeconds(30) };
        var client = new AgentClient(new HttpClient(new FakeAgentNetwork(agent)));

        var probe = await client.ProbeAsync(agent.Endpoint());

        Assert.Equal("agent-1", probe.Info.Machine);
        Assert.Equal(0, probe.Info.RunningRuns);
        Assert.All(agent.Received, r => Assert.Equal("Bearer s3cret", r.Headers.Authorization!.ToString()));
        Assert.InRange(probe.ClockOffset.TotalSeconds, 25, 35);
    }

    [Fact]
    public async Task Errors_AreExplainedInPlainWords()
    {
        var agent = new FakeAgent("agent-1", "s3cret");
        var client = new AgentClient(new HttpClient(new FakeAgentNetwork(agent)));

        var wrongToken = await Assert.ThrowsAsync<AgentException>(() => client.ProbeAsync(agent.Endpoint("nope")));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
        Assert.Contains("rejected the token", wrongToken.Message);

        var unknownHost = await Assert.ThrowsAsync<AgentException>(() => client.ProbeAsync(new AgentEndpoint("agent-9", new Uri("http://agent-9:5299"), "s3cret")));
        Assert.Null(unknownHost.StatusCode);
        Assert.Contains("Can't reach agent-9", unknownHost.Message);

        var unknownRun = await Assert.ThrowsAsync<AgentException>(() => client.GetResultsAsync(agent.Endpoint(), Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, unknownRun.StatusCode);

        agent.RefusesStarts = true;
        var refused = await Assert.ThrowsAsync<AgentException>(() => client.StartAsync(agent.Endpoint(), new AgentStartRequest(new(), null, null)));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Concurrency must be between 1 and 10,000.", refused.Message);
    }

    [Fact]
    public async Task Probe_RejectsAnswersThatArentFromAnAgent()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, "<html>not falcon-load</html>");
        var client = new AgentClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<AgentException>(() => client.ProbeAsync(new AgentEndpoint("web", new Uri("http://web/"), "t")));

        Assert.Contains("didn't answer like a falcon-load agent", ex.Message);
        Assert.Equal("http://web/api/agent", handler.Received.First().Request.RequestUri!.ToString());
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using FalconLoad.Core.Distributed;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class OAuth2Tests
{
    private const string ClientSecret = "very-secret+value";
    private static readonly Uri TokenUrl = new("http://auth.test/oauth2/token");

    /// <summary>A clock that only moves when a test moves it.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = TimeSpan.FromHours(1).Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public TimeSpan Now => TimeSpan.FromTicks(GetTimestamp());

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>
    /// A token endpoint at <see cref="TokenUrl"/> that hands out <c>tok-1</c>, <c>tok-2</c>, … and an API that accepts
    /// the tokens it handed out until they expire or are revoked.
    /// </summary>
    private sealed class FakeAuthServer
    {
        private readonly Dictionary<string, TimeSpan> _issued = new();
        private readonly HashSet<string> _revoked = new();
        private readonly ManualTime? _time;
        private int _tokens;

        public FakeAuthServer(ManualTime? time = null, double? expiresIn = 3600)
        {
            _time = time;
            ExpiresIn = expiresIn;
            Handler = new FakeHandler(RespondAsync);
        }

        public FakeHandler Handler { get; }

        public double? ExpiresIn { get; set; }

        /// <summary>When set, the token endpoint answers with this instead of a token.</summary>
        public Func<HttpResponseMessage>? TokenFailure { get; set; }

        /// <summary>When set, a token request waits for it, to hold a token request in flight.</summary>
        public TaskCompletionSource? Hold { get; set; }

        /// <summary>Moves the clock after every API request, as if each one took this long.</summary>
        public TimeSpan ApiTime { get; set; }

        public List<(HttpRequestMessage Request, string? Body)> TokenRequests => [.. Handler.Received.Where(r => r.Request.RequestUri == TokenUrl)];

        public List<HttpRequestMessage> ApiRequests => [.. Handler.Received.Where(r => r.Request.RequestUri != TokenUrl).Select(r => r.Request)];

        public int TokensIssued => Volatile.Read(ref _tokens);

        public void Revoke(string token)
        {
            lock (_issued)
            {
                _revoked.Add(token);
            }
        }

        public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var now = _time?.Now ?? TimeSpan.Zero;
            if (request.RequestUri == TokenUrl)
            {
                if (Hold is { } hold)
                {
                    await hold.Task.WaitAsync(ct);
                }

                if (TokenFailure is { } failure)
                {
                    return failure();
                }

                var token = $"tok-{Interlocked.Increment(ref _tokens)}";
                lock (_issued)
                {
                    _issued[token] = now;
                }

                var body = ExpiresIn is { } e
                    ? JsonSerializer.Serialize(new { access_token = token, token_type = "Bearer", expires_in = e })
                    : JsonSerializer.Serialize(new { access_token = token, token_type = "Bearer" });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }

            var presented = request.Headers.Authorization is { Scheme: "Bearer" } a ? a.Parameter : null;
            bool valid;
            lock (_issued)
            {
                valid = presented is not null && _issued.TryGetValue(presented, out var issuedAt) && !_revoked.Contains(presented)
                    && (ExpiresIn is null || now < issuedAt + TimeSpan.FromSeconds(ExpiresIn.Value));
            }

            _time?.Advance(ApiTime);
            return new HttpResponseMessage(valid ? HttpStatusCode.OK : HttpStatusCode.Unauthorized) { Content = new StringContent("ok") };
        }
    }

    private static AuthSettings Auth(ClientAuthentication method = ClientAuthentication.BasicHeader) => new()
    {
        Type = AuthType.OAuth2ClientCredentials,
        TokenUrl = TokenUrl.ToString(),
        ClientId = "load test",
        ClientSecret = ClientSecret,
        Scope = "orders.read orders.write",
        Audience = "https://api.test",
        ClientAuthentication = method,
    };

    private static LoadTestPlan Plan(int requests = 20, int concurrency = 4, AuthSettings? auth = null) => new()
    {
        Name = "Orders",
        Auth = auth ?? Auth(),
        Requests = [new RequestDefinition { Url = "http://api.test/orders" }],
        Profile = new LoadProfile { Concurrency = concurrency, StopCondition = StopCondition.RequestCount, TotalRequests = requests },
    };

    private static async Task<LoadTestSnapshot> RunAsync(LoadTestPlan plan, FakeAuthServer server, TestEnvironment? environment = null, TimeProvider? time = null)
    {
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(server.Handler), environment, time);
        return await runner.RunAsync(cancellationToken: CancellationToken.None);
    }

    private static OAuth2TokenSource Source(FakeAuthServer server, ManualTime time) =>
        new(Auth().Resolve(null), new HttpMessageInvoker(server.Handler), TimeSpan.FromSeconds(5), time: time);

    [Fact]
    public async Task WebSocketHandshake_CarriesTheToken()
    {
        var server = new FakeAuthServer();
        var sockets = new FakeWebSocketConnector(m => m);
        var plan = Plan(requests: 3, concurrency: 1);
        plan.Requests[0] = new RequestDefinition { Protocol = RequestProtocol.WebSocket, Url = "ws://api.test/feed", Body = "hi", WebSocket = new WebSocketOptions() };
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(server.Handler), webSockets: sockets);

        var snapshot = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(3, snapshot.SuccessfulRequests);
        var (_, headers) = Assert.Single(sockets.Connections);
        Assert.Contains(headers, h => h.Key == "Authorization" && h.Value == "Bearer tok-1");
    }

    [Fact]
    public async Task Run_FetchesOneTokenAndSendsItWithEveryRequest()
    {
        var server = new FakeAuthServer();

        var snapshot = await RunAsync(Plan(), server);

        Assert.Equal(RunState.Completed, snapshot.State);
        Assert.Single(server.TokenRequests);
        Assert.Equal(20, server.ApiRequests.Count);
        Assert.All(server.ApiRequests, r => Assert.Equal("Bearer tok-1", r.Headers.Authorization!.ToString()));

        // The token request is not part of the load.
        Assert.Equal(20, snapshot.TotalRequests);
        Assert.Equal(0, snapshot.FailedRequests);
        Assert.Equal(new AuthStats(1, 0, null, 3600), snapshot.Auth);
    }

    [Fact]
    public async Task TokenRequest_SendsTheClientCredentialsInABasicHeaderByDefault()
    {
        var server = new FakeAuthServer();

        await RunAsync(Plan(requests: 1, concurrency: 1), server);

        var (request, body) = Assert.Single(server.TokenRequests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        // The id and secret are form-encoded before they are joined, so a space or a plus survives.
        Assert.Equal("load%20test:very-secret%2Bvalue", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
        Assert.Equal("grant_type=client_credentials&scope=orders.read+orders.write&audience=https%3A%2F%2Fapi.test", body);
    }

    [Fact]
    public async Task TokenRequest_CanSendTheClientCredentialsInTheBody()
    {
        var server = new FakeAuthServer();
        var auth = Auth(ClientAuthentication.RequestBody);
        auth.Scope = string.Empty;
        auth.Audience = string.Empty;

        await RunAsync(Plan(requests: 1, concurrency: 1, auth), server);

        var (request, body) = Assert.Single(server.TokenRequests);
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("grant_type=client_credentials&client_id=load+test&client_secret=very-secret%2Bvalue", body);
    }

    [Fact]
    public async Task ARequestWithItsOwnAuthorizationHeader_KeepsIt()
    {
        var server = new FakeAuthServer();
        var plan = Plan(requests: 2, concurrency: 1);
        plan.Requests.Add(new RequestDefinition { Url = "http://api.test/public", Headers = [new HeaderEntry { Name = "Authorization", Value = "ApiKey abc" }] });

        await RunAsync(plan, server);

        Assert.Equal("Bearer tok-1", server.ApiRequests[0].Headers.Authorization!.ToString());
        Assert.Equal("ApiKey abc", server.ApiRequests[1].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Token_IsReplacedInTheBackgroundBeforeItExpires()
    {
        var time = new ManualTime();
        var server = new FakeAuthServer(time, expiresIn: 100);
        using var source = Source(server, time);
        Assert.Equal("tok-1", (await source.FetchFirstAsync(CancellationToken.None)).Value);

        // A quarter of the lifetime before expiry the token is replaced; until then nothing is fetched.
        time.Advance(TimeSpan.FromSeconds(74));
        Assert.Equal("tok-1", (await source.GetAsync(CancellationToken.None)).Value);
        Assert.Null(source.Refreshing);

        // While the new token is on its way, requests keep the old one rather than wait.
        server.Hold = new TaskCompletionSource();
        time.Advance(TimeSpan.FromSeconds(2));
        var during = source.GetAsync(CancellationToken.None);
        Assert.True(during.IsCompleted);
        Assert.Equal("tok-1", (await during).Value);
        Assert.Equal("tok-1", (await source.GetAsync(CancellationToken.None)).Value);

        server.Hold.SetResult();
        await source.Refreshing!;
        Assert.Equal("tok-2", (await source.GetAsync(CancellationToken.None)).Value);
        Assert.Equal(2, server.TokensIssued);
        Assert.Equal(2, source.Stats.TokensFetched);
    }

    [Fact]
    public async Task ALongRun_NeverSendsAnExpiredToken()
    {
        // Each request takes 10 s on the test's clock and a token lasts 60 s, so a 300 s run needs several tokens.
        var time = new ManualTime();
        var server = new FakeAuthServer(time, expiresIn: 60) { ApiTime = TimeSpan.FromSeconds(10) };

        var snapshot = await RunAsync(Plan(requests: 30, concurrency: 1), server, time: time);

        Assert.Equal(30, snapshot.TotalRequests);
        Assert.Equal(0, snapshot.FailedRequests);
        Assert.InRange(snapshot.Auth!.TokensFetched, 5, 8);
    }

    [Fact]
    public async Task ARejectedToken_IsReplacedOnTheNextRequest()
    {
        var time = new ManualTime();
        var server = new FakeAuthServer(time) { ApiTime = TimeSpan.FromSeconds(11) };
        var sent = 0;
        var handler = new FakeHandler(async (request, ct) =>
        {
            if (request.RequestUri != TokenUrl && Interlocked.Increment(ref sent) == 3)
            {
                server.Revoke("tok-1");
            }

            return await server.RespondAsync(request, ct);
        });

        await using var runner = new LoadTestRunner(Plan(requests: 5, concurrency: 1), new HttpMessageInvoker(handler), null, time);
        var snapshot = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(1, snapshot.FailedRequests);
        Assert.Equal(2, snapshot.Auth!.TokensFetched);
        Assert.Equal(1, snapshot.StatusCodes["401"]);
    }

    [Fact]
    public async Task AFailedRefresh_KeepsTheOldTokenAndIsRetriedLater()
    {
        var time = new ManualTime();
        var server = new FakeAuthServer(time, expiresIn: 100);
        using var source = Source(server, time);
        await source.FetchFirstAsync(CancellationToken.None);

        server.TokenFailure = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        time.Advance(TimeSpan.FromSeconds(80));
        Assert.Equal("tok-1", (await source.GetAsync(CancellationToken.None)).Value);
        await source.Refreshing!;
        Assert.Equal(1, source.Stats.FailedFetches);
        Assert.Contains("503", source.Stats.LastError);

        // Requests right after the failure don't each ask again.
        await source.GetAsync(CancellationToken.None);
        await source.GetAsync(CancellationToken.None);
        Assert.Equal(2, server.TokenRequests.Count);

        server.TokenFailure = null;
        time.Advance(TimeSpan.FromSeconds(2));
        await source.GetAsync(CancellationToken.None);
        await source.Refreshing!;
        Assert.Equal("tok-2", (await source.GetAsync(CancellationToken.None)).Value);
        Assert.Equal(3, server.TokenRequests.Count);
    }

    [Fact]
    public async Task Run_FailsAtOnceWhenNoTokenCanBeFetched_AndTheSecretStaysHidden()
    {
        var server = new FakeAuthServer
        {
            TokenFailure = () => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent($$"""{ "error": "invalid_client", "error_description": "Secret {{ClientSecret}} is wrong" }"""),
            },
        };

        var snapshot = await RunAsync(Plan(), server);

        Assert.Equal(RunState.Failed, snapshot.State);
        Assert.Empty(server.ApiRequests);
        Assert.Equal(0, snapshot.TotalRequests);
        Assert.StartsWith("Couldn't get an OAuth2 token from http://auth.test/oauth2/token: 401 Unauthorized: invalid_client (Secret ****** is wrong)", snapshot.FailureReason);
        Assert.DoesNotContain(ClientSecret, snapshot.FailureReason);
        Assert.DoesNotContain(ClientSecret, snapshot.Auth!.LastError);
    }

    [Fact]
    public async Task Run_FailsWhenTheTokenResponseHasNoToken()
    {
        var server = new FakeAuthServer { TokenFailure = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") } };

        var snapshot = await RunAsync(Plan(), server);

        Assert.Equal(RunState.Failed, snapshot.State);
        Assert.Contains("the response has no access_token", snapshot.FailureReason);
    }

    [Fact]
    public async Task Tokens_AreMaskedInErrors()
    {
        // An error that quotes the token, here an extraction rule that names it, would otherwise show it in the errors table.
        var server = new FakeAuthServer();
        var plan = Plan(requests: 2, concurrency: 1);
        plan.Requests[0].Extractions = [new ExtractionRule { Variable = "id", Source = ExtractionSource.Regex, Expression = "tok-1(x)" }];

        var snapshot = await RunAsync(plan, server);

        Assert.NotEmpty(snapshot.Errors);
        Assert.All(snapshot.Errors, e => Assert.DoesNotContain("tok-1", e.Message));
    }

    [Fact]
    public async Task Probe_SendsTheTokenAndHidesItInTheResponse()
    {
        var server = new FakeAuthServer();
        var echo = new FakeHandler(async (request, ct) =>
        {
            if (request.RequestUri == TokenUrl)
            {
                return await server.RespondAsync(request, ct);
            }

            var header = request.Headers.Authorization!.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"you sent {header} and {ClientSecret}") };
            response.Headers.Add("X-Echo", header);
            return response;
        });

        var result = await RequestProbe.SendAsync(Plan().Requests[0], new LoadProfile(), new HttpMessageInvoker(echo), auth: Auth());

        Assert.True(result.Success);
        Assert.Equal("you sent Bearer ****** and ******", result.BodyPreview);
        Assert.Equal("Bearer ******", result.Headers.Single(h => h.Key == "X-Echo").Value);
    }

    [Fact]
    public async Task GetToken_ReportsTheLifetimeButNotTheToken()
    {
        var server = new FakeAuthServer(expiresIn: 300);

        var result = await RequestProbe.GetTokenAsync(Auth(), new LoadProfile(), invoker: new HttpMessageInvoker(server.Handler));

        Assert.True(result.Success);
        Assert.Equal(300, result.LifetimeSeconds);
        Assert.DoesNotContain("tok-1", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task ATokenWithoutExpiry_IsKeptUntilTheApiRejectsIt()
    {
        var time = new ManualTime();
        var server = new FakeAuthServer(time, expiresIn: null);
        using var source = Source(server, time);
        var first = await source.FetchFirstAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromDays(1));
        Assert.Same(first, await source.GetAsync(CancellationToken.None));

        source.Reject(first);
        Assert.Equal("tok-2", (await source.GetAsync(CancellationToken.None)).Value);
    }

    [Fact]
    public async Task AFreshToken_IsKeptWhenTheApiRejectsIt()
    {
        // A server that rejects every token would otherwise make every request fetch a new one.
        var time = new ManualTime();
        var server = new FakeAuthServer(time);
        using var source = Source(server, time);
        var first = await source.FetchFirstAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(5));
        source.Reject(first);

        Assert.Same(first, await source.GetAsync(CancellationToken.None));
        Assert.Equal(1, server.TokensIssued);
    }

    [Theory]
    [InlineData("""{ "access_token": "a", "expires_in": 60 }""", 60.0)]
    [InlineData("""{ "access_token": "a", "expires_in": "60" }""", 60.0)]
    [InlineData("""{ "access_token": "a" }""", null)]
    [InlineData("""{ "access_token": "a", "expires_in": 0 }""", null)]
    public void Parse_ReadsTheLifetime(string body, double? seconds)
    {
        var parsed = OAuth2TokenSource.Parse(body);

        Assert.Equal("a", parsed!.Value.Value);
        Assert.Equal(seconds, parsed.Value.Lifetime?.TotalSeconds);
    }

    [Fact]
    public void Validate_NeedsATokenUrlAndClientId()
    {
        var plan = Plan(auth: new AuthSettings { Type = AuthType.OAuth2ClientCredentials, TokenUrl = "not a url" });

        var problems = plan.Validate();

        Assert.Contains("Auth: the token URL must be an absolute http:// or https:// address.", problems);
        Assert.Contains("Auth: enter the client id.", problems);
        Assert.Empty(Plan(auth: new AuthSettings { Type = AuthType.None }).Validate());
    }

    [Fact]
    public async Task EnvironmentVariables_FillTheAuthFields_AndTheirSecretsStayHidden()
    {
        var auth = Auth();
        auth.TokenUrl = "{{authUrl}}/oauth2/token";
        auth.ClientSecret = "{{clientSecret}}";
        var plan = Plan(requests: 1, concurrency: 1, auth);
        var environment = new TestEnvironment
        {
            Name = "Staging",
            Variables =
            [
                new EnvironmentVariable { Name = "authUrl", Value = "http://auth.test" },
                new EnvironmentVariable { Name = "clientSecret", Value = "from-the-environment", Secret = true },
            ],
        };

        // Saving doesn't need the environment; running does.
        Assert.Empty(plan.Validate());
        Assert.Contains(plan.ValidateForRun(null), p => p.Contains("{{clientSecret}}") && p.Contains("{{authUrl}}"));
        Assert.Empty(plan.ValidateForRun(environment));

        var server = new FakeAuthServer
        {
            TokenFailure = () => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad secret from-the-environment") },
        };
        var snapshot = await RunAsync(plan, server, environment);

        var (request, _) = Assert.Single(server.TokenRequests);
        Assert.Equal("load%20test:from-the-environment", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!)));
        Assert.DoesNotContain("from-the-environment", snapshot.FailureReason);
    }

    [Fact]
    public void PlansWithoutAuth_StillLoad_AndAuthRoundTrips()
    {
        var old = PlanFile.Read("""{ "name": "Old", "requests": [{ "url": "http://api.test/" }] }""");
        Assert.Null(Assert.Single(old.Plans).Auth);

        var read = PlanFile.Read(PlanFile.Write([Plan()]));
        var auth = Assert.Single(read.Plans).Auth!;
        Assert.Equal(AuthType.OAuth2ClientCredentials, auth.Type);
        Assert.Equal("orders.read orders.write", auth.Scope);
        Assert.Contains("\"type\": \"OAuth2ClientCredentials\"", PlanFile.Write([Plan()]));
    }

    [Fact]
    public void AgentShares_KeepTheAuth()
    {
        var shares = LoadSplitter.Split(Plan(concurrency: 4), [1, 1]);

        Assert.All(shares, share => Assert.Equal(TokenUrl.ToString(), share.Plan.Auth!.TokenUrl));
    }

    [Fact]
    public async Task HtmlReport_CountsTheTokensButShowsNoSecret()
    {
        var server = new FakeAuthServer();
        await using var runner = new LoadTestRunner(Plan(requests: 2, concurrency: 1), new HttpMessageInvoker(server.Handler));
        await runner.RunAsync(cancellationToken: CancellationToken.None);
        var (snapshot, histograms) = runner.GetResults();
        var export = new RunExport { Plan = runner.Plan, Results = snapshot, Histograms = histograms, StartedAt = DateTimeOffset.UnixEpoch };

        var html = Reports.HtmlReport.Render(export, Reports.RunVerdict.Evaluate(snapshot, null));

        Assert.Contains("OAuth2, 1 token fetched", html);
        Assert.DoesNotContain("tok-1", html);
    }

    [Fact]
    public void CombinedResults_AddUpTheTokensOfEveryMachine()
    {
        RunExport Export(long fetched) => new()
        {
            Plan = Plan(),
            StartedAt = DateTimeOffset.UnixEpoch,
            Results = new MetricsCollector(["GET"]).Snapshot(RunState.Completed, TimeSpan.FromSeconds(1), 0) with { Auth = new AuthStats(fetched, 0, null, 60) },
            Histograms = new MetricsCollector(["GET"]).SnapshotWithHistograms(RunState.Completed, TimeSpan.FromSeconds(1), 0).Histograms,
        };

        var combined = ResultCombiner.Combine([Export(2), Export(3)]);

        Assert.Equal(5, combined.Combined.Auth!.TokensFetched);
    }
}

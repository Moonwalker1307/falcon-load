using System.Net;
using System.Text;
using System.Text.Json;
using FalconLoad.Core.Distributed;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;
using FalconLoad.Core.Telemetry;

namespace FalconLoad.Core.Tests;

public class TelemetryTests
{
    private static readonly Guid RunId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static async Task<RunTelemetry> RunOnce(LoadTestPlan plan, FakeHandler handler, TestEnvironment? environment = null, string machine = "load-1")
    {
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler), environment);
        await runner.RunAsync();
        return RunTelemetry.OnOneMachine(RunId, plan.Name, Start, runner.GetTelemetry(machine));
    }

    private static LoadTestPlan TwoRequests(int total = 20) => new()
    {
        Name = "Checkout",
        Requests =
        [
            new RequestDefinition { Url = "http://api.test/cart" },
            new RequestDefinition { Name = "Pay", Method = "POST", Url = "http://api.test/pay", Body = "{}", ExpectedStatusCodes = "201" },
        ],
        Profile = new LoadProfile { Concurrency = 2, StopCondition = StopCondition.RequestCount, TotalRequests = total },
    };

    // The cart answers 200 and payments 500, so the second request fails every time.
    private static FakeHandler CartOkPayFails() => new((request, _) => Task.FromResult(new HttpResponseMessage(
        request.RequestUri!.AbsolutePath == "/cart" ? HttpStatusCode.OK : HttpStatusCode.InternalServerError) { Content = new StringContent("x") }));

    private static HistogramData Histogram(params double[] latenciesMs)
    {
        var histogram = new LatencyHistogram();
        foreach (var ms in latenciesMs)
        {
            histogram.Record(TimeSpan.FromMilliseconds(ms));
        }

        return histogram.ToData();
    }

    private static MachineTelemetry Machine(string name, HistogramData latency, long failures = 0, int users = 3) => new(
        name,
        new LoadTestSnapshot
        {
            State = RunState.Running,
            Elapsed = TimeSpan.FromSeconds(10),
            TotalRequests = latency.Count,
            SuccessfulRequests = latency.Count - failures,
            FailedRequests = failures,
            ActiveVirtualUsers = users,
            BytesSent = 100,
            BytesReceived = 200,
            Latency = LatencyHistogram.FromData(latency).ToStats(),
            StatusCodes = new Dictionary<string, long> { ["200"] = latency.Count - failures, ["No response"] = failures },
            Errors = [],
            Steps = [new StepStats(0, "GET /items", latency.Count, failures, LatencyHistogram.FromData(latency).ToStats())],
            Timeline = [],
        },
        [latency]);

    private static RunTelemetry Run(string test, params MachineTelemetry[] machines) =>
        new(RunId, test, Start, RunState.Running, null, machines);

    [Fact]
    public async Task Prometheus_ReportsRequestsByOutcome_StatusCodes_AndALatencyHistogramPerRequest()
    {
        var run = await RunOnce(TwoRequests(), CartOkPayFails());

        var text = PrometheusText.Write(TelemetryMetrics.Build([run]));

        var labels = $"run_id=\"{RunId}\",test=\"Checkout\",machine=\"load-1\"";
        Assert.Contains("# TYPE falcon_load_requests_total counter", text);
        Assert.Contains($"falcon_load_requests_total{{{labels},request_index=\"0\",request=\"GET http://api.test/cart\",outcome=\"success\"}} 10\n", text);
        Assert.Contains($"falcon_load_requests_total{{{labels},request_index=\"0\",request=\"GET http://api.test/cart\",outcome=\"failure\"}} 0\n", text);
        Assert.Contains($"falcon_load_requests_total{{{labels},request_index=\"1\",request=\"Pay\",outcome=\"failure\"}} 10\n", text);
        Assert.Contains($"falcon_load_responses_total{{{labels},status_code=\"200\"}} 10\n", text);
        Assert.Contains($"falcon_load_responses_total{{{labels},status_code=\"500\"}} 10\n", text);
        Assert.Contains("# TYPE falcon_load_request_duration_seconds histogram", text);
        Assert.Contains($"falcon_load_request_duration_seconds_count{{{labels},request_index=\"1\",request=\"Pay\"}} 10\n", text);
        Assert.Contains($"falcon_load_request_duration_seconds_bucket{{{labels},request_index=\"1\",request=\"Pay\",le=\"+Inf\"}} 10\n", text);
        Assert.Contains($"falcon_load_sent_bytes_total{{{labels}}}", text);
        Assert.Contains($"falcon_load_run_active{{run_id=\"{RunId}\",test=\"Checkout\"}} 0\n", text);

        // Without thresholds there is no verdict to report.
        Assert.DoesNotContain("falcon_load_run_passed", text);

        // Buckets are cumulative, so they never shrink and the last one holds every request.
        var buckets = text.Split('\n')
            .Where(l => l.StartsWith("falcon_load_request_duration_seconds_bucket{", StringComparison.Ordinal) && l.Contains("request=\"Pay\""))
            .Select(l => long.Parse(l[(l.LastIndexOf(' ') + 1)..]))
            .ToList();
        Assert.Equal(TelemetryMetrics.LatencyBoundsSeconds.Count + 1, buckets.Count);
        Assert.Equal(buckets.Order(), buckets);
        Assert.Equal(10, buckets[^1]);
    }

    [Fact]
    public void Prometheus_EscapesLabelValues()
    {
        var text = PrometheusText.Write(TelemetryMetrics.Build([Run("Say \"hi\"\\\nbye", Machine("m", Histogram(5)))]));

        Assert.Contains("test=\"Say \\\"hi\\\"\\\\\\nbye\"", text);
    }

    [Fact]
    public void Prometheus_ReportsTheVerdictOfATestWithThresholds()
    {
        var run = Run("Smoke", Machine("m", Histogram(5))) with { Passed = false };

        var text = PrometheusText.Write(TelemetryMetrics.Build([run]));

        Assert.Contains($"falcon_load_run_passed{{run_id=\"{RunId}\",test=\"Smoke\"}} 0\n", text);
    }

    [Fact]
    public void Metrics_GiveEachAgentItsOwnSeries_ThatAddUpToTheWholeTest()
    {
        var run = Run("Spread", Machine("agent-1", Histogram(5, 6, 7)), Machine("agent-2", Histogram(50, 60), failures: 1));

        var families = TelemetryMetrics.Build([run]);

        var requests = families.Single(f => f.Name == "falcon_load_requests").Points;
        Assert.Equal(["agent-1", "agent-2"], requests.Select(p => p.Labels.Single(l => l.Key == "machine").Value).Distinct());
        Assert.Equal(5, requests.Sum(p => p.Value));
        Assert.Equal(1, requests.Where(p => p.Labels.Contains(new("outcome", "failure"))).Sum(p => p.Value));

        // Histograms add up bucket by bucket, so a dashboard's percentiles over all agents are those of one test.
        var duration = families.Single(f => f.Name == "falcon_load_request_duration").Points;
        Assert.Equal(5, duration.Sum(p => p.Buckets!.Sum()));
        Assert.Equal(0.128, duration.Sum(p => p.Sum), 6);
        Assert.Equal(6, families.Single(f => f.Name == "falcon_load_virtual_users").Points.Sum(p => p.Value));
        var responses = families.Single(f => f.Name == "falcon_load_responses").Points.Where(p => p.Labels.Contains(new("machine", "agent-2"))).ToList();
        Assert.Equal(["200", "none"], responses.Select(p => p.Labels.Single(l => l.Key == "status_code").Value).Order());
    }

    [Fact]
    public void Rebucket_SortsSamplesIntoTheCoarseBuckets()
    {
        var buckets = LatencyHistogram.Rebucket(Histogram(0.5, 3, 3, 40, 900, 120_000), [1, 5, 50, 1000]);

        Assert.Equal([1, 2, 1, 1, 1], buckets);
    }

    [Fact]
    public void Since_HoldsOnlyTheSamplesRecordedInBetween()
    {
        var histogram = new LatencyHistogram();
        histogram.Record(TimeSpan.FromMilliseconds(10));
        histogram.Record(TimeSpan.FromMilliseconds(500));
        var before = histogram.ToData();
        histogram.Record(TimeSpan.FromMilliseconds(100));
        histogram.Record(TimeSpan.FromMilliseconds(200));

        var added = LatencyHistogram.Since(histogram.ToData(), before);

        Assert.Equal(2, added.Count);
        Assert.Equal(150, added.MeanMs, 6);
        Assert.InRange(added.Percentile(50), 99, 101);
        Assert.InRange(added.MaxMs, 198, 202);

        // An earlier copy with more samples than now belongs to another run, so everything counts.
        Assert.Equal(4, LatencyHistogram.Since(histogram.ToData(), Histogram(1, 1, 1, 1, 1)).Count);
    }

    [Fact]
    public void Otlp_WritesCumulativeSumsGaugesAndHistograms_AsTheJsonEncodingAsks()
    {
        var run = Run("Spread", Machine("agent-1", Histogram(5, 6, 7)), Machine("agent-2", Histogram(50, 60)));
        var now = Start.AddSeconds(10);

        using var json = JsonDocument.Parse(OtlpJson.Write(TelemetryMetrics.Build([run]), now, "controller", "1.2.3"));

        var resource = json.RootElement.GetProperty("resourceMetrics")[0];
        var attributes = resource.GetProperty("resource").GetProperty("attributes").EnumerateArray()
            .ToDictionary(a => a.GetProperty("key").GetString()!, a => a.GetProperty("value").GetProperty("stringValue").GetString());
        Assert.Equal("falcon-load", attributes["service.name"]);
        Assert.Equal("controller", attributes["service.instance.id"]);

        var metrics = resource.GetProperty("scopeMetrics")[0].GetProperty("metrics").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!);
        var requests = metrics["falcon_load_requests"];
        Assert.Equal("{request}", requests.GetProperty("unit").GetString());
        Assert.True(requests.GetProperty("sum").GetProperty("isMonotonic").GetBoolean());
        Assert.Equal(2, requests.GetProperty("sum").GetProperty("aggregationTemporality").GetInt32());
        var point = requests.GetProperty("sum").GetProperty("dataPoints")[0];
        Assert.Equal("3", point.GetProperty("asInt").GetString());
        Assert.Equal("1793448000000000000", point.GetProperty("startTimeUnixNano").GetString());
        Assert.Equal("1793448010000000000", point.GetProperty("timeUnixNano").GetString());

        var histogram = metrics["falcon_load_request_duration"];
        Assert.Equal("s", histogram.GetProperty("unit").GetString());
        var histogramPoint = histogram.GetProperty("histogram").GetProperty("dataPoints")[1];
        Assert.Equal("2", histogramPoint.GetProperty("count").GetString());
        Assert.Equal(0.11, histogramPoint.GetProperty("sum").GetDouble(), 6);
        Assert.Equal(TelemetryMetrics.LatencyBoundsSeconds.Count, histogramPoint.GetProperty("explicitBounds").GetArrayLength());
        var counts = histogramPoint.GetProperty("bucketCounts").EnumerateArray().Select(c => long.Parse(c.GetString()!)).ToList();
        Assert.Equal(TelemetryMetrics.LatencyBoundsSeconds.Count + 1, counts.Count);
        Assert.Equal(2, counts.Sum());

        Assert.Equal(3, metrics["falcon_load_virtual_users"].GetProperty("gauge").GetProperty("dataPoints")[0].GetProperty("asDouble").GetDouble());
    }

    [Fact]
    public void Influx_ReportsEachIntervalsThroughputAndPercentiles_WithAgentsMerged()
    {
        var influx = new InfluxLineProtocol();
        var first = influx.Write([Run("Spread", Machine("agent-1", Histogram(10, 10)), Machine("agent-2", Histogram(10, 10)))], Start.AddSeconds(5));

        // Five seconds later both agents have answered two more requests each, far slower than before.
        var second = influx.Write(
            [Run("Spread", Machine("agent-1", Histogram(10, 10, 300, 300)), Machine("agent-2", Histogram(10, 10, 300, 300), failures: 1))],
            Start.AddSeconds(10));

        Assert.Contains("rps=0.8,interval_requests=4i", Line(first, "falcon_load,"));
        var run = Line(second, "falcon_load,");
        Assert.StartsWith($"falcon_load,run_id={RunId},test=Spread requests=8i,failures=1i,virtual_users=6i,", run);
        Assert.Contains("rps=0.8,interval_requests=4i,interval_failures=1i,error_percent=25,", run);
        Assert.InRange(Field(run, "latency_p50_ms"), 297, 303);
        Assert.InRange(Field(run, "latency_mean_ms"), 299.9, 300.1);
        Assert.EndsWith(" 1793448010000", run);

        var agent = Line(second, "falcon_load_machine,run_id=" + RunId + ",test=Spread,machine=agent-2 ");
        Assert.Contains("requests=4i,failures=1i,virtual_users=3i,rps=0.4,interval_requests=2i,interval_failures=1i,error_percent=50", agent);
        Assert.Contains($"falcon_load_request,run_id={RunId},test=Spread,request_index=0,request=GET\\ /items requests=8i,failures=1i", second);
        Assert.Contains($"falcon_load_status,run_id={RunId},test=Spread,status_code=200 count=7i", second);
        Assert.Contains($"falcon_load_status,run_id={RunId},test=Spread,status_code=none count=1i", second);
    }

    [Fact]
    public void Influx_EscapesTags_AndLeavesOutPercentilesOfAQuietInterval()
    {
        var influx = new InfluxLineProtocol();
        var run = Run("Orders, slow=yes", Machine("m", Histogram(10)));
        influx.Write([run], Start.AddSeconds(5));

        var quiet = influx.Write([run], Start.AddSeconds(10));

        var line = Line(quiet, "falcon_load,");
        Assert.StartsWith($"falcon_load,run_id={RunId},test=Orders\\,\\ slow\\=yes ", line);
        Assert.Contains("rps=0,interval_requests=0i,interval_failures=0i,running=true", line);
        Assert.DoesNotContain("latency_p95_ms", line);
    }

    [Fact]
    public async Task Publisher_PushesRunningTests_AndEachFinishedOneOnceMore()
    {
        var pushes = new List<(HttpRequestMessage Request, string? Body)>();
        var receiver = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        var settings = new TelemetrySettings
        {
            InfluxDb = new InfluxDbSettings { Enabled = true, Url = "http://influx.test:8086/", Bucket = "load tests", Organization = "MyOrg", Token = "influx-secret" },
            Otlp = new OtlpSettings { Enabled = true, Endpoint = "http://collector.test:4318", Headers = [new TelemetryHeader { Name = "Authorization", Value = "Basic abc123" }] },
        };
        var current = Run("Smoke", Machine("m", Histogram(5)));
        var publisher = new TelemetryPublisher(() => settings, () => [current], new HttpMessageInvoker(receiver), "this-machine", "1.0");

        await publisher.PushAsync();
        current = current with { State = RunState.Completed };
        await publisher.PushAsync();
        await publisher.PushAsync();

        var received = receiver.Received.ToList();
        var influx = received.Where(r => r.Request.RequestUri!.Host == "influx.test").ToList();
        var otlp = received.Where(r => r.Request.RequestUri!.Host == "collector.test").ToList();
        Assert.Equal(2, influx.Count);
        Assert.Equal(2, otlp.Count);
        Assert.Equal("http://influx.test:8086/api/v2/write?bucket=load%20tests&precision=ms&org=MyOrg", influx[0].Request.RequestUri!.AbsoluteUri);
        Assert.Equal("Token influx-secret", influx[0].Request.Headers.Authorization!.ToString());
        Assert.Contains("running=true", influx[0].Body);
        Assert.Contains("running=false", influx[1].Body);
        Assert.Equal("http://collector.test:4318/v1/metrics", otlp[0].Request.RequestUri!.AbsoluteUri);
        Assert.Equal("Basic abc123", otlp[0].Request.Headers.Authorization!.ToString());
        Assert.Equal("application/json", otlp[0].Request.Content!.Headers.ContentType!.MediaType);
        Assert.Null(publisher.InfluxDbStatus.LastError);
        Assert.NotNull(publisher.OtlpStatus.LastSuccess);
        Assert.Equal(1, publisher.OtlpStatus.Runs);
    }

    [Fact]
    public async Task Publisher_SendsARunShorterThanTheInterval_ButNotRunsThatEndedBeforeIt()
    {
        var receiver = FakeHandler.Returning(HttpStatusCode.NoContent);
        var settings = new TelemetrySettings { Otlp = new OtlpSettings { Enabled = true, Endpoint = "http://collector.test:4318/v1/metrics" } };
        var old = Run("Old", Machine("m", Histogram(5))) with { RunId = Guid.NewGuid(), State = RunState.Completed, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3) };
        var runs = new List<RunTelemetry> { old };
        var publisher = new TelemetryPublisher(() => settings, () => runs, new HttpMessageInvoker(receiver), "m", "1.0");

        await publisher.PushAsync();
        runs.Add(Run("Quick", Machine("m", Histogram(5))) with { State = RunState.Completed, StartedAt = DateTimeOffset.UtcNow });
        await publisher.PushAsync();

        var body = Assert.Single(receiver.Received).Body;
        Assert.Contains("Quick", body);
        Assert.DoesNotContain("Old", body);
        Assert.Equal("http://collector.test:4318/v1/metrics", receiver.Received.Single().Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Publisher_ReportsAFailedPush_WithTheTokenMasked()
    {
        var receiver = FakeHandler.Returning(HttpStatusCode.Unauthorized, "token influx-secret is not valid");
        var settings = new TelemetrySettings
        {
            InfluxDb = new InfluxDbSettings { Enabled = true, Url = "http://influx.test:8086", Bucket = "b", Token = "influx-secret" },
        };
        var logged = new List<string>();
        var publisher = new TelemetryPublisher(() => settings, () => [Run("Smoke", Machine("m", Histogram(5)))], new HttpMessageInvoker(receiver), "m", "1.0", logged.Add);

        await publisher.PushAsync();
        await publisher.PushAsync();

        var error = publisher.InfluxDbStatus.LastError;
        Assert.NotNull(error);
        Assert.StartsWith("InfluxDB answered 401 Unauthorized", error);
        Assert.Contains(SecretMasker.Mask, error);
        Assert.DoesNotContain("influx-secret", error);
        Assert.Null(publisher.InfluxDbStatus.LastSuccess);

        // The same error twice is logged once.
        Assert.Single(logged);
    }

    [Fact]
    public async Task Publisher_ReportsAnUnreachableEndpoint()
    {
        var receiver = new FakeHandler((_, _) => throw new HttpRequestException("Connection refused"));
        var settings = new TelemetrySettings { Otlp = new OtlpSettings { Enabled = true, Endpoint = "http://collector.test:4318" } };
        var publisher = new TelemetryPublisher(() => settings, () => [Run("Smoke", Machine("m", Histogram(5)))], new HttpMessageInvoker(receiver), "m", "1.0");

        await publisher.PushAsync();

        Assert.Equal("Couldn't reach the OTLP endpoint: Connection refused", publisher.OtlpStatus.LastError);
    }

    [Fact]
    public async Task Exports_NeverContainSecretValues_OfTheEnvironment()
    {
        var environment = new TestEnvironment
        {
            Name = "Staging",
            Variables =
            [
                new EnvironmentVariable { Name = "base", Value = "http://secret-host.test", Secret = true },
                new EnvironmentVariable { Name = "token", Value = "s3cr3t-token", Secret = true },
            ],
        };
        var plan = new LoadTestPlan
        {
            Name = "Secret API",
            Requests =
            [
                new RequestDefinition
                {
                    Url = "{{base}}/orders?key={{token}}",
                    Headers = [new HeaderEntry { Name = "Authorization", Value = "Bearer {{token}}" }],
                    Assertions = [new Assertion { Source = AssertionSource.Body, Operator = AssertionOperator.Contains, Value = "{{token}}" }],
                },
            ],
            Profile = new LoadProfile { Concurrency = 1, StopCondition = StopCondition.RequestCount, TotalRequests = 5 },
        };

        // The server echoes the token in an error status, as a misbehaving API might.
        var run = await RunOnce(plan, FakeHandler.Returning(HttpStatusCode.Forbidden, "bad token s3cr3t-token"), environment);
        var families = TelemetryMetrics.Build([run]);
        var outputs = new[]
        {
            PrometheusText.Write(families),
            Encoding.UTF8.GetString(OtlpJson.Write(families, DateTimeOffset.UtcNow, "m", "1.0")),
            new InfluxLineProtocol().Write([run], DateTimeOffset.UtcNow),
        };

        Assert.Equal(5, run.Machines[0].Results.FailedRequests);
        Assert.All(outputs, output =>
        {
            Assert.DoesNotContain("s3cr3t-token", output);
            Assert.DoesNotContain("secret-host", output);
            Assert.Contains("{{base}}/orders?key", output);
            Assert.Contains("={{token}}", output);
        });
    }

    [Fact]
    public async Task DistributedRun_ExportsOneSeriesPerAgent_WithoutTheEnvironmentsSecret()
    {
        var one = new FakeAgent("agent-1", "t1");
        var two = new FakeAgent("agent-2", "t2");
        var environment = new TestEnvironment { Name = "Staging", Variables = [new EnvironmentVariable { Name = "base", Value = "http://hidden-api.test", Secret = true }] };
        var plan = new LoadTestPlan
        {
            Name = "Products",
            Requests = [new RequestDefinition { Url = "{{base}}/items" }],
            Profile = new LoadProfile { Concurrency = 4, StopCondition = StopCondition.RequestCount, TotalRequests = 40 },
        };
        var options = new DistributedRunnerOptions { StartDelay = TimeSpan.FromMilliseconds(100), PollInterval = TimeSpan.FromMilliseconds(50) };
        var runner = new DistributedRunner(plan, [one.Endpoint(), two.Endpoint()], [1, 1], new AgentClient(new HttpClient(new FakeAgentNetwork(one, two))), options, environment);

        var result = await runner.RunAsync();
        var telemetry = new RunTelemetry(RunId, plan.Name, result.StartedAt!.Value, result.State, result.Results.Passed,
            runner.GetAgentExports().Select(MachineTelemetry.From).OfType<MachineTelemetry>().ToList());
        var text = PrometheusText.Write(TelemetryMetrics.Build([telemetry]));

        Assert.Contains($"falcon_load_requests_total{{run_id=\"{RunId}\",test=\"Products\",machine=\"agent-1\",request_index=\"0\",request=\"GET {{{{base}}}}/items\",outcome=\"success\"}} 20\n", text);
        Assert.Contains($"falcon_load_requests_total{{run_id=\"{RunId}\",test=\"Products\",machine=\"agent-2\",request_index=\"0\",request=\"GET {{{{base}}}}/items\",outcome=\"success\"}} 20\n", text);
        Assert.Contains($"falcon_load_request_duration_seconds_count{{run_id=\"{RunId}\",test=\"Products\",machine=\"agent-2\",request_index=\"0\",request=\"GET {{{{base}}}}/items\"}} 20\n", text);
        Assert.DoesNotContain("hidden-api", text);
    }

    [Fact]
    public void Settings_RedactSecrets_AndKeepThemWhenSavedBackUnchanged()
    {
        var stored = new TelemetrySettings
        {
            InfluxDb = new InfluxDbSettings { Enabled = true, Url = "http://influx.test", Bucket = "b", Token = "influx-secret" },
            Otlp = new OtlpSettings { Enabled = true, Endpoint = "http://c.test", Headers = [new TelemetryHeader { Name = "Authorization", Value = "Basic abc" }, new TelemetryHeader { Name = "X-Empty", Value = "" }] },
        };

        var redacted = stored.Redacted();
        Assert.Null(redacted.InfluxDb.Token);
        Assert.Null(redacted.Otlp.Headers[0].Value);
        Assert.Equal(string.Empty, redacted.Otlp.Headers[1].Value);
        Assert.DoesNotContain("influx-secret", JsonSerializer.Serialize(redacted));
        Assert.DoesNotContain("Basic abc", JsonSerializer.Serialize(redacted));

        redacted.Otlp.Headers.Add(new TelemetryHeader { Name = "X-Scope-OrgID", Value = null });
        var saved = redacted.KeepSecretsFrom(stored);
        Assert.Equal("influx-secret", saved.InfluxDb.Token);
        Assert.Equal("Basic abc", saved.Otlp.Headers[0].Value);
        Assert.Equal(string.Empty, saved.Otlp.Headers[2].Value);
    }

    [Fact]
    public void Settings_Validate()
    {
        Assert.Empty(new TelemetrySettings().Validate());

        var settings = new TelemetrySettings
        {
            PushIntervalSeconds = 0,
            InfluxDb = new InfluxDbSettings { Enabled = true, Url = "localhost:8086" },
            Otlp = new OtlpSettings { Enabled = true, Endpoint = "ftp://c", Headers = [new TelemetryHeader { Name = "Bad Name" }, new TelemetryHeader { Name = "A" }, new TelemetryHeader { Name = "a" }] },
        };

        var problems = settings.Validate();
        Assert.Contains(problems, p => p.Contains("push interval"));
        Assert.Contains(problems, p => p.Contains("InfluxDB URL"));
        Assert.Contains(problems, p => p.Contains("bucket"));
        Assert.Contains(problems, p => p.Contains("OTLP endpoint"));
        Assert.Contains(problems, p => p.Contains("'Bad Name' isn't a valid header name"));
        Assert.Contains(problems, p => p.Contains("header a is given twice"));
    }

    private static string Line(string text, string prefix) =>
        text.Split('\n').Single(l => l.StartsWith(prefix, StringComparison.Ordinal));

    private static double Field(string line, string name)
    {
        var start = line.IndexOf(name + "=", StringComparison.Ordinal) + name.Length + 1;
        var end = line.IndexOfAny([',', ' '], start);
        return double.Parse(line[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }
}

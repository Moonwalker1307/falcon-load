using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Distributed;

/// <summary>Talks to an agent's <c>/api/agent</c> endpoints with its bearer token.</summary>
public sealed class AgentClient(HttpClient http)
{
    public static readonly JsonSerializerOptions JsonOptions = PlanFile.JsonOptions;

    private const int ProbeAttempts = 3;

    /// <summary>
    /// Asks the agent about itself and measures how far its clock is from this machine's. The agent is asked a few
    /// times and the quickest answer is used, since the clock reading is most accurate when the round trip is shortest.
    /// </summary>
    public async Task<AgentProbe> ProbeAsync(AgentEndpoint agent, CancellationToken cancellationToken = default)
    {
        AgentProbe? best = null;
        for (var attempt = 0; attempt < ProbeAttempts; attempt++)
        {
            var sent = DateTimeOffset.UtcNow;
            var start = Stopwatch.GetTimestamp();
            var info = await SendAsync<AgentInfo>(agent, HttpMethod.Get, "api/agent", null, cancellationToken).ConfigureAwait(false);
            var roundTrip = Stopwatch.GetElapsedTime(start);

            // The agent read its clock somewhere during the round trip; halfway is the best guess.
            var probe = new AgentProbe(info, roundTrip, info.Time - (sent + roundTrip / 2));
            if (best is null || probe.RoundTrip < best.RoundTrip)
            {
                best = probe;
            }
        }

        return best!;
    }

    public Task<AgentRunStarted> StartAsync(AgentEndpoint agent, AgentStartRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AgentRunStarted>(agent, HttpMethod.Post, "api/agent/runs", request, cancellationToken);

    /// <summary>The run's results so far, with the histograms needed to merge them with other agents' results.</summary>
    public Task<RunExport> GetResultsAsync(AgentEndpoint agent, Guid runId, CancellationToken cancellationToken = default) =>
        SendAsync<RunExport>(agent, HttpMethod.Get, $"api/agent/runs/{runId}", null, cancellationToken);

    public Task StopAsync(AgentEndpoint agent, Guid runId, CancellationToken cancellationToken = default) =>
        SendAsync<object?>(agent, HttpMethod.Post, $"api/agent/runs/{runId}/stop", null, cancellationToken);

    private async Task<T> SendAsync<T>(AgentEndpoint agent, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var url = new Uri(WithTrailingSlash(agent.BaseUrl), path);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agent.Token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AgentException($"Can't reach {agent.Name} at {url}: {ex.InnerException?.Message ?? ex.Message}", null, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentException($"{agent.Name} at {url} didn't answer within {http.Timeout.TotalSeconds:0} seconds.", null, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new AgentException(await DescribeAsync(agent, response, cancellationToken).ConfigureAwait(false), response.StatusCode);
            }

            if (typeof(T) == typeof(object) || response.StatusCode == HttpStatusCode.NoContent)
            {
                return default!;
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new AgentException($"{agent.Name} sent an empty answer to {method} {path}.", response.StatusCode);
            }
            catch (JsonException ex)
            {
                throw new AgentException($"{agent.Name} at {url} didn't answer like a falcon-load agent. Is the URL right?", response.StatusCode, ex);
            }
        }
    }

    private static async Task<string> DescribeAsync(AgentEndpoint agent, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var detail = await ProblemDetailAsync(response, cancellationToken).ConfigureAwait(false);
        var code = (int)response.StatusCode;
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => $"{agent.Name} rejected the token. Check that it matches the agent's FalconLoad:AgentToken setting.",
            HttpStatusCode.Forbidden => $"{agent.Name}: {detail ?? "agent mode is off on that falcon-load."}",
            HttpStatusCode.NotFound when detail is null => $"{agent.Name} has no /api/agent endpoint. Is it a falcon-load with agent mode, and is the URL right?",
            HttpStatusCode.NotFound => $"{agent.Name}: {detail}",
            _ => $"{agent.Name} answered {code} {response.ReasonPhrase}{(detail is null ? "" : ": " + detail)}",
        };
    }

    /// <summary>The message of a problem-details answer, including validation errors, or null when there is none.</summary>
    private static async Task<string?> ProblemDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType is not ("application/problem+json" or "application/json"))
        {
            return null;
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var parts = new List<string>();
            if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
            {
                parts.Add(detail.GetString()!);
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                parts.AddRange(errors.EnumerateObject()
                    .SelectMany(e => e.Value.ValueKind == JsonValueKind.Array ? e.Value.EnumerateArray().Select(v => v.GetString()) : [e.Value.ToString()])
                    .OfType<string>());
            }

            if (parts.Count == 0 && root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            {
                parts.Add(title.GetString()!);
            }

            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Uri WithTrailingSlash(Uri url) =>
        url.AbsolutePath.EndsWith('/') ? url : new Uri(url.GetLeftPart(UriPartial.Path) + "/");
}

/// <summary>An agent couldn't be reached, refused the request or answered with an error.</summary>
public sealed class AgentException(string message, HttpStatusCode? statusCode, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

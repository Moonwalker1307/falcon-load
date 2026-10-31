using System.Diagnostics;
using System.Text;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Engine;

/// <summary>The response to a single test request, used to check a request before load testing with it.</summary>
/// <param name="Error">Why nothing came back, or, with a status code, what the protocol found wrong with the response, such as a GraphQL error.</param>
public sealed record ProbeResult(
    int? StatusCode,
    string? ReasonPhrase,
    TimeSpan Latency,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string BodyPreview,
    long BodyBytes,
    bool Success,
    string? Error,
    IReadOnlyList<ExtractedValue>? Extractions = null)
{
    /// <summary>What each of the request's checks saw in the response. Empty when it has none or nothing came back.</summary>
    public IReadOnlyList<CheckOutcome> Checks { get; init; } = [];

    /// <summary>Whether the status matched the expected codes, regardless of the checks.</summary>
    public bool StatusMatches { get; init; }
}

/// <summary>The outcome of asking the token endpoint for a token. The token itself is never part of it.</summary>
/// <param name="LifetimeSeconds">The token's <c>expires_in</c>; null when the server gave none.</param>
public sealed record TokenProbeResult(bool Success, string? Error, TimeSpan Latency, double? LifetimeSeconds);

/// <summary>
/// Sends one request and captures the response, so users can verify a request before load testing. Parameter lists
/// supply their first value, the environment, if any, supplies its variables, and the request's extraction rules are
/// tried on the response. With OAuth2 auth, a token is fetched first and sent as the test would send it. The
/// environment's secret values, the client secret and the token are hidden in what comes back, in case the server echoes them.
/// </summary>
public static class RequestProbe
{
    private const int MaxPreviewBytes = 16 * 1024;

    /// <summary>Asks the token endpoint for a token, to check the auth settings before a run.</summary>
    public static async Task<TokenProbeResult> GetTokenAsync(
        AuthSettings auth,
        LoadProfile profile,
        TestEnvironment? environment = null,
        HttpMessageInvoker? invoker = null,
        CancellationToken cancellationToken = default)
    {
        var problems = auth.Validate(environment?.ToValues(), forRun: true).ToList();
        if (!auth.IsEnabled || problems.Count > 0)
        {
            return new TokenProbeResult(false, auth.IsEnabled ? string.Join(" ", problems) : "Auth is off for this test.", TimeSpan.Zero, null);
        }

        var owned = invoker is null;
        invoker ??= new HttpMessageInvoker(HttpHandlerFactory.Create(profile), disposeHandler: true);
        var masker = environment?.CreateMasker() ?? SecretMasker.None;
        using var tokens = new OAuth2TokenSource(auth.Resolve(environment?.ToValues()), invoker, TimeSpan.FromSeconds(profile.TimeoutSeconds), masker);
        var start = Stopwatch.GetTimestamp();
        try
        {
            var token = await tokens.FetchFirstAsync(cancellationToken).ConfigureAwait(false);
            return new TokenProbeResult(true, null, Stopwatch.GetElapsedTime(start), token.Lifetime?.TotalSeconds);
        }
        catch (OAuth2TokenException ex)
        {
            return new TokenProbeResult(false, ex.Message, Stopwatch.GetElapsedTime(start), null);
        }
        finally
        {
            if (owned)
            {
                invoker.Dispose();
            }
        }
    }

    /// <param name="variables">
    /// Extracted variables for placeholders, compared without regard to case. Values this request extracts are added.
    /// </param>
    public static async Task<ProbeResult> SendAsync(
        RequestDefinition definition,
        LoadProfile profile,
        HttpMessageInvoker? invoker = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ParameterList>? parameters = null,
        Dictionary<string, string>? variables = null,
        TestEnvironment? environment = null,
        IReadOnlyList<DataFile>? dataFiles = null,
        AuthSettings? auth = null)
    {
        var set = new ParameterSet(parameters ?? [], dataFiles);
        var secrets = environment?.CreateMasker() ?? SecretMasker.None;
        auth = auth is { IsEnabled: true } ? auth : null;

        // Parameter lists and variables already extracted win over an environment variable of the same name.
        var values = new Dictionary<string, string>(environment?.ToValues() ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in set.Names.Concat(variables?.Keys ?? Enumerable.Empty<string>()))
        {
            values.Remove(name);
        }

        var problems = definition.Validate(set, variables?.Keys.ToList(), values, forRun: true).ToList();
        var prepared = PreparedRequest.From(definition, values, auth is not null);
        if (prepared.UsesAuth)
        {
            problems.AddRange(auth!.Validate(environment?.ToValues(), forRun: true));
        }

        if (problems.Count > 0)
        {
            return new ProbeResult(null, null, TimeSpan.Zero, [], string.Empty, 0, false, string.Join(" ", problems));
        }

        var owned = invoker is null;
        invoker ??= new HttpMessageInvoker(HttpHandlerFactory.Create(profile), disposeHandler: true);
        using var tokens = prepared.UsesAuth
            ? new OAuth2TokenSource(auth!.Resolve(environment?.ToValues()), invoker, TimeSpan.FromSeconds(profile.TimeoutSeconds), secrets)
            : null;
        Func<string?, string?> masker = text => text is null ? null : tokens?.Mask(text) ?? secrets.Apply(text);
        var start = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));

        try
        {
            // Variables from earlier probes fill in placeholders, and this probe's extractions are added to them, so
            // sending a login request once lets the next "Send once" use its token.
            variables ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var token = tokens is null ? null : await tokens.FetchFirstAsync(timeout.Token).ConfigureAwait(false);
            start = Stopwatch.GetTimestamp();
            using var request = prepared.Build(new TemplateContext(1, 1, set.First(), variables), out _, token?.Header);
            using var response = await invoker.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            var latency = Stopwatch.GetElapsedTime(start);

            var headers = response.Headers.Concat(response.Content.Headers)
                .Select(h => new KeyValuePair<string, string>(h.Key, masker(string.Join(", ", h.Value))!))
                .ToList();
            var preview = masker(Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxPreviewBytes)))!;
            if (bytes.Length > MaxPreviewBytes)
            {
                preview += $"\n… ({bytes.Length - MaxPreviewBytes:N0} more bytes)";
            }

            var code = (int)response.StatusCode;
            var statusMatches = prepared.Matcher.IsMatch(code);
            var protocolError = statusMatches ? prepared.ProtocolError(bytes, bytes.Length > Assertion.MaxBodyBytes) : null;
            var checks = prepared.Checker.Explain(response, bytes);
            List<ExtractedValue>? extractions = null;
            if (prepared.Extractor is { } extractor)
            {
                extractions = [];
                extractor.Apply(response, bytes.AsSpan(0, Math.Min(bytes.Length, ResponseExtractor.MaxBodyBytes)), variables, extractions);
            }

            return new ProbeResult(code, masker(response.ReasonPhrase), latency, headers, preview, bytes.Length, statusMatches && protocolError is null && checks.All(c => c.Passed), masker(protocolError), extractions)
            {
                Checks = [.. checks.Select(c => c with { Actual = masker(c.Actual) })],
                StatusMatches = statusMatches,
            };
        }
        catch (OAuth2TokenException ex)
        {
            return Failed(ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"Timeout after {profile.TimeoutSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            return Failed(ex.InnerException is null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})");
        }
        catch (Exception ex) when (ex is IOException or FormatException or UriFormatException or InvalidOperationException)
        {
            return Failed(ex.Message);
        }
        finally
        {
            if (owned)
            {
                invoker.Dispose();
            }
        }

        ProbeResult Failed(string error) =>
            new(null, null, Stopwatch.GetElapsedTime(start), [], string.Empty, 0, false, masker(error));
    }
}


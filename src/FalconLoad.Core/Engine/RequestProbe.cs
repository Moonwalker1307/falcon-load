using System.Diagnostics;
using System.Text;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Engine;

/// <summary>The response to a single test request, used to check a request before load testing with it.</summary>
public sealed record ProbeResult(
    int? StatusCode,
    string? ReasonPhrase,
    TimeSpan Latency,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string BodyPreview,
    long BodyBytes,
    bool Success,
    string? Error,
    IReadOnlyList<ExtractedValue>? Extractions = null);

/// <summary>
/// Sends one request and captures the response, so users can verify a request before load testing. Parameter lists
/// supply their first value, and the request's extraction rules are tried on the response.
/// </summary>
public static class RequestProbe
{
    private const int MaxPreviewBytes = 16 * 1024;

    /// <param name="variables">
    /// Extracted variables for placeholders, compared without regard to case. Values this request extracts are added.
    /// </param>
    public static async Task<ProbeResult> SendAsync(
        RequestDefinition definition,
        LoadProfile profile,
        HttpMessageInvoker? invoker = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ParameterList>? parameters = null,
        Dictionary<string, string>? variables = null)
    {
        var set = new ParameterSet(parameters ?? []);
        var problems = definition.Validate(set).ToList();
        if (problems.Count > 0)
        {
            return new ProbeResult(null, null, TimeSpan.Zero, [], string.Empty, 0, false, string.Join(" ", problems));
        }

        var owned = invoker is null;
        invoker ??= new HttpMessageInvoker(HttpHandlerFactory.Create(profile), disposeHandler: true);
        var prepared = PreparedRequest.From(definition);
        var start = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));

        try
        {
            // Variables from earlier probes fill in placeholders, and this probe's extractions are added to them, so
            // sending a login request once lets the next "Send once" use its token.
            variables ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var request = prepared.Build(new TemplateContext(1, 1, set.First(), variables), out _);
            using var response = await invoker.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            var latency = Stopwatch.GetElapsedTime(start);

            var headers = response.Headers.Concat(response.Content.Headers)
                .Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)))
                .ToList();
            var preview = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxPreviewBytes));
            if (bytes.Length > MaxPreviewBytes)
            {
                preview += $"\n… ({bytes.Length - MaxPreviewBytes:N0} more bytes)";
            }

            var code = (int)response.StatusCode;
            var success = prepared.Matcher.IsMatch(code);
            List<ExtractedValue>? extractions = null;
            if (prepared.Extractor is { } extractor)
            {
                extractions = [];
                extractor.Apply(response, bytes.AsSpan(0, Math.Min(bytes.Length, ResponseExtractor.MaxBodyBytes)), variables, extractions);
            }

            return new ProbeResult(code, response.ReasonPhrase, latency, headers, preview, bytes.Length, success, null, extractions);
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
            new(null, null, Stopwatch.GetElapsedTime(start), [], string.Empty, 0, false, error);
    }
}


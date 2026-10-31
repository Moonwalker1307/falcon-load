using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Engine;

/// <summary>
/// Gets OAuth2 client credentials tokens for one run and shares them between all of its virtual users. The first token
/// is fetched before the run starts. A token is replaced shortly before it expires, in the background, while requests
/// keep using the old one, so a long run never waits for a token and never sends an expired one as long as the token
/// endpoint answers. Only one token request is in flight at a time, and a failed one is retried with a growing delay
/// rather than by every request.
/// </summary>
internal sealed class OAuth2TokenSource : IDisposable
{
    public const string ErrorPrefix = "Couldn't get an OAuth2 token";

    // A token is replaced when a quarter of its lifetime is left, but no earlier than a minute before it expires, so a
    // one hour token lasts 59 minutes and a 20 second token 15 seconds.
    private static readonly TimeSpan MaxRefreshMargin = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    // A 401 makes the next request fetch a new token, in case the old one was revoked. A server that rejects every
    // token would otherwise make every request fetch one, so a token this young is kept.
    private static readonly TimeSpan MinAgeToInvalidate = TimeSpan.FromSeconds(10);

    private readonly ResolvedAuth _auth;
    private readonly HttpMessageInvoker _invoker;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly SecretMasker _masker;
    private readonly CancellationTokenSource _disposed = new();
    private readonly Lock _gate = new();

    private volatile Token? _current;
    private volatile string? _previous;
    private Task? _refresh;
    private long _nextAttempt;
    private TimeSpan _retryDelay = FirstRetryDelay;
    private long _fetched;
    private long _failed;
    private volatile string? _lastError;

    /// <param name="masker">Hides the environment's secrets; the client secret is hidden as well.</param>
    public OAuth2TokenSource(ResolvedAuth auth, HttpMessageInvoker invoker, TimeSpan timeout, SecretMasker? masker = null, TimeProvider? time = null)
    {
        _auth = auth;
        _invoker = invoker;
        _timeout = timeout;
        _time = time ?? TimeProvider.System;
        _masker = new SecretMasker([auth.ClientSecret, .. masker?.Secrets ?? []]);
    }

    public static OAuth2TokenSource? For(LoadTestPlan plan, TestEnvironment? environment, HttpMessageInvoker invoker, SecretMasker masker, TimeProvider? time = null) =>
        plan.Auth is { IsEnabled: true } auth
            ? new OAuth2TokenSource(auth.Resolve(environment?.ToValues()), invoker, TimeSpan.FromSeconds(plan.Profile.TimeoutSeconds), masker, time)
            : null;

    /// <summary>The token request running in the background, if any, so tests can wait for it.</summary>
    internal Task? Refreshing => Volatile.Read(ref _refresh);

    public AuthStats Stats => new(
        Interlocked.Read(ref _fetched),
        Interlocked.Read(ref _failed),
        _lastError,
        _current?.Lifetime?.TotalSeconds);

    /// <summary>Gets the first token. Throws <see cref="OAuth2TokenException"/> with a message safe to show when it can't.</summary>
    public async Task<Token> FetchFirstAsync(CancellationToken cancellationToken)
    {
        await FetchAsync(cancellationToken).ConfigureAwait(false);
        return _current!;
    }

    /// <summary>
    /// The token to send now. It returns at once unless the token has expired or a 401 made it stale; then it waits for
    /// the new one, or, when the token endpoint fails, returns the old one, so the API's own answer shows what happened.
    /// </summary>
    public ValueTask<Token> GetAsync(CancellationToken cancellationToken)
    {
        var token = _current ?? throw new InvalidOperationException("The first token hasn't been fetched.");
        var now = _time.GetTimestamp();
        return now < token.RefreshAt && !token.Stale ? new(token) : RefreshAsync(token, now, cancellationToken);
    }

    private async ValueTask<Token> RefreshAsync(Token token, long now, CancellationToken cancellationToken)
    {
        var refresh = StartRefresh(token, now);
        if (refresh is not null && (now >= token.ExpiresAt || token.Stale))
        {
            await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return _current!;
    }

    /// <summary>Starts a token request unless one is running or a failed one is waiting to be retried.</summary>
    private Task? StartRefresh(Token token, long now)
    {
        var running = Volatile.Read(ref _refresh);
        if (running is { IsCompleted: false })
        {
            return running;
        }

        if (now < Interlocked.Read(ref _nextAttempt) || !ReferenceEquals(token, _current))
        {
            return null;
        }

        lock (_gate)
        {
            if (_refresh is { IsCompleted: false })
            {
                return _refresh;
            }

            if (!ReferenceEquals(token, _current))
            {
                return null;
            }

            _refresh = Task.Run(async () =>
            {
                try
                {
                    await FetchAsync(_disposed.Token).ConfigureAwait(false);
                    _retryDelay = FirstRetryDelay;
                }
                catch (OAuth2TokenException)
                {
                    Interlocked.Exchange(ref _nextAttempt, _time.GetTimestamp() + Ticks(_retryDelay));
                    _retryDelay = TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
                }
                catch (OperationCanceledException)
                {
                }
            });
            return _refresh;
        }
    }

    /// <summary>
    /// Marks the token as stale after the API rejected it with a 401, so the next request waits for a new one. A token
    /// fetched less than <see cref="MinAgeToInvalidate"/> ago is kept: a new one would most likely be rejected too.
    /// </summary>
    public void Reject(Token token)
    {
        if (ReferenceEquals(token, _current) && _time.GetElapsedTime(token.FetchedAt) >= MinAgeToInvalidate)
        {
            token.Stale = true;
        }
    }

    /// <summary>Hides the client secret, the environment's secrets and the current and previous token in <paramref name="text"/>.</summary>
    public string Mask(string text)
    {
        text = _masker.Apply(text);
        foreach (var value in new[] { _current?.Value, _previous })
        {
            if (value is { Length: >= 3 })
            {
                text = text.Replace(value, SecretMasker.Mask, StringComparison.Ordinal);
            }
        }

        return text;
    }

    private async Task FetchAsync(CancellationToken cancellationToken)
    {
        var startedAt = _time.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var where = _auth.TokenUrl.GetLeftPart(UriPartial.Path);
        try
        {
            using var request = BuildRequest();
            using var response = await _invoker.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw Fail($"{ErrorPrefix} from {where}: {(int)response.StatusCode} {response.ReasonPhrase}{Describe(body)}");
            }

            var (value, lifetime) = Parse(body) ?? throw Fail($"{ErrorPrefix} from {where}: the response has no access_token.");
            var token = new Token(value, startedAt, lifetime);
            if (lifetime is { } l)
            {
                var margin = TimeSpan.FromTicks(Math.Min(MaxRefreshMargin.Ticks, l.Ticks / 4));
                token.RefreshAt = startedAt + Ticks(l - margin);
                token.ExpiresAt = startedAt + Ticks(l);
            }

            _previous = _current?.Value;
            _current = token;
            Interlocked.Increment(ref _fetched);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Fail($"{ErrorPrefix} from {where}: no answer within {_timeout.TotalSeconds:0.#}s.");
        }
        catch (HttpRequestException ex)
        {
            throw Fail($"{ErrorPrefix} from {where}: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            throw Fail($"{ErrorPrefix} from {where}: {ex.Message}");
        }
    }

    private OAuth2TokenException Fail(string message)
    {
        var masked = Mask(message);
        Interlocked.Increment(ref _failed);
        _lastError = masked;
        return new OAuth2TokenException(masked);
    }

    private HttpRequestMessage BuildRequest()
    {
        var fields = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
        if (_auth.Scope.Length > 0)
        {
            fields.Add(new("scope", _auth.Scope));
        }

        if (_auth.Audience.Length > 0)
        {
            fields.Add(new("audience", _auth.Audience));
        }

        var request = new HttpRequestMessage(HttpMethod.Post, _auth.TokenUrl);
        if (_auth.ClientAuthentication == ClientAuthentication.RequestBody)
        {
            fields.Add(new("client_id", _auth.ClientId));
            fields.Add(new("client_secret", _auth.ClientSecret));
        }
        else
        {
            // RFC 6749 section 2.3.1: the id and secret are form-encoded before they are joined and base64-encoded.
            var credentials = $"{Uri.EscapeDataString(_auth.ClientId)}:{Uri.EscapeDataString(_auth.ClientSecret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(fields);
        return request;
    }

    /// <summary>The token and its lifetime from a token response; null when it holds no token.</summary>
    internal static (string Value, TimeSpan? Lifetime)? Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("access_token", out var accessToken)
                || accessToken.ValueKind != JsonValueKind.String
                || accessToken.GetString() is not { Length: > 0 } value)
            {
                return null;
            }

            // Some servers send expires_in as a string. Without it, the token is kept until the API rejects it.
            TimeSpan? lifetime = null;
            if (root.TryGetProperty("expires_in", out var expiresIn))
            {
                var seconds = expiresIn.ValueKind switch
                {
                    JsonValueKind.Number when expiresIn.TryGetDouble(out var n) => n,
                    JsonValueKind.String when double.TryParse(expiresIn.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var s) => s,
                    _ => 0,
                };
                if (seconds > 0)
                {
                    lifetime = TimeSpan.FromSeconds(seconds);
                }
            }

            return (value, lifetime);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The OAuth2 error from an error response, such as <c>: invalid_client (Client authentication failed)</c>.</summary>
    private static string Describe(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                return document.RootElement.TryGetProperty("error_description", out var description) && description.ValueKind == JsonValueKind.String
                    ? $": {error.GetString()} ({description.GetString()})"
                    : $": {error.GetString()}";
            }
        }
        catch (JsonException)
        {
        }

        var text = body.ReplaceLineEndings(" ").Trim();
        return text.Length == 0 ? string.Empty : $": {(text.Length > 200 ? text[..200] + "…" : text)}";
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    public void Dispose()
    {
        _disposed.Cancel();
        _disposed.Dispose();
    }

    /// <summary>A token with the times, on the token source's clock, when it is replaced and when it expires.</summary>
    public sealed class Token(string value, long fetchedAt, TimeSpan? lifetime)
    {
        public string Value { get; } = value;

        /// <summary>The <c>Authorization</c> header value, built once per token rather than per request.</summary>
        public string Header { get; } = "Bearer " + value;

        public long FetchedAt { get; } = fetchedAt;

        public TimeSpan? Lifetime { get; } = lifetime;

        public long RefreshAt { get; set; } = long.MaxValue;

        public long ExpiresAt { get; set; } = long.MaxValue;

        public volatile bool Stale;
    }
}

/// <summary>A token request failed. The message is safe to show: secrets and tokens are masked.</summary>
public sealed class OAuth2TokenException(string message) : Exception(message);

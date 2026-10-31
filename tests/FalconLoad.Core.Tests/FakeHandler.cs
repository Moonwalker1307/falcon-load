using System.Collections.Concurrent;
using System.Net;

namespace FalconLoad.Core.Tests;

internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public ConcurrentQueue<(HttpRequestMessage Request, string? Body)> Received { get; } = new();

    public static FakeHandler Returning(HttpStatusCode code, string body = "ok", TimeSpan? delay = null) =>
        new(async (_, ct) =>
        {
            if (delay is { } d)
            {
                await Task.Delay(d, ct);
            }

            return new HttpResponseMessage(code) { Content = new StringContent(body) };
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Received.Enqueue((request, body));
        return await respond(request, cancellationToken);
    }
}

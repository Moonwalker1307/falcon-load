using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace FalconLoad.Web.Services;

/// <summary>
/// Whether this falcon-load lets controllers run tests on it. Agent mode is off until the <c>FalconLoad:AgentToken</c>
/// setting (or the <c>FalconLoad__AgentToken</c> environment variable) holds a token, and every agent request has to
/// carry that token as a bearer token. An agent listens on an address other machines can reach, so while agent mode is
/// on the whole app, not only the agent endpoints, answers other machines only when they carry the token; the UI and
/// API stay open to the agent machine itself.
/// </summary>
public sealed class AgentMode(IConfiguration configuration)
{
    public const string SettingName = "FalconLoad:AgentToken";

    private readonly byte[] _token = Encoding.UTF8.GetBytes(configuration[SettingName]?.Trim() ?? string.Empty);

    public bool IsEnabled => _token.Length > 0;

    /// <summary>Checks a request's <c>Authorization</c> header value in constant time.</summary>
    public bool Accepts(string? authorization)
    {
        if (!IsEnabled || authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(authorization["Bearer ".Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(presented, _token);
    }

    /// <summary>
    /// Whether a request may reach this app. With agent mode off, everything is allowed, as before: falcon-load listens
    /// on localhost unless told otherwise. With it on, a request from another machine needs the token, so nobody who
    /// can merely reach the agent can start tests, read results or use the probe from it.
    /// </summary>
    public bool Allows(IPAddress? remoteAddress, string? authorization) =>
        !IsEnabled || IsLocal(remoteAddress) || Accepts(authorization);

    // A request with no remote address didn't come over the network (an in-process test server, for one).
    private static bool IsLocal(IPAddress? address) =>
        address is null || IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}

public static class AgentModeGate
{
    /// <summary>Turns away requests from other machines without the agent token while agent mode is on.</summary>
    public static IApplicationBuilder UseAgentModeGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var mode = context.RequestServices.GetRequiredService<AgentMode>();
            if (mode.Allows(context.Connection.RemoteIpAddress, context.Request.Headers.Authorization))
            {
                await next(context);
                return;
            }

            context.Response.Headers.WWWAuthenticate = "Bearer";
            await Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "The agent token is missing or wrong",
                detail: "Agent mode is on, so this falcon-load answers other machines only with its agent token. Open the UI on the agent machine itself.")
                .ExecuteAsync(context);
        });
}

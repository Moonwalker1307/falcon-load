using System.Security.Cryptography;
using System.Text;

namespace FalconLoad.Web.Services;

/// <summary>
/// Whether this falcon-load lets controllers run tests on it. Agent mode is off until the <c>FalconLoad:AgentToken</c>
/// setting (or the <c>FalconLoad__AgentToken</c> environment variable) holds a token, and every agent request has to
/// carry that token as a bearer token.
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
}

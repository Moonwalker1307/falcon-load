using System.Reflection;

namespace FalconLoad.Core.Engine;

/// <summary>The <c>User-Agent</c> falcon-load sends when a request doesn't set its own, such as <c>falcon-load/1.0.0</c>.</summary>
public static class FalconLoadUserAgent
{
    public static readonly string Value = "falcon-load/" + Version();

    // The informational version carries the commit hash after a '+', which a server's logs don't need.
    private static string Version() =>
        (typeof(FalconLoadUserAgent).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(FalconLoadUserAgent).Assembly.GetName().Version?.ToString() ?? "0").Split('+')[0];
}

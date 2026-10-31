using System.Net;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Engine;

internal static class HttpHandlerFactory
{
    public static SocketsHttpHandler Create(LoadProfile profile)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = profile.FollowRedirects,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = int.MaxValue,
            ConnectTimeout = TimeSpan.FromSeconds(profile.TimeoutSeconds),
        };

        if (profile.IgnoreTlsErrors)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        return handler;
    }
}

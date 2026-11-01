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

            // gRPC calls share HTTP/2 connections; a server allows about a hundred calls at once on one, so without
            // more connections a test with many virtual users would queue behind that limit instead of measuring the server.
            EnableMultipleHttp2Connections = true,
        };

        if (profile.IgnoreTlsErrors)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        return handler;
    }
}

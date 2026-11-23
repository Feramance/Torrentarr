using System.Net;
using System.Net.Sockets;

namespace Torrentarr.Infrastructure.Tests;

internal static class LoopbackHttpListener
{
    internal static HttpListener Start(out int port)
    {
        // Reserving an ephemeral socket and immediately releasing it cannot guarantee that
        // another parallel test will not claim the port before HttpListener.Start().
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException ex) when (ex.NativeErrorCode is 48 or 98 or 10048 or 183 && attempt < 19)
            {
                listener.Close();
            }
            catch
            {
                listener.Close();
                throw;
            }
        }
        throw new InvalidOperationException("Unable to start HTTP test listener after retrying port collisions.");
    }
}

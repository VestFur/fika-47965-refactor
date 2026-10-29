using System;
using System.Net.WebSockets;
using System.Text;

namespace Fika.Core.Networking.Websocket;

internal static class FikaBackendSocket
{
    internal static ClientWebSocket Create(string sessionId)
    {
        var socket = new ClientWebSocket();
        // SPT generates its own backend certificate. Match its HTTP client's
        // trust policy for these backend-only sockets, without changing global
        // TLS validation or installing a machine-wide root certificate.
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        socket.Options.SetRequestHeader("Authorization", "Basic " +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(sessionId + ":")));
        return socket;
    }
}

using System.Net.Http.Headers;
using System.Text;

namespace FikaServer.WebSockets;

internal static class WebSocketAuthHelper
{
    public static bool TryGetSessionId(HttpContext context, out string sessionId)
    {
        sessionId = string.Empty;

        var header = context.Request.Headers.Authorization.ToString();
        if (!AuthenticationHeaderValue.TryParse(header, out var authorization) ||
            !string.Equals(authorization.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization.Parameter));
            var separator = decoded.IndexOf(':');
            if (separator <= 0)
            {
                return false;
            }

            sessionId = decoded[..separator];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

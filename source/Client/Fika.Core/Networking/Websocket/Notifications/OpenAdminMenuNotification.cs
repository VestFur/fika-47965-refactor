using System.Text.Json.Serialization;

namespace Fika.Core.Networking.Websocket.Notifications;

internal sealed class OpenAdminMenuNotification
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

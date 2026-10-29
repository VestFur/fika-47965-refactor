using EFT.Communications;
using EFT;
using System.Text.Json.Serialization;

namespace Fika.Core.Networking.Websocket.Notifications;

public sealed class PushNotification
{
    [JsonPropertyName("notificationIcon")]
    public ENotificationIconType NotificationIcon { get; set; }
    [JsonPropertyName("notification")]
    public string Notification { get; set; }
    public ENotificationIconType Icon => NotificationIcon;
    public string Description => Notification ?? string.Empty;
}


using EFT.Communications;
using EFT;
using Fika.Core.Main.Utils;
using Fika.Core.UI;
using System.Text.Json.Serialization;

namespace Fika.Core.Networking.Websocket.Notifications;

public sealed class HeadlessConnectedNotification
{
    [JsonPropertyName("name")]
    public string Name { get; set; }
    public ENotificationIconType Icon => ENotificationIconType.Friend;
    public string Description => string.Format(LocaleUtils.UI_HEADLESS_CONNECTED.Localized(),
        FikaUIGlobals.ColorizeText(FikaUIGlobals.EColor.BLUE, Name));
}


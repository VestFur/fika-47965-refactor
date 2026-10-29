using EFT.Communications;
using EFT;
using Fika.Core.Main.Utils;
using JsonType;
using System.Text.Json.Serialization;
using static Fika.Core.UI.FikaUIGlobals;

namespace Fika.Core.Networking.Websocket.Notifications;

public sealed class StartRaidNotification
{
    [JsonPropertyName("nickname")]
    public string Nickname { get; set; }
    [JsonPropertyName("location")]
    public string Location { get; set; }
    [JsonPropertyName("isHeadlessRaid")]
    public bool IsHeadlessRaid { get; set; }
    [JsonPropertyName("headlessRequesterName")]
    public string HeadlessRequesterName { get; set; }
    [JsonPropertyName("raidTime")]
    public EDateTime RaidTime { get; set; }
    public ENotificationIconType Icon => ENotificationIconType.EntryPoint;
    public string Description
    {
        get
        {
            var nickname = Nickname ?? string.Empty;
            var location = Location ?? string.Empty;
            var message = string.Format(LocaleUtils.UI_NOTIFICATION_STARTED_RAID.Localized(),
                ColorizeText(EColor.GREEN, nickname.StartsWith("headless_") ? "Headless Client" : nickname),
                ColorizeText(EColor.BLUE, location.Localized()));
            if (location is not "laboratory")
            {
                var time = FormattedTime(RaidTime, location is "factory4_day" or "factory4_night");
                if (!string.IsNullOrEmpty(time)) message += $" ({BoldText(ColorizeText(EColor.BLUE, time))})";
            }
            return message;
        }
    }
}


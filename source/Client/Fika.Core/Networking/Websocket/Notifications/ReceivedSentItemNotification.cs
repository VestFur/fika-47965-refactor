using EFT.Communications;
using EFT;
using Fika.Core.Main.Utils;
using System;
using System.Text.Json.Serialization;
using static Fika.Core.UI.FikaUIGlobals;

namespace Fika.Core.Networking.Websocket.Notifications;

public sealed class ReceivedSentItemNotification
{
    [JsonPropertyName("nickname")]
    public string Nickname { get; set; }
    [JsonPropertyName("targetId")]
    public string TargetId { get; set; }
    [JsonPropertyName("itemName")]
    public string ItemName { get; set; }
    [JsonPropertyName("stackCount")]
    public double StackCount { get; set; }
    [JsonPropertyName("multiple")]
    public bool Multiple { get; set; }
    public ENotificationIconType Icon => ENotificationIconType.WishlistOther;
    public string Description
    {
        get
        {
            if (Multiple)
                return string.Format(LocaleUtils.UI_NOTIFICATION_RECEIVED_MULTIPLE_ITEMS.Localized(), ColorizeText(EColor.GREEN, Nickname));
            if (StackCount > 1d)
                return string.Format(LocaleUtils.UI_NOTIFICATION_RECEIVED_ITEM_STACK.Localized(),
                    (int)Math.Round(StackCount, MidpointRounding.AwayFromZero),
                    ColorizeText(EColor.BLUE, (ItemName ?? string.Empty).Localized()), ColorizeText(EColor.GREEN, Nickname));
            return string.Format(LocaleUtils.UI_NOTIFICATION_RECEIVED_ITEM.Localized(),
                ColorizeText(EColor.BLUE, (ItemName ?? string.Empty).Localized()), ColorizeText(EColor.GREEN, Nickname));
        }
    }
}


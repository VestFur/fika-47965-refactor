using EFT.Communications;

namespace Fika.Core;

internal static class FikaNotifications
{
    // IL2CPP's generated wrapper unboxes Nullable<Color>. C#'s optional null
    // is a null wrapper, not the native value type with HasValue == false.
    public static void DisplayMessageNotification(string message,
        ENotificationDurationType duration = ENotificationDurationType.Default,
        ENotificationIconType iconType = ENotificationIconType.Default,
        Il2CppSystem.Nullable<Color> textColor = null, bool rawText = true)
    {
        NotificationManager.DisplayMessageNotification(message, duration, iconType,
            textColor ?? new Il2CppSystem.Nullable<Color>(), rawText);
    }
}

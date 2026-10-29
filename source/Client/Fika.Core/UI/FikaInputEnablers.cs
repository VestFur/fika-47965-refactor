using EFT.UI;

namespace Fika.Core.UI;

internal static class FikaInputEnablers
{
    // UIEventSystem uses a flags mask. Separate bits prevent Fika windows from
    // disabling each other, the console, or the game's notes window.
    public const UIEventSystem.EInputEnabler Chat = (UIEventSystem.EInputEnabler)(1 << 16);
    public const UIEventSystem.EInputEnabler RaidAdmin = (UIEventSystem.EInputEnabler)(1 << 17);
}

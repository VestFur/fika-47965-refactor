using Fika.Core.Main.Utils;

namespace Fika.Core.Main.ClientClasses;

internal static class FikaClientPhysical
{
    internal static void ApplySettings(Physical physical)
    {
        var settings = FikaBackendUtils.CustomRaidSettings;
        if (settings.DisableArmStamina)
        {
            physical.HandsStamina.ForceMode = true;
        }
        if (settings.DisableLegStamina)
        {
            physical.Stamina.ForceMode = true;
        }
        if (settings.DisableOverload)
        {
            physical.EncumberDisabled = true;
        }
    }
}


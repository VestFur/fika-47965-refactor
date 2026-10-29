using System.Collections.Generic;
using System.Reflection;
using EFT;
using Fika.Core.Main.GameMode;
using SPTushonka.Reflection.Patching;
using JsonType;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Fika.Core.Main.Patches.LocalGame;

/// <summary>
/// Used to prevent players from getting everyone elses BTR items
/// </summary>
public class BaseLocalGame_GetTransferItems_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BaseLocalGame<EftGamePlayerOwner>)
            .GetMethod(nameof(BaseLocalGame<EftGamePlayerOwner>.GetTransferItems));
    }

    [PatchPrefix]
    public static bool Prefix(BaseLocalGame<EftGamePlayerOwner> __instance, ref Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppReferenceArray<FlatItem>> __result)
    {
        // Native calls can enter this patch with only a BaseLocalGame wrapper.
        // Query the IL2CPP type rather than the wrapper's managed type so each
        // player's BTR/transit items are filtered on the Fika raid path.
        if (__instance?.TryCast<CoopGame>() is { } coopGame)
        {
            __result = coopGame.GetOwnSentItems(coopGame.ProfileId)
                .Select(pair => new KeyValuePair<string, Il2CppReferenceArray<FlatItem>>(pair.Key, pair.Value)).ToNativeDictionary();
            return false;
        }
        return true;
    }
}

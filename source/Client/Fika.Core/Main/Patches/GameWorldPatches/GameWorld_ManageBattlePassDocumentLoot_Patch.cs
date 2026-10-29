using System.Linq;
using System.Reflection;
using EFT;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.GameWorldPatches;

/// <summary>
/// SPT 5 generates battle pass documents per player through
/// <see cref="GameWorld.ManageBattlePassDocumentLoot"/> with freshly minted item
/// ids, so a Fika client would spawn a private batch the host has never seen.
/// The host then cannot resolve the client's pickup descriptor and rejects it,
/// which forces the client through a full inventory resync while it is still in
/// the interaction state. A Fika client keeps the host's world instead; the
/// host still manages its own documents.
/// </summary>
public sealed class GameWorld_ManageBattlePassDocumentLoot_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(GameWorld)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(x => x.Name == nameof(GameWorld.ManageBattlePassDocumentLoot));
    }

    [PatchPrefix]
    public static bool Prefix() => !FikaBackendUtils.IsClient;
}

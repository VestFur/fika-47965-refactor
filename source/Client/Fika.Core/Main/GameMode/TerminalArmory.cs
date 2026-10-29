using System;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using JsonType;

namespace Fika.Core.Main.GameMode;

/// <summary>
/// (2026-10-19, plan A) Terminal armory confiscation.
///
/// Vanilla moves a player's equipment into one of the five armory containers of the Terminal map; the player
/// has to walk to the armory, find the key on the corpse in the substation yard and open the stash to get the
/// gear back. The transfer itself is done when the raid loot is built, so on Fika it has to happen on the host
/// right before the loot descriptor is serialized and sent to the clients.
///
/// Data (SPT 1.2.0, verified): container template 67a0e494e8fc6968ef0fc9da = lc_spec_stash_player_terminal,
/// five always-spawn instances in Terminal_Area_05_Administration, empty static loot table - they only ever
/// hold what this method puts in them. Five containers for MaxPlayers 5, one per player.
/// </summary>
internal static class TerminalArmory
{
    private const string ContainerTemplateId = "67a0e494e8fc6968ef0fc9da";

    private static readonly string[] Containers =
    [
        "container_Terminal_Design_Stuff_00159",
        "container_Terminal_Design_Stuff_00161",
        "container_Terminal_Design_Stuff_00164",
        "container_Terminal_Design_Stuff_00165",
        "container_Terminal_Design_Stuff_00167"
    ];

    private static bool _stripped;

    private static readonly EquipmentSlot[] Slots =
    [
        EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.Eyewear, EquipmentSlot.FaceCover,
        EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack, EquipmentSlot.Pockets,
        EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster,
        EquipmentSlot.Scabbard
    ];

    internal static void Reset() => _stripped = false;

    /// <summary>
    /// (2026-10-19, plan A step 2) Empties the local player's equipment once per raid on Terminal. The gear is
    /// already in the armory (snapshot injected into the loot descriptor), so the player has to walk there with
    /// what the map gives him - the vanilla loop. Runs on every machine for its own player: the host cannot
    /// move another machine's real inventory.
    /// </summary>
    internal static void Tick(GameWorld world, Player localPlayer)
    {
        if (_stripped || world == null || localPlayer == null)
        {
            return;
        }

        try
        {
            var controller = localPlayer.InventoryController;
            var equipment = localPlayer.Inventory?.Equipment;
            if (controller == null || equipment == null)
            {
                return;
            }

            _stripped = true;

            var removed = 0;
            var failed = 0;
            foreach (var slot in Slots)
            {
                Item item;
                try { item = equipment.GetSlot(slot).ContainedItem; }
                catch (Exception) { continue; }

                if (item == null)
                {
                    continue;
                }

                try
                {
                    var result = ItemManipulator.Remove(item, controller, true);
                    if (result == null || result.Error != null)
                    {
                        failed++;
                        continue;
                    }

                    controller.TryRunNetworkTransaction(result, null);
                    removed++;
                }
                catch (Exception)
                {
                    failed++;
                }
            }

            FikaGlobals.LogInfo($"[TerminalArmory] confiscated {removed} slot(s), {failed} kept (the armory holds the snapshot)");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[TerminalArmory] confiscation failed, the player keeps the equipment: {exception}");
        }
    }

    internal static bool Applies(string locationId) =>
        !string.IsNullOrEmpty(locationId) &&
        string.Equals(locationId, "terminal", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Appends one loot entry per participant. Entries keep ValidProfiles null so the host-side profile filter
    /// cannot drop them; the container instance id is the armory slot the player has to open later.
    /// </summary>
    /// <summary>
    /// Serializes the tree and builds an independent one, so later changes to the player's inventory cannot
    /// reach the armory container.
    /// </summary>
    private static Item Clone(Item item)
    {
        try
        {
            var descriptor = ItemBinarySerializer.SerializeItem(item, FikaGlobals.SearchControllerSerializer);
            return ItemBinarySerializer.DeserializeItem(descriptor, Singleton<ItemFactory>.Instance,
                new Il2CppSystem.Collections.Generic.Dictionary<MongoID, Item>());
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[TerminalArmory] clone failed: {exception}");
            return null;
        }
    }

    internal static void Inject(LocationSettings.Location location, CoopHandler handler)
    {
        try
        {
            if (location?.Loot == null || handler?.HumanPlayers == null)
            {
                return;
            }

            var players = handler.HumanPlayers;
            if (players.Count == 0)
            {
                return;
            }

            var entries = new Il2CppSystem.Collections.Generic.List<JsonLootItem>();
            foreach (var existing in location.Loot)
            {
                entries.Add(existing);
            }

            var injected = 0;
            for (var i = 0; i < players.Count && i < Containers.Length; i++)
            {
                var player = players[i];
                if (player == null)
                {
                    continue;
                }

                Item equipment;
                try { equipment = player.Inventory?.Equipment; }
                catch (Exception) { equipment = null; }

                if (equipment == null)
                {
                    FikaGlobals.LogWarning($"[TerminalArmory] no equipment tree for {player.ProfileId}, that player keeps the gear");
                    continue;
                }

                // Deep copy: the loot entry must be a snapshot taken at raid start, not a live reference to the
                // player's own Equipment tree (a live reference makes the armory container mirror the player's
                // inventory for the whole raid).
                var snapshot = Clone(equipment);
                if (snapshot == null)
                {
                    FikaGlobals.LogWarning($"[TerminalArmory] could not snapshot the equipment of {player.ProfileId}, that player keeps the gear");
                    continue;
                }

                entries.Add(new JsonLootItem
                {
                    Id = Containers[i],
                    IsContainer = true,
                    Item = snapshot,
                    ValidProfiles = null
                });
                injected++;
            }

            if (injected == 0)
            {
                return;
            }

            location.Loot = new LootData(entries);
            FikaGlobals.LogInfo($"[TerminalArmory] injected {injected} equipment stash(es) into the armory containers");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[TerminalArmory] injection failed, the raid keeps its unobstructed equipment: {exception}");
        }
    }
}

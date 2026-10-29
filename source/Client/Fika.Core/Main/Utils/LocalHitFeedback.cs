// © 2026 Lacyway All Rights Reserved

using System;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Gives the shooter the hit feedback the game produces locally, without applying damage twice.
///
/// Why it is needed: in a Fika raid the bullet damage of a client is applied by the machine that
/// owns the victim - the host for a bot, the victim itself for another player - so
/// <c>ActiveHealthController.ApplyDamage(EBodyPart, float, DamageInfo)</c> never runs on the
/// shooter's machine with <c>damageInfo.Player.iPlayer</c> being the shooter. Every consumer of
/// that member (hit markers, damage numbers, hit sounds - for example the SimpleHitMarker plugin,
/// which postfixes exactly that method) therefore stays silent for the shooter even though the
/// hit registers and is relayed to the authoritative machine.
///
/// The call below re-uses the very same member the plugin listens to, on the victim's own health
/// controller, with a damage value of 0 and a damage type that is not enemy damage:
///  * <c>ActiveHealthController.ApplyDamage</c> is declared on the base class only (neither
///    PlayerHealthController nor NetworkHealthController override it), so the Harmony postfix
///    runs for an observed puppet as well;
///  * zero damage means no health change, no destroyed body part, no OnApplyDamageByPlayer
///    (that callback is gated on enemy damage, which is why the damage type is left undefined)
///    and no packet: the victim's health on this machine still comes exclusively from the
///    authoritative machine's health sync.
/// </summary>
internal static class LocalHitFeedback
{
    /// <summary>
    /// Reports a hit the local player just registered on <paramref name="victim"/> through the
    /// health-controller hook that hit feedback plugins patch.
    /// </summary>
    internal static void Report(Player shooter, ObservedPlayer victim, EBodyPart bodyPart, float damage, EDamageType damageType)
    {
        try
        {
            if (shooter == null || victim == null || damage <= 0f || !shooter.IsYourPlayer)
            {
                return;
            }

            var healthController = victim.ActiveHealthController;
            if (healthController == null || !healthController.IsAlive)
            {
                return;
            }

            // The bridge is what DamageInfo.Player carries on the authoritative machine; the game
            // registers one per player (keyed by raid id, and by collider as a fallback).
            var world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            var owner = world.GetAlivePlayerBridgeByRaidID(shooter.RaidId);
            if (owner == null)
            {
                var characterController = shooter.GetCharacterControllerCommon();
                var collider = characterController != null ? characterController.GetCollider() : null;
                if (collider != null)
                {
                    owner = world.GetAlivePlayerBridgeByCollider(collider);
                }
            }

            if (owner == null)
            {
                FikaGlobals.LogWarning($"[HitMarker] no player bridge for {shooter.ProfileId}, the local hook was not fired");
                return;
            }

            DamageInfo feedback = new()
            {
                Damage = damage,
                DamageType = EDamageType.Undefined,
                Player = owner
            };

            // Zero damage: fires the hook, changes nothing. The victim's local health is read
            // around the call so a single raid also proves that no damage was applied here.
            var healthBefore = healthController.GetBodyPartHealth(bodyPart, false).Current;
            healthController.ApplyDamage(bodyPart, 0f, feedback);
            var healthAfter = healthController.GetBodyPartHealth(bodyPart, false).Current;

            if (healthAfter < healthBefore)
            {
                FikaGlobals.LogError($"[HitMarker] the local hook changed the local health of {victim.ProfileId} " +
                    $"({healthBefore:F2} -> {healthAfter:F2}); this must never happen, please report it");
            }

            FikaPlugin.Instance.FikaLogger.LogInfo($"[HitMarker] shooter={shooter.ProfileId}, victim={victim.ProfileId}, " +
                $"role={(victim.Profile?.Info?.Settings != null ? victim.Profile.Info.Settings.Role.ToString() : "none")}, " +
                $"isObservedAI={victim.IsObservedAI}, part={bodyPart}, damage={damage:F2}, damageType={damageType}, " +
                $"health={healthBefore:F2}->{healthAfter:F2}, hook=ActiveHealthController.ApplyDamage(part, 0, DamageInfo)");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError("[HitMarker] could not report the local hit: " + exception);
        }
    }
}

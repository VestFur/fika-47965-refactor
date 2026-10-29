using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Communication;
using JsonType;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.TransitController;

/// <summary>
/// (analysis/transit-point-sync) Every change of a transit point's <c>Enabled</c> flag passes through the
/// native property setter <c>EFT.Interactive.TransitPoint.set_Enabled(Boolean)</c> (interop dump 212407:
/// <c>P|EFT.Interactive.TransitPoint|Enabled|System.Boolean Enabled|get:public set:public</c>; the generated
/// stub field is <c>NativeMethodInfoPtr_set_Enabled_Public_set_Void_Boolean_0</c>, dump 212312), so the
/// setter is the one event this hook needs: the map triggers, the raid start enable and Fika's own writes
/// all end there.
/// <para>
/// The postfix runs on the host only and re-broadcasts the host's whole enabled list in the existing
/// <see cref="TransitEventPacket"/> initialization (<see cref="TransitEventPacket.ETransitEventType.Init"/>,
/// <see cref="TransitInitEvent"/>, <see cref="TransitEventPacket.EnabledPointIds"/>, built like
/// <c>FikaServer.SendMapStateSnapshot</c>), addressed at no player
/// (<see cref="TransitEventPacket.AnyPlayerRaidId"/>). No new packet, no new field, no polling; the client
/// applies the list in both directions (ClientTransitController.SetPointEnabled).
/// </para>
/// </summary>
internal sealed class TransitPoint_SetEnabled_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var property = typeof(TransitPoint).GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
        var target = property?.GetSetMethod(true);
        if (target == null)
        {
            throw new MissingMethodException("EFT.Interactive.TransitPoint.set_Enabled(Boolean)");
        }
        return target;
    }

    [PatchPostfix]
    public static void Postfix(TransitPoint __instance)
    {
        try
        {
            // The client writes the same property when it applies a snapshot; only the host is the source.
            if (!FikaBackendUtils.IsServer) return;
            if (__instance == null || __instance.parameters == null) return;

            var pointId = __instance.parameters.id;
            var enabled = __instance.Enabled;
            // (2026-10-26) A send that never happened used to be silent: `Send()` returned false and the
            // postfix left without a line, which reads exactly like "the native setter was never called".
            // The reason is now part of the log, so one host run decides which of the two it was.
            if (!TransitPointStateBroadcast.Send(out var skipReason))
            {
                FikaGlobals.LogWarning($"[SyncDiag] point-broadcast-skipped id={pointId} " +
                    $"enabled={(enabled ? "true" : "false")} reason={skipReason}");
                return;
            }

            FikaGlobals.LogWarning($"[SyncDiag] point-broadcast id={pointId} enabled={(enabled ? "true" : "false")}");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[SyncDiag] point-sync-failed where=host-broadcast error={exception.GetType().Name}: {exception.Message}");
        }
    }
}

/// <summary>
/// Builds and sends the state-only initialization of the running raid: the host's complete
/// <c>point.Enabled</c> list, nothing else. Shared by the setter hook above; it never mutates the world.
/// </summary>
internal static class TransitPointStateBroadcast
{
    internal static bool Send() => Send(out _);

    /// <summary>
    /// Same send, with the reason it did not happen (2026-10-26). Empty on success; otherwise one of
    /// <c>no-network</c>, <c>no-transit-controller</c>, <c>no-points-by-id</c>, so a host log can tell a broken
    /// wiring apart from a native setter that never ran.
    /// </summary>
    internal static bool Send(out string detail)
    {
        detail = string.Empty;
        var network = FikaSingleton<IFikaNetworkManager>.Instance;
        if (network == null)
        {
            detail = "no-network";
            return false;
        }

        var transit = Singleton<GameWorld>.Instance?.TransitController;
        if (transit == null)
        {
            detail = "no-transit-controller";
            return false;
        }

        if (transit.pointsById == null)
        {
            detail = "no-points-by-id";
            return false;
        }

        var points = new Il2CppSystem.Collections.Generic.Dictionary<int, LocationSettings.Location.TransitParameters>();
        var requirements = new Il2CppSystem.Collections.Generic.Dictionary<int, bool>();
        var enabled = new List<int>();
        foreach (var entry in transit.pointsById)
        {
            var point = entry.Value;
            if (point == null) continue;
            if (point.parameters != null && point.parameters.active)
            {
                points.Add(entry.Key, point.parameters);
            }
            // Fika reports every entry requirement as met (FikaServer.MapState.cs:178-183 and
            // FikaHostTransitController.CreateInitialization:3225-3228): the raw native result disabled
            // the point in every client UI, and the per player validation stays with the machine that
            // shows the prompt and with the host that answers the request.
            requirements.Add(entry.Key, true);
            if (point.Enabled) enabled.Add(entry.Key);
        }

        var transitData = FikaBackendUtils.TransitData;
        var packet = new TransitEventPacket
        {
            EventType = TransitEventPacket.ETransitEventType.Init,
            TransitEvent = new TransitInitEvent
            {
                // No player owns this initialization: it only carries the host's point state, and the
                // sentinel raid id keeps every receiver from applying it to its own transit history.
                PlayerRaidId = TransitEventPacket.AnyPlayerRaidId,
                Points = points,
                CompletedQuestRequirementMetByPointId = requirements,
                TransitionCount = (ushort)System.Math.Clamp(transitData?.transitionCount ?? 0, 0, ushort.MaxValue),
                EventPlayer = transitData != null &&
                    (transitData.transitionType & ELocationTransition.Event) == ELocationTransition.Event
            },
            EnabledPointIds = enabled.ToArray(),
            RegisteredPointId = -1,
            KeyId = string.Empty,
            SelectedTime = default
        };

        network.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        return true;
    }
}

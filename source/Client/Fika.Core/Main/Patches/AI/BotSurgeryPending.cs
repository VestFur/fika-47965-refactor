using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.AI;

// Surgery can be requested in the completion callback of first aid, before
// its return-to-weapon Process finishes. Keep the request rather than replace
// that weapon Process and let its delayed retry interrupt the new treatment.
internal static class BotSurgeryPending
{
    private sealed class Request(BotSurgicalKit surgery, FikaBot bot, AITaskManager manager,
        Il2CppSystem.Action callback)
    {
        internal readonly BotSurgicalKit Surgery = surgery;
        internal readonly FikaBot Bot = bot;
        internal readonly AITaskManager Manager = manager;
        // The interop's own delegate type: it is passed back to the native
        // ApplyToCurrentPart(Il2CppSystem.Action) overload untouched.
        internal readonly Il2CppSystem.Action Callback = callback;
        internal int TaskId;
    }

    private static readonly Dictionary<IntPtr, Request> Requests = new();

    /// <summary>
    /// Drops every pending surgery request at a world teardown (analysis/transit-crash-unload): each one
    /// holds a native bot and the native callback of an action that was refused during the raid that ended.
    /// Returns the number dropped.
    /// </summary>
    internal static int ClearForWorldTeardown()
    {
        var count = Requests.Count;
        if (count > 0)
        {
            Requests.Clear();
        }

        return count;
    }

    internal static bool Begin(BotSurgicalKit surgery, Il2CppSystem.Action callback)
    {
        var bot = surgery?._owner?.GetPlayer?.TryCast<FikaBot>();
        if (bot == null || bot.ProcessStatus == Player.EProcessStatus.None)
        {
            Cancel(surgery);
            return true;
        }
        Cancel(surgery);
        var request = new Request(surgery, bot, surgery._owner.BotsController.AiTaskManager, callback);
        Requests.Add(surgery.Pointer, request);
        Schedule(request);
        return false;
    }

    private static void Schedule(Request request) =>
        request.TaskId = request.Manager.RegisterDelayedTask(request.Surgery._owner, 0.1f,
            (System.Action)(() => Resume(request)));

    private static void Resume(Request request)
    {
        var surgery = request.Surgery;
        if (!Requests.TryGetValue(surgery.Pointer, out var current) || !ReferenceEquals(current, request)) return;
        var bot = request.Bot;
        if (surgery._owner?.GetPlayer?.Pointer != bot.Pointer || !bot.HealthController.IsAlive || surgery.Using)
        {
            Cancel(surgery);
            return;
        }
        if (bot.ProcessStatus != Player.EProcessStatus.None || surgery._owner.Medecine.Using ||
            surgery._nextPosibleUseTime > UnityEngine.Time.time)
        {
            Schedule(request);
            return;
        }
        // Re-evaluate the real inventory, injured limb and native CanApplyItem
        // after the transition. Preserve the original completion callback; no
        // Using/health edits or synthetic completion when eligibility changed.
        Requests.Remove(surgery.Pointer);
        surgery.Refresh();
        surgery.FindDamagedPart();
        surgery.ApplyToCurrentPart(request.Callback);
    }

    internal static void Cancel(BotSurgicalKit surgery)
    {
        if (surgery != null && Requests.Remove(surgery.Pointer, out var request))
            request.Manager.CancelDelayedTask(request.TaskId);
    }
}

public sealed class BotSurgicalKit_ApplyToCurrentPart_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(BotSurgicalKit).GetMethod(nameof(BotSurgicalKit.ApplyToCurrentPart));

    // Mirrors the interop signature BotSurgicalKit.ApplyToCurrentPart(Il2CppSystem.Action). A
    // System.Action parameter produced the Il2CppInterop warning "takes callbackEndUse as
    // System.Action (not an il2cpp type) ... the patch will read it as the wrong type".
    [PatchPrefix]
    public static bool Prefix(BotSurgicalKit __instance, Il2CppSystem.Action callbackEndUse)
    {
        try
        {
            return BotSurgeryPending.Begin(__instance, callbackEndUse);
        }
        catch (Exception exception)
        {
            
            return true;
        }
    }
}

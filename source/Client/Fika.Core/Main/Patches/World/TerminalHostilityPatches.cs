using System;
using System.Linq;
using System.Reflection;
using EFT;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

// (analysis/terminal-hostility-graph, hotfix 2026-10-14) The seven observation patches of this file are DISABLED.
//
// Why: in the first live co-op test of the terminal-hostility-graph round the client took no damage at all
// ("the client is invincible") and the host process died as soon as its own player took fall damage - i.e. the
// exact two things these patches and the immunity fan-out sit on. Every patch of this file is installed through
// PatchAll and therefore adds a managed detour to a native damage / zone path (Player.ApplyDamageInfo,
// SniperFiringZone.IsPlayerCanBeDamaged, BorderZone.ProcessIncomingPacket, the two immunity writers,
// SniperFireTargetManager, BotGroupHostility.AddEnemyAction), and a fail-fast abort inside one of those is not
// catchable by the try/catch their bodies already carry. Until they can be re-introduced behind a startup
// switch that decides whether they are installed at all, the safety of the damage pipeline comes first: this
// port adds no detour there.
//
// What is still live: FikaTerminalHostility itself (the lines and the fan-out) is called from FikaSentryAccess,
// so the sentry access chain of BJ/BM/BT keeps working; FikaTerminalHostility.ImmunityEnabled is false by
// default (see that file), so the sniper-immunity container is no longer written from here either.
//
// The complete previous content of this file is preserved next to the report:
//     mods/fika-47888/analysis/terminal-hostility-graph/_disabled-patches/TerminalHostilityPatches.cs.txt
// Restoring it means restoring both halves: this file and the defaults of FikaTerminalHostility.
internal static class TerminalHostilityPatches
{
    /// <summary>True while the observation file is present but its patches are not installed.</summary>
    internal const bool Disabled = true;

    /// <summary>Kept so a future round can find the preserved source without searching the report.</summary>
    internal const string PreservedSource =
        "analysis/terminal-hostility-graph/_disabled-patches/TerminalHostilityPatches.cs.txt";
}

using EFT.Weather;
using System.Collections.Generic;
using EFT;

namespace Fika.Core.Main.GameMode;

/// <summary>
/// Interface for the <see cref="CoopGame"/>
/// </summary>
public interface IFikaGame
{
    public List<int> ExtractedPlayers { get; }

    ExitStatus ExitStatus { get; set; }

    string ExitLocation { get; set; }

    public void Stop(string profileId, ExitStatus exitStatus, string exitName, float delay = 0f);

    public ESeason Season { get; set; }

    public SeasonsSettings SeasonsSettings { get; set; }

    public BaseGameController GameController { get; set; }

    /// <summary>Restores the local first-person hands after the IL2CPP deployment path.</summary>
    public void RepairLocalHands(bool force = false);


    /// <summary>Releases a pickup/loot state that the native animation never left.</summary>
    public void RepairLocalInteractionState();

    /// <summary>
    /// Releases an inventory gate that stayed closed even though nothing holds
    /// it, so that item moves and weapon switches work again.
    /// </summary>
    public void RepairLocalInventoryState();
}

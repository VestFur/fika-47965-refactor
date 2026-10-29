using EFT.InventoryLogic;
using Fika.Core.Main.ClientClasses.HandsControllers;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.BotClasses;

public sealed class BotFirearmController : FikaClientFirearmController
{
    public BotFirearmController(System.IntPtr pointer) : base(pointer) { }


    public BotFirearmController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<BotFirearmController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public override Vector3 WeaponDirection
    {
        get
        {
            return _fikaPlayer.LookDirection;
        }
    }

    public static BotFirearmController Create(FikaBot player, Weapon weapon)
    {
        var controller = CreateController<BotFirearmController>(player, weapon);
        controller._fikaPlayer = player;
        controller._isGrenadeLauncher = weapon.IsGrenadeLauncher;
        controller._packet = new()
        {
            NetId = player.NetId
        };
        return controller;
    }
}

using EFT;
using EFT.Dialogs;
using EFT.InventoryLogic;
using EFT.Quests;

namespace Fika.Core.Main.ObservedClasses;

/// <summary>
/// Dialog controller used by observed players.  Keeping it separate from the
/// local player's ClientDialogController prevents remote quest/dialog state
/// from writing into the local backend controller.
/// </summary>
public sealed class ObservedDialogController : BaseTraderDialogController
{
    public ObservedDialogController(System.IntPtr pointer) : base(pointer)
    {
    }

    public ObservedDialogController(Profile profile, QuestController questController, InventoryController inventoryController)
        : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedDialogController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<BaseTraderDialogController>(this, profile, questController, inventoryController);
    }
}


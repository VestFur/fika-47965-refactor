#pragma warning disable CS0169
#pragma warning disable CS0649
using TMPro;
using UnityEngine.UI;

public class IntUpDown : MonoBehaviour
{
    public IntUpDown(System.IntPtr pointer) : base(pointer) { }


    public IntUpDown() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<IntUpDown>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> _increaseButton;
    
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> _decreaseButton;
    
    public int _amountOfPlayers = 1;
    
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> _amountText;

    public void IncreaseAmount()
    {
        _amountOfPlayers++;
        _amountOfPlayers = Mathf.Clamp(_amountOfPlayers, 1, 32);

        _amountText.Value.SetFormattedText("{0}", _amountOfPlayers);
    }

    public void DecreaseAmount()
    {
        _amountOfPlayers--;
        _amountOfPlayers = Mathf.Clamp(_amountOfPlayers, 1, 32);

        _amountText.Value.SetFormattedText("{0}", _amountOfPlayers);
    }
}




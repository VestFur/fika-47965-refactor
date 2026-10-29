using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;

namespace Fika.Core.Main.Components;

public class ItemPositionSyncer : MonoBehaviour
{
    public ItemPositionSyncer(System.IntPtr pointer) : base(pointer) { }


    public ItemPositionSyncer() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ItemPositionSyncer>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private FikaServer _server;
    private FikaClient _client;
    private bool _isServer;
    private ObservedLootItem _lootItem;
    private EFT.LootSyncPacket _data;
    private System.Action<RemoveItemEventArgs> _removeItemHandler;

    private bool PhysicsDone
    {
        get
        {
            return _lootItem.RigidBody == null;
        }
    }

    public static void Create(GameObject gameObject, bool isServer, ObservedLootItem lootItem)
    {
        // AP (analysis/nonfika-raid-inertness): this component is injected into il2cpp and its transport
        // (FikaServer/FikaClient) only exists inside a raid of ours. Outside one it used to be added to a
        // native loot item and faulted afterwards on every frame; the guard is the belt to the caller's
        // braces, so a future call site cannot reintroduce that state either.
        if (FikaRaidGate.Skip("ItemPositionSyncer.Create"))
        {
            return;
        }

        var posSync = gameObject.AddComponent<ItemPositionSyncer>();
        posSync._isServer = isServer;
        if (isServer)
        {
            posSync._server = FikaSingleton<FikaServer>.Instance;
        }
        else
        {
            posSync._client = FikaSingleton<FikaClient>.Instance;
        }
        posSync._lootItem = lootItem;
        posSync._data = new()
        {
            Id = lootItem.GetNetId()
        };
    }

    /// <summary>
    /// AP (analysis/nonfika-raid-inertness): the transport this component exists for. Inside a raid of
    /// ours one of the two singletons is always there; outside one both are absent, and every method
    /// below used to fault on that instead of ending its own life. The check is the one thing that turns
    /// "our component on a native loot item" from a per-frame exception source into a clean self-destroy.
    /// </summary>
    private bool HasTransport()
    {
        return _isServer ? _server != null : _client != null;
    }

    public void Start()
    {
        if (_lootItem == null)
        {
            FikaGlobals.LogError("ItemPositionSyncer::Start: LootItem was null!");
            Destroy(this);
            return;
        }

        if (!HasTransport())
        {
            FikaGlobals.LogError("ItemPositionSyncer::Start: no transport (" + (_isServer ? "server" : "client")
                + "); the component ends here instead of faulting on every frame");
            Destroy(this);
            return;
        }

        if (_lootItem.RigidBody == null)
        {
            FikaGlobals.LogError("ItemPositionSyncer::Start: Rigidbody was null!");
            Destroy(this);
            return;
        }

        _data.Position = _lootItem.transform.position;
        _data.Rotation = _lootItem.transform.rotation;
        _data.Velocity = _lootItem.RigidBody.velocity;
        _data.AngularVelocity = _lootItem.RigidBody.angularVelocity;
        if (_isServer)
        {
            _server.FikaHostWorld.AddLootSyncStruct(_data);
            return;
        }

        _client.FikaClientWorld.AddLootSyncStruct(_data);
        _removeItemHandler = (System.Action<RemoveItemEventArgs>)ItemOwner_RemoveItemEvent;
        _lootItem.ItemOwner.RemoveItemEvent += _removeItemHandler;
    }

    private void ItemOwner_RemoveItemEvent(RemoveItemEventArgs obj)
    {
        NotifyDone();
    }

    public void FixedUpdate()
    {
        // AP: a component whose transport is gone (raid teardown, or a loot item that outlived the raid)
        // ends itself here; the old shape kept faulting once per physics frame until the world was gone.
        if (!HasTransport())
        {
            Destroy(this);
            return;
        }

        if (PhysicsDone)
        {
            NotifyDone();
            return;
        }

        _data.Position = _lootItem.transform.position;
        _data.Rotation = _lootItem.transform.rotation;
        _data.Velocity = _lootItem.RigidBody.velocity;
        _data.AngularVelocity = _lootItem.RigidBody.angularVelocity;

        if (_isServer)
        {
            _server.FikaHostWorld.AddLootSyncStruct(_data);
            return;
        }

        _client.FikaClientWorld.AddLootSyncStruct(_data);
    }

    private void NotifyDone()
    {
        if (!HasTransport())
        {
            Destroy(this);
            return;
        }

#if DEBUG
        FikaGlobals.LogInfo($"{_lootItem.Item.LocalizedShortName()} rigid body done");
#endif

        _data.Position = _lootItem.transform.position;
        _data.Rotation = _lootItem.transform.rotation;
        _data.Velocity = Vector3.zero;
        _data.AngularVelocity = Vector3.zero;
        _data.Done = true;

        if (_isServer)
        {
            _server.FikaHostWorld.WorldPacket.LootSyncStructs.Add(_data);
            _server.FikaHostWorld.SetCritical();
        }
        else
        {
            _client.FikaClientWorld.WorldPacket.LootSyncStructs.Add(_data);
            _client.FikaClientWorld.SetCritical();
        }

        _lootItem.ItemOwner.RemoveItemEvent -= _removeItemHandler;
        Destroy(this);
    }
}

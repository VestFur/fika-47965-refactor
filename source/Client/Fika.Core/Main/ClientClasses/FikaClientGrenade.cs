using EFT;
using System.Collections;

namespace Fika.Core.Main.ClientClasses;

public class FikaClientGrenade : ObservedGrenade
{
    public FikaClientGrenade(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<ObservedGrenade>(this);
    }


    public FikaClientGrenade() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientGrenade>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private const float _smoothSpeed = 10f;

    private GrenadeSyncPacket _packet;
    private bool _hasPacket;

    private Coroutine _interpolationRoutine;
    private Coroutine _fuseRoutine;
    private int _previousCollision;

    private System.Collections.IEnumerator FuseFallbackCoroutine()
    {
        var delay = WeaponSource != null ? WeaponSource.GetExplDelay : 4f;
        var start = UnityEngine.Time.time;
        while (UnityEngine.Time.time - start < delay)
        {
            if (_networkDone) yield break;
            yield return null;
        }
        if (_networkDone) yield break;
        Fika.Core.Main.Utils.FikaGlobals.LogWarning($"[FikaGrenade] no Done from the host within the fuse ({delay:F2}s) - exploding locally id={Id}");
        _networkDone = true;
        OnDoneFromNet();
        if (this != null)
        {
            Fika.Core.Main.Utils.FikaGrenadeExplosion.TryExplode(this, "fuse-fallback");
        }
    }

    public override void ApplyNetPacket(GrenadeSyncPacket packet, float remoteTime)
    {
        if (_networkDone) return;
        _packet = packet;
        _previousCollision = CollisionNumber;
        CollisionNumber = packet.CollisionNumber;

        if (_packet.Done)
        {
            _hasPacket = false;
            if (_interpolationRoutine != null) StopCoroutine(_interpolationRoutine);
            _interpolationRoutine = null;
            transform.SetPositionAndRotation(_packet.Position, _packet.Rotation);
            if (Rigidbody != null)
            {
                Rigidbody.velocity = _packet.Velocity;
                Rigidbody.angularVelocity = _packet.AngularVelocity;
            }

            // Run the native observed-grenade completion first. It is what plays the local
            // explosion (ObservedGrenade.Expire -> Effects.EmitGrenade + the emission audio).
            // Pre-setting _networkDone made that native path early-return, which is why the
            // throwing client and every other client saw and heard no explosion while the
            // host did. The latch is still set afterwards for Fika's own duplicate-packet
            // guard (the native code usually sets it itself).
            Fika.Core.Main.Utils.FikaGlobals.LogInfo($"[FikaGrenade] client got Done id={Id} pos={_packet.Position} nativeDone={_networkDone}");
            OnDoneFromNet();
            if (!_networkDone)
            {
                _networkDone = true;
            }
            // An observed grenade never runs the real fuse: the visible + audible explosion comes only
            // from the native completion, so enforce it here as well in case that short-circuited.
            if (this != null)
            {
                Fika.Core.Main.Utils.FikaGrenadeExplosion.TryExplode(this, "net-done");
            }
            return;
        }

        _hasPacket = true;
        _interpolationRoutine ??= Fika.Core.Il2CppCoroutineExtensions.StartCoroutine(this, SmoothingCoroutine());
        // If the host's Done packet never arrives the grenade would only die by Expire() (no effect).
        if (_fuseRoutine == null)
        {
            _fuseRoutine = Fika.Core.Il2CppCoroutineExtensions.StartCoroutine(this, FuseFallbackCoroutine());
        }
    }

    private IEnumerator SmoothingCoroutine()
    {
        var wait = new WaitForFixedUpdate();
        while (_hasPacket)
        {
            yield return wait;
            if (!_hasPacket) yield break;

            var t = 1f - Mathf.Exp(-_smoothSpeed * Time.fixedDeltaTime);

            transform.SetPositionAndRotation(Vector3.Lerp(transform.position, _packet.Position, t),
                Quaternion.Lerp(transform.rotation, _packet.Rotation, t));

            if (Rigidbody != null)
            {
                if (_previousCollision == _packet.CollisionNumber)
                {
                    Rigidbody.velocity = Vector3.Lerp(Rigidbody.velocity, _packet.Velocity, t);

                    Rigidbody.angularVelocity = Vector3.Lerp(Rigidbody.angularVelocity, _packet.AngularVelocity, t);
                }
                else
                {
                    Rigidbody.velocity = _packet.Velocity;
                    Rigidbody.angularVelocity = _packet.AngularVelocity;
                }
            }
        }
    }
}

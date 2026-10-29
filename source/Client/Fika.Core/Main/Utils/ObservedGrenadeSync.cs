using EFT;

namespace Fika.Core.Main.Utils;

internal static class ObservedGrenadeSync
{
    internal static void Apply(Throwable throwable, GrenadeSyncPacket packet, float smoothSpeed)
    {
        // Native snapshot handling owns this flag, but Fika supplies its own
        // interpolation. Final packets may be repeated for a resting smoke
        // grenade and stale unreliable packets can arrive after the final one.
        if (throwable._networkDone) return;
        var previousCollision = throwable.CollisionNumber;
        throwable.CollisionNumber = packet.CollisionNumber;
        var body = throwable.Rigidbody;
        if (packet.Done)
        {
            throwable.transform.SetPositionAndRotation(packet.Position, packet.Rotation);
            if (body != null)
            {
                body.velocity = packet.Velocity;
                body.angularVelocity = packet.AngularVelocity;
            }

            // Native completion first so the local explosion effect and audio actually run;
            // the latch is only a fallback for Fika's duplicate-packet guard.
            throwable.OnDoneFromNet();
            if (!throwable._networkDone)
            {
                throwable._networkDone = true;
            }
            // Same guarantee as the observed grenade subclass: make sure the local explosion ran.
            if (throwable.TryCast<Grenade>() is { } grenade)
            {
                FikaGrenadeExplosion.TryExplode(grenade, "sync-net-done");
            }
            return;
        }

        var t = 1f - Mathf.Exp(-smoothSpeed * Time.deltaTime);
        throwable.transform.SetPositionAndRotation(Vector3.Lerp(throwable.transform.position, packet.Position, t),
            Quaternion.Lerp(throwable.transform.rotation, packet.Rotation, t));
        // Smoke grenades legitimately release their rigidbody while emitting.
        if (body != null)
        {
            body.velocity = previousCollision == packet.CollisionNumber
                ? Vector3.Lerp(body.velocity, packet.Velocity, t) : packet.Velocity;
            body.angularVelocity = previousCollision == packet.CollisionNumber
                ? Vector3.Lerp(body.angularVelocity, packet.AngularVelocity, t) : packet.AngularVelocity;
        }
    }
}

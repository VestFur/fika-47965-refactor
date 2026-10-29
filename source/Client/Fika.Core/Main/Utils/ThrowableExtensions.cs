using EFT;

namespace Fika.Core.Main.Utils;

public static class ThrowableExtensions
{
    public static void SetNetVelocity(this Throwable throwable, GrenadeSyncPacket packet)
    {
        var rigidbody = throwable?.Rigidbody;
        if (rigidbody == null)
        {
            return;
        }

        rigidbody.velocity = packet.Velocity;
        rigidbody.angularVelocity = packet.AngularVelocity;
    }
}

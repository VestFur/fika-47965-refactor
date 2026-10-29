using System.Diagnostics.CodeAnalysis;

namespace Fika.Core.Networking.LiteNetLib;

internal static class Trimming
{
    // NetSerializer discovers property accessors on managed packet DTOs.
    internal const DynamicallyAccessedMemberTypes SerializerMemberTypes =
        DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties;
}

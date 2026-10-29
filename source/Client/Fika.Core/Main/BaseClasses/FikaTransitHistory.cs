using EFT;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Fika.Core.Main.BaseClasses;

internal static class FikaTransitHistory
{
    internal static Il2CppStringArray AppendLocation(LocalRaidSettings settings)
    {
        var visited = settings.transition.visitedLocations;
        var length = visited?.Length ?? 0;
        var result = new Il2CppStringArray(length + 1);
        for (var i = 0; i < length; i++) result[i] = visited[i];
        result[length] = settings.location;
        return result;
    }
}

using EFT;
using EFT.Counters;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Fika.Core;

internal static class NativeCounters
{
    // Counter keys contain native boxed enums, not CLR boxed enum values.
    public static Il2CppReferenceArray<Il2CppSystem.Object> ExperienceTags() =>
        new(new Il2CppSystem.Object[]
        {
            Il2CppSystem.Enum.ToObject(Il2CppType.Of<CounterTag>(), (int)CounterTag.Exp)
        });

    public static CountersCollection.Identifier ExitStanding() =>
        new(CounterValueType.Float, new Il2CppSystem.Object[]
        {
            Il2CppSystem.Enum.ToObject(Il2CppType.Of<CounterTag>(), (int)CounterTag.FenceStanding),
            Il2CppSystem.Enum.ToObject(Il2CppType.Of<EFenceStandingSource>(), (int)EFenceStandingSource.ExitStanding)
        });
}

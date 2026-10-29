using System;
using System.Reflection;
using EFT;
using Fika.Core.Main.Utils;

namespace Fika.Core.UI.Patches.LoadingScreen;

/// <summary>
/// Resolves the <c>TarkovApplication._LoadMapAndData</c> progress callbacks the loading screen patches need.
/// </summary>
/// <remarks>
/// Both callbacks live on the compiler generated display class of the <c>LoadMapAndData</c> state machine, and
/// that class is numbered from the metadata order: <c>__c__DisplayClass216_0</c> on the build this port started
/// from, <c>__c__DisplayClass237_0</c> on EFT 1.2.0.47888 (<c>_LoadMapAndData_d__237</c> is the state machine).
/// Only the method names are stable across builds, so the display class is searched for instead of hard coded;
/// the resolved name is written to the diagnostics log so a later build can be checked from the log alone.
/// The cache callback (<c>b__0</c>) and the loading callback (<c>b__1</c>) both take the single
/// <c>float</c> progress argument the patches bind by name.
/// </remarks>
internal static class LoadMapAndDataTargets
{
    internal static MethodBase Resolve(string methodName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var nested in typeof(TarkovApplication).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            var method = nested.GetMethod(methodName, flags, null, new[] { typeof(float) }, null);
            if (method == null) continue;
            
            return method;
        }
        throw new MissingMethodException(
            $"TarkovApplication has no nested type declaring {methodName}(float) in this game build.");
    }
}

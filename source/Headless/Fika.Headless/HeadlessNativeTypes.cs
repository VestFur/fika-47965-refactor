using System.Reflection;
using BepInEx.Logging;
using Fika.Core.Main.Utils;

namespace Fika.Headless;

internal static class HeadlessNativeTypes
{
    internal static void RegisterAll(Assembly assembly, ManualLogSource logger)
    {
        FikaNativeTypes.RegisterAll(assembly, logger);
    }
}

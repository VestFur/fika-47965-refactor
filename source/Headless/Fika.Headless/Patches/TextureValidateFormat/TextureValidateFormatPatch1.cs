using SPTushonka.Reflection.Patching;
using System.Reflection;

namespace Fika.Headless.Patches.TextureValidateFormat;

/// <summary>
/// The RenderTextureFormat overload of Texture.ValidateFormat is a managed
/// wrapper in this Unity build. Patch its native support query instead so
/// native callers receive the same headless-compatible result.
/// </summary>
public class ValidateFormatPatch1 : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(SystemInfo).GetMethod(nameof(SystemInfo.SupportsRenderTextureFormat),
            BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(RenderTextureFormat) }, null);
    }

    [PatchPostfix]
    public static void Postfix(ref bool __result)
    {
        __result = true;
    }
}

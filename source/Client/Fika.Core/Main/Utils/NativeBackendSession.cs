using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Sends a backend operation (the payload the game posts to
/// /client/game/profile/items/moving) without naming any Newtonsoft.Json type in
/// managed metadata.
/// <para>
/// The direct call builds a <c>JObject</c> and hands it to
/// <c>ClientBackendSession.SendOperationRightNow</c>. With a managed
/// Newtonsoft.Json in BepInEx/plugins that constructor produces a managed object
/// which the native method cannot accept. The JSON is parsed by the game's own
/// JToken.Parse through IL2CPP instead, and the same method is invoked with the
/// raw pointer.
/// </para>
/// </summary>
internal static unsafe class NativeBackendSession
{
    private static IntPtr _parse;
    private static bool _parseResolved;

    /// <summary>Parses Fika built JSON into a native Newtonsoft JToken.</summary>
    internal static IntPtr ParsePayload(string json)
    {
        if (!_parseResolved)
        {
            _parseResolved = true;
            var jToken = NativeIl2Cpp.FindClass("Newtonsoft.Json", "Newtonsoft.Json.Linq", "JToken");
            _parse = NativeIl2Cpp.FindMethod(jToken, "Parse", 1);
            if (_parse == IntPtr.Zero)
            {
                FikaGlobals.LogError("Could not locate Newtonsoft.Json JToken.Parse through IL2CPP: " +
                    NativeIl2Cpp.Describe("Newtonsoft.Json", "Parse"));
            }
        }

        if (_parse == IntPtr.Zero) return IntPtr.Zero;
        return NativeIl2Cpp.Invoke(_parse, IntPtr.Zero, IL2CPP.il2cpp_string_new(json));
    }

    /// <summary>
    /// Sends the operation through IL2CPP. Returns false when the game's parser or
    /// the backend method cannot be reached, so the caller can fall back.
    /// </summary>
    internal static bool TrySend(Il2CppObjectBase session, string payloadJson, Il2CppObjectBase callback)
    {
        if (session == null) return false;

        try
        {
            var payload = ParsePayload(payloadJson);
            if (payload == IntPtr.Zero) return false;

            var runtimeClass = IL2CPP.il2cpp_object_get_class(session.Pointer);
            var method = NativeIl2Cpp.FindMethod(runtimeClass, "SendOperationRightNow", 2);
            if (method == IntPtr.Zero)
            {
                // The runtime type can be a wrapper that does not inherit the
                // method from ClientBackendSession, so search the declaring class
                // as well before giving up on the native path.
                var declaring = NativeIl2Cpp.FindClass("Assembly-CSharp", "EFT", "ClientBackendSession");
                method = NativeIl2Cpp.FindMethod(declaring, "SendOperationRightNow", 2);
            }

            if (method == IntPtr.Zero)
            {
                FikaGlobals.LogError("Could not resolve ClientBackendSession.SendOperationRightNow through IL2CPP. " +
                    NativeIl2Cpp.Describe("Assembly-CSharp", "SendOperationRightNow"));
                return false;
            }

            NativeIl2Cpp.Invoke(method, session.Pointer, payload,
                callback == null ? IntPtr.Zero : callback.Pointer);
            return true;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"Native backend send failed, falling back to the interop call: {exception.Message}");
            return false;
        }
    }
}

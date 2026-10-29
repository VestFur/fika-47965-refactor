using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Preserves inline ref value wrappers while generating Fika detours.
/// <para>
/// The detour trampoline hands a byref value argument (for example <c>ref DamageInfo</c>) to the
/// managed replacement as a real wrapper and has to copy the - possibly modified - value back into
/// the caller's inline storage afterwards. Il2CppInterop has emitted that copy in two shapes:
/// up to at least 1.5.x the trampoline ended with <c>call Il2CppObjectBaseToPtr</c> + <c>stind.i</c>,
/// which stores an object pointer over the first eight structure bytes and is rewritten here into a
/// field-wise copy; 1.6.7 ends with <c>call TrampolineHelpers.CopyBoxedStructTo(Il2CppObjectBase src,
/// IntPtr dst)</c>, which already copies the complete boxed structure back and must be left alone.
/// A trampoline that contains neither shape is reported once and left untouched instead of failing
/// the patch that depends on it.
/// </para>
/// </summary>
internal sealed class NativeDetourScope : IDisposable
{
    private static readonly ManualLogSource _logger = Logger.CreateLogSource("Fika.NativeDetour");
    private readonly Harmony _harmony;
    private readonly MethodInfo _readEmitter;
    private readonly MethodInfo _generator;
    private static readonly Dictionary<int, Type> ValueLocals = new();
    private static bool _reportedCopyBack;
    private static bool _reportedRewrite;
    private static bool _reportedMissing;

    internal NativeDetourScope(MethodBase target)
    {
        if (!Array.Exists(target.GetParameters(), p => p.ParameterType.IsByRef &&
                typeof(Il2CppSystem.ValueType).IsAssignableFrom(p.ParameterType.GetElementType()))) return;
        if (ValueLocals.Count != 0) throw new InvalidOperationException("Native detour value scopes cannot overlap.");
        var patcher = AccessTools.TypeByName("Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher")
            ?? throw new TypeLoadException("IL2CPP detour backend missing");
        _readEmitter = AccessTools.DeclaredMethod(patcher, "EmitConvertArgumentToManaged");
        _generator = AccessTools.DeclaredMethod(patcher, "GenerateNativeToManagedTrampoline");
        _harmony = new Harmony("com.fika.core.native-detour-values");
        try
        {
            _harmony.Patch(_readEmitter, prefix: new HarmonyMethod(typeof(NativeDetourScope), nameof(ReadValueArgument)));
            _harmony.Patch(_generator, postfix: new HarmonyMethod(typeof(NativeDetourScope), nameof(WriteValueArguments)));
        }
        catch { Dispose(); throw; }
    }

    private static bool ReadValueArgument(ILGenerator il, int argIndex, Type managedParamType, ref LocalBuilder variable)
    {
        if (!managedParamType.IsByRef) return true;
        var element = managedParamType.GetElementType();
        if (!typeof(Il2CppSystem.ValueType).IsAssignableFrom(element)) return true;
        variable = il.DeclareLocal(element);
        ValueLocals.Add(variable.LocalIndex, element);
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg, argIndex);
        il.Emit(System.Reflection.Emit.OpCodes.Call, AccessTools.DeclaredMethod(typeof(NativeDetourScope), nameof(ReadInline)).MakeGenericMethod(element));
        il.Emit(System.Reflection.Emit.OpCodes.Stloc, variable);
        il.Emit(System.Reflection.Emit.OpCodes.Ldloca, variable);
        return false;
    }

    private static void WriteValueArguments(DynamicMethodDefinition __result)
    {
        if (ValueLocals.Count == 0) return;

        var instructions = __result.Definition.Body.Instructions;
        var writeInline = AccessTools.DeclaredMethod(typeof(NativeDetourScope), nameof(WriteInline));
        var handled = new HashSet<int>();
        var rewritten = 0;
        var copiedBack = 0;
        for (var i = 1; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (instruction.OpCode != Mono.Cecil.Cil.OpCodes.Call ||
                instruction.Operand is not Mono.Cecil.MethodReference call ||
                instructions[i - 1].Operand is not VariableDefinition local ||
                !ValueLocals.TryGetValue(local.Index, out var element)) continue;

            if (call.Name == "CopyBoxedStructTo")
            {
                // Il2CppInterop 1.6.7: the stack already holds the caller's inline address and the
                // updated wrapper, and CopyBoxedStructTo copies the whole boxed structure back.
                if (handled.Add(local.Index)) copiedBack++;
                continue;
            }

            if (call.Name != "Il2CppObjectBaseToPtr" || i + 1 >= instructions.Count ||
                instructions[i + 1].OpCode != Mono.Cecil.Cil.OpCodes.Stind_I) continue;

            // Legacy: the stack holds the caller's inline address and the updated wrapper, and the
            // wrapper would be stored as an object pointer over the first eight structure bytes.
            // Copy the fields back instead, including GC reference barriers.
            instruction.Operand = __result.Module.ImportReference(writeInline.MakeGenericMethod(element));
            instructions[i + 1].OpCode = Mono.Cecil.Cil.OpCodes.Nop;
            if (handled.Add(local.Index)) rewritten++;
        }

        if (rewritten > 0 && !_reportedRewrite)
        {
            _reportedRewrite = true;
            _logger.LogInfo($"Rewrote {rewritten} ref value argument(s) whose trampoline stored an object pointer over the inline structure; fields are copied back instead.");
        }
        if (copiedBack > 0 && !_reportedCopyBack)
        {
            _reportedCopyBack = true;
            _logger.LogInfo($"Il2CppInterop copies {copiedBack} ref value argument(s) back with CopyBoxedStructTo; the trampoline needs no rewrite.");
        }
        if (handled.Count != ValueLocals.Count && !_reportedMissing)
        {
            _reportedMissing = true;
            // The patch itself stays installed: the read side is independent of the writeback, so the
            // managed code still sees the correct value. Only the copy back to native memory is missing,
            // which has to be visible instead of a silent "patch did not apply" at startup.
            _logger.LogWarning($"{__result.Definition.Name}: {handled.Count} of {ValueLocals.Count} ref value argument(s) have a native writeback; " +
                "the remaining argument(s) keep their pre-call value in native memory.");
        }
        ValueLocals.Clear();
    }

    private static T ReadInline<T>(IntPtr data) where T : Il2CppObjectBase =>
        Il2CppInterop.Runtime.Runtime.Il2CppObjectPool.Get<T>(
            NativeInjectionScope.BoxNativeValue(Il2CppClassPointerStore<T>.NativeClassPtr, data));

    private static unsafe void WriteInline<T>(IntPtr destination, T value) where T : Il2CppObjectBase
    {
        ArgumentNullException.ThrowIfNull(value);
        var klass = Il2CppClassPointerStore<T>.NativeClassPtr;
        var header = IL2CPP.il2cpp_object_unbox(value.Pointer).ToInt64() - value.Pointer.ToInt64();
        var iterator = IntPtr.Zero;
        IntPtr field;
        while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iterator)) != IntPtr.Zero)
        {
            if ((IL2CPP.il2cpp_field_get_flags(field) & (int)FieldAttributes.Static) != 0) continue;
            var offset = (long)IL2CPP.il2cpp_field_get_offset(field);
            // field_set_value adds the boxed field offset to its owner pointer.
            // Adjust for the caller's unboxed struct without touching its header.
            var fieldClass = IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_field_get_type(field));
            if (IL2CPP.il2cpp_class_is_valuetype(fieldClass))
                IL2CPP.il2cpp_field_set_value(destination - (int)header, field, (byte*)value.Pointer + offset);
            else
                IL2CPP.il2cpp_gc_wbarrier_set_field(destination - (int)header,
                    destination + (int)(offset - header), *(IntPtr*)((byte*)value.Pointer + offset));
        }
        GC.KeepAlive(value);
    }

    public void Dispose()
    {
        if (_harmony == null) return;
        if (_readEmitter != null) _harmony.Unpatch(_readEmitter, HarmonyPatchType.All, _harmony.Id);
        if (_generator != null) _harmony.Unpatch(_generator, HarmonyPatchType.All, _harmony.Id);
        ValueLocals.Clear();
    }
}

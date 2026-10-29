using System;

namespace Fika.Core;

public static class NativeTime
{
    public static DateTime ToManaged(this Il2CppSystem.DateTime value) => new(value.Ticks, (DateTimeKind)value.Kind);
    public static Il2CppSystem.DateTime ToNative(this DateTime value) => new(value.Ticks, (Il2CppSystem.DateTimeKind)value.Kind);
    public static TimeSpan ToManaged(this Il2CppSystem.TimeSpan value) => TimeSpan.FromTicks(value.Ticks);
    public static Il2CppSystem.TimeSpan ToNative(this TimeSpan value) => new(value.Ticks);

    public static DateTime? ToManaged(this Il2CppSystem.Nullable<Il2CppSystem.DateTime> value) =>
        value.HasValue ? value.Value.ToManaged() : null;
    public static TimeSpan? ToManaged(this Il2CppSystem.Nullable<Il2CppSystem.TimeSpan> value) =>
        value.HasValue ? value.Value.ToManaged() : null;
    public static Il2CppSystem.Nullable<Il2CppSystem.DateTime> ToNative(this DateTime? value) =>
        value.HasValue ? new(value.Value.ToNative()) : new();
    public static Il2CppSystem.Nullable<Il2CppSystem.TimeSpan> ToNative(this TimeSpan? value) =>
        value.HasValue ? new(value.Value.ToNative()) : new();
}

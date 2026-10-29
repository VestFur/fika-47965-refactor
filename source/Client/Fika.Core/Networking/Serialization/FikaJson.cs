using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using EFT;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Fika.Core.Main.Utils;

namespace Fika.Core.Networking.Serialization;

/// <summary>Separates managed Fika DTOs from native game objects at the JSON boundary.</summary>
internal static class FikaJson
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = ManagedJson.CreateOptions();
        options.Converters.Insert(0, new MongoIdConverter());
        options.Converters.Insert(1, new NativeObjectConverterFactory());
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    private sealed class MongoIdConverter : JsonConverter<MongoID>
    {
        public override MongoID Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var thread = new NativeThreadScope();
            return new(reader.GetString());
        }
        public override void Write(Utf8JsonWriter writer, MongoID value, JsonSerializerOptions options)
        {
            using var thread = new NativeThreadScope();
            writer.WriteStringValue(value.ToString());
        }
    }

    private sealed class NativeObjectConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => typeof(Il2CppObjectBase).IsAssignableFrom(type);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(NativeObjectConverter<>).MakeGenericType(type));
    }

    private sealed class NativeObjectConverter<T> : JsonConverter<T> where T : Il2CppObjectBase
    {
        // Set once the managed Newtonsoft.Json that shadows the IL2CPP proxy has
        // been detected; every later call then goes through IL2CPP directly.
        private static bool _throughIl2Cpp;

        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var thread = new NativeThreadScope();
            using var document = JsonDocument.ParseValue(ref reader);
            var json = document.RootElement.GetRawText();

            if (_throughIl2Cpp)
            {
                var bridged = NativeNewtonsoftJson.Deserialize(json, type);
                return bridged == null ? null : bridged.Cast<T>();
            }

            try
            {
                return DeserializeThroughInterop(json, type);
            }
            catch (Exception exception) when (IsShadowedInterop(exception))
            {
                _throughIl2Cpp = true;
                ReportShadowed(exception);
                var bridged = NativeNewtonsoftJson.Deserialize(json, type);
                return bridged == null ? null : bridged.Cast<T>();
            }
        }

        /// <summary>
        /// Kept in its own non-inlined method: a linkage failure against a
        /// shadowing Newtonsoft.Json surfaces while this method is compiled, and a
        /// try/catch inside the same method could never observe it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T DeserializeThroughInterop(string json, Type type)
        {
            // Use the non-generic native overload. A managed generic DTO has no
            // AOT instantiation in the game and must never reach JsonConvert<T>.
            var native = Newtonsoft.Json.JsonConvert.DeserializeObject(json, Il2CppType.From(type));
            return native == null ? null : native.Cast<T>();
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            using var thread = new NativeThreadScope();
            string json;

            if (_throughIl2Cpp)
            {
                json = NativeNewtonsoftJson.Serialize(value.Cast<Il2CppSystem.Object>());
            }
            else
            {
                try
                {
                    json = SerializeThroughInterop(value);
                }
                catch (Exception exception) when (IsShadowedInterop(exception))
                {
                    _throughIl2Cpp = true;
                    ReportShadowed(exception);
                    json = NativeNewtonsoftJson.Serialize(value.Cast<Il2CppSystem.Object>());
                }
            }

            using var document = JsonDocument.Parse(json);
            document.RootElement.WriteTo(writer);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string SerializeThroughInterop(T value) =>
            Newtonsoft.Json.JsonConvert.SerializeObject(value.Cast<Il2CppSystem.Object>());

        /// <summary>
        /// A managed Newtonsoft.Json in BepInEx/plugins wins assembly resolution
        /// over the IL2CPP interop proxy and lacks the IL2CPP overloads. Both
        /// helpers live in <see cref="ShadowedAssemblyReporter"/>, which also
        /// names the assembly file that won and doubles as the preflight a start
        /// path can run before it depends on those overloads.
        /// </summary>
        private static bool IsShadowedInterop(Exception exception) =>
            ShadowedAssemblyReporter.IsShadowedInterop(exception);

        private static void ReportShadowed(Exception exception) =>
            ShadowedAssemblyReporter.ReportShadowed(exception);
    }
}

internal static class FikaJsonCompat
{
    public static string Serialize<T>(T value) => FikaJson.Serialize(value);
    public static T Deserialize<T>(string json) => FikaJson.Deserialize<T>(json);
}

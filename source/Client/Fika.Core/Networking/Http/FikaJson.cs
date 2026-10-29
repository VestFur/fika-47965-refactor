using System.Text.Json;

// Compatibility facade for code migrated from the official 5.0 tree and
// consumed by the separate Fika.Headless plugin.
namespace Fika.Core.Networking.Http;

public static class FikaJson
{
    public static JsonSerializerOptions Options => Fika.Core.Networking.Serialization.FikaJson.Options;

    public static string Serialize<T>(T value) => Fika.Core.Networking.Serialization.FikaJsonCompat.Serialize(value);
    public static T Deserialize<T>(string json) => Fika.Core.Networking.Serialization.FikaJsonCompat.Deserialize<T>(json);
}

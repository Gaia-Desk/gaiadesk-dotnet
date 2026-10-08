using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

#if NETSTANDARD2_1
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif

namespace GaiaDesk
{
    /// <summary>The JSON settings the SDK reads and writes the API's objects with.</summary>
    public static class GaiaDeskJson
    {
        /// <summary>Snake_case wire names (from the models' attributes), nulls written, relaxed escaping off.</summary>
        public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General)
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        /// <summary>A value as compact JSON, as the SDK sends it.</summary>
        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

        internal static T To<T>(JsonElement json, string operation)
        {
            try
            {
                var v = json.Deserialize<T>(Options);
                if (v is null) throw new JsonException("null");
                return v;
            }
            catch (JsonException e)
            {
                throw new ProtocolException($"the GaiaDesk API answered {operation} with JSON that is not the documented shape: {e.Message}",
                    new ErrorDetails { Operation = operation, Json = json.Clone(), ExitCode = 255 });
            }
        }

        internal static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();

        internal static JsonElement Element(JsonNode? node) => Parse(node?.ToJsonString() ?? "null");

        internal static byte[] Utf8(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

        internal static string? Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        internal static bool IsObject(JsonElement o) => o.ValueKind == JsonValueKind.Object;

        internal static bool TryProp(JsonElement o, string name, out JsonElement v)
        {
            v = default;
            return o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out v);
        }
    }

    /// <summary>Reads a string, or an array of strings joined by spaces; writes a string.</summary>
    internal sealed class StringOrListConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return reader.GetString() ?? "";
            if (reader.TokenType == JsonTokenType.Null) return "";
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected a string or a list of strings");
            var parts = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String) throw new JsonException("expected a list of strings");
                parts.Add(reader.GetString() ?? "");
            }
            return string.Join(" ", parts);
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
}

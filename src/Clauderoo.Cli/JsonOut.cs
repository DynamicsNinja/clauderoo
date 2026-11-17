using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Clauderoo.Cli;

/// <summary>Shared writer for every --json payload: schemaVersion first, camelCase, UTC ISO 8601 times.</summary>
public static class JsonOut
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new UtcDateTimeOffsetConverter() },
    };

    public static void Write(TextWriter w, object payload)
    {
        var obj = new JsonObject { ["schemaVersion"] = SchemaVersion };
        if (JsonSerializer.SerializeToNode(payload, Options) is JsonObject props)
            foreach (var kv in props.ToList())
            {
                props.Remove(kv.Key);
                obj[kv.Key] = kv.Value;
            }
        w.WriteLine(obj.ToJsonString());
    }

    public static void Error(TextWriter err, string message) =>
        err.WriteLine(new JsonObject { ["schemaVersion"] = SchemaVersion, ["error"] = message }.ToJsonString());

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
            reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions o) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
    }
}

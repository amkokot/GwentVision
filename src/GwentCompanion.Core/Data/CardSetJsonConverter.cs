using System.Text.Json;
using System.Text.Json.Serialization;

namespace GwentCompanion.Core.Data;

public sealed class CardSetJsonConverter : JsonConverter<IReadOnlySet<string>>
{
    public override IReadOnlySet<string>? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<string[]>(ref reader, options)?.ToHashSet(StringComparer.OrdinalIgnoreCase);
    public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Order(StringComparer.OrdinalIgnoreCase).ToArray(), options);
}

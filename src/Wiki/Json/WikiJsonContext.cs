using System.Text.Json.Serialization;
namespace Wiki.Json;

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(WikiError))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, string>))]
public partial class WikiJsonContext : JsonSerializerContext { }

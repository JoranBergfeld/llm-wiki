using System.Text.Json.Serialization;
using Wiki.Cli.Commands;
using Wiki.Services;
namespace Wiki.Json;

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(WikiError))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, string>))]
[JsonSerializable(typeof(InitResult))]
[JsonSerializable(typeof(UpsertResult))]
public partial class WikiJsonContext : JsonSerializerContext { }

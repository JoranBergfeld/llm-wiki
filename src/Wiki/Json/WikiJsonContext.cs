using System.Text.Json.Serialization;
using Wiki.Cli.Commands;
using Wiki.Services;
using Wiki.State;
namespace Wiki.Json;

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(WikiError))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, string>))]
[JsonSerializable(typeof(InitResult))]
[JsonSerializable(typeof(UpsertResult))]
[JsonSerializable(typeof(PageSummary))]
[JsonSerializable(typeof(PageSummary[]))]
[JsonSerializable(typeof(PageView))]
[JsonSerializable(typeof(ReindexReport))]
[JsonSerializable(typeof(SourceAddResult))]
[JsonSerializable(typeof(LedgerEntryData))]
[JsonSerializable(typeof(LedgerEntryData[]))]
[JsonSerializable(typeof(ResumePlanView))]
[JsonSerializable(typeof(IngestAdvanceResult))]
[JsonSerializable(typeof(LintStateData))]
[JsonSerializable(typeof(Hit))]
[JsonSerializable(typeof(Hit[]))]
public partial class WikiJsonContext : JsonSerializerContext { }

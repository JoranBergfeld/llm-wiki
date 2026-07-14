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
[JsonSerializable(typeof(RenameResult))]
[JsonSerializable(typeof(PageSummary))]
[JsonSerializable(typeof(PageSummary[]))]
[JsonSerializable(typeof(PageView))]
[JsonSerializable(typeof(ReindexReport))]
[JsonSerializable(typeof(SourceAddResult))]
[JsonSerializable(typeof(SourceSummary))]
[JsonSerializable(typeof(SourceSummary[]))]
[JsonSerializable(typeof(SourceView))]
[JsonSerializable(typeof(SourceImpactEntry))]
[JsonSerializable(typeof(SourceImpactEntry[]))]
[JsonSerializable(typeof(RetractResult))]
[JsonSerializable(typeof(LedgerEntryData))]
[JsonSerializable(typeof(LedgerEntryData[]))]
[JsonSerializable(typeof(ResumePlanView))]
[JsonSerializable(typeof(IngestAdvanceResult))]
[JsonSerializable(typeof(LintStateData))]
[JsonSerializable(typeof(Hit))]
[JsonSerializable(typeof(Hit[]))]
[JsonSerializable(typeof(IssueData))]
[JsonSerializable(typeof(IssueData[]))]
[JsonSerializable(typeof(LintReport))]
[JsonSerializable(typeof(PendingView))]
[JsonSerializable(typeof(PendingView[]))]
[JsonSerializable(typeof(ProposalData))]
[JsonSerializable(typeof(ProposalData[]))]
[JsonSerializable(typeof(string[]))]
public partial class WikiJsonContext : JsonSerializerContext { }

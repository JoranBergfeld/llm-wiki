using Microsoft.Extensions.DependencyInjection;
using Wiki.Mcp.Tools;

namespace Wiki.Mcp;

// Every intent tool `wiki mcp` serves (spec, Tool catalogue). One line per
// tool class, always through the generic WithTools<T>(instance): it carries
// [DynamicallyAccessedMembers], so the trimmer keeps the tool methods. Never
// WithToolsFromAssembly or WithTools(IEnumerable<Type>) - both are
// [RequiresUnreferencedCode], and a trimmed tool fails only at runtime in the
// published AOT binary (issue #29).
//
// Tool results are CallToolResult built by IntentRun, so no tool DTO needs
// a JsonSerializerContext; keep tool parameters primitive (string, bool,
// int?) for the same reason.
public static class McpToolCatalog
{
    public static IMcpServerBuilder AddWikiTools(this IMcpServerBuilder builder, CliAdapter cli, IngestInbox inbox) => builder
        .WithTools(new SearchWikiTool(cli))
        .WithTools(new ReadPageTool(cli))
        .WithTools(new BrowseWikiTool(cli))
        .WithTools(new CheckHealthTool(cli))
        .WithTools(new WorkIssuesTool(cli))
        .WithTools(new AuditPageTool(cli))
        .WithTools(new WritePageTool(cli))
        .WithTools(new RenamePageTool(cli))
        .WithTools(new AddCategoryTool(cli))
        .WithTools(new RetractSourceTool(cli))
        .WithTools(new RepairVaultTool(cli))
        .WithTools(new ProposeChangeTool(cli))
        .WithTools(new ReviewQueueTool(cli))
        .WithTools(new DecideTool(cli))
        .WithTools(new IngestSourceTool(cli, inbox))
        .WithTools(new ContinueIngestTool(cli))
        .WithTools(new ReadPersonaTool(cli))
        .WithTools(new DistillPersonaTool(cli))
        .WithTools(new RecordOwnerStatementTool(cli));
}

using System;
using System.CommandLine;
using System.IO;
using Wiki.Core;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki page ...` command group. This task (12) wires up only `upsert`
// (create path); `show`/`list`/`rename`/`set-status`/`backlinks` land in
// later tasks and get added as siblings under the same `page` group.
public static class PageCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var page = new Command("page", "Create, update, and query wiki pages");
        page.Add(BuildUpsert(vaultOption, jsonOption, stdout, stdin));
        return page;
    }

    private static Command BuildUpsert(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var typeOption = new Option<string>("--type")
        {
            Required = true,
            Description = "Page type: summary | entity | concept | overview",
        };
        var titleOption = new Option<string>("--title")
        {
            Required = true,
            Description = "Page title",
        };
        // Not CLI-Required: a missing --summary is a *blocking validation*
        // (code=summary-required) raised by PageService, not a parser error,
        // so the agent gets our JSON envelope instead of System.CommandLine's
        // generic usage text.
        var summaryOption = new Option<string?>("--summary")
        {
            Description = "One-line routing description (required; stored as frontmatter 'summary')",
        };
        var idOption = new Option<string?>("--id")
        {
            Description = "Existing page id to update (update path lands in a later task)",
        };
        var sourcesOption = new Option<string?>("--sources")
        {
            Description = "Comma-separated source ids this page cites",
        };
        var tagsOption = new Option<string?>("--tags")
        {
            Description = "Comma-separated free-form tags",
        };
        var allowDanglingOption = new Option<bool>("--allow-dangling")
        {
            Description = "Permit wikilinks whose target doesn't exist yet (instead of rejecting the write)",
        };
        // Documents intent per the CLI spec; body resolution itself always
        // falls back to stdin when --body-file isn't given, so this flag is
        // not required to be present for stdin to be read.
        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read the page body from stdin (default body source when --body-file is omitted)",
        };
        var bodyFileOption = new Option<string?>("--body-file")
        {
            Description = "Read the page body from this file instead of stdin",
        };

        var upsert = new Command("upsert", "Create (no --id) or update (--id) a wiki page")
        {
            typeOption,
            titleOption,
            summaryOption,
            idOption,
            sourcesOption,
            tagsOption,
            allowDanglingOption,
            stdinOption,
            bodyFileOption,
        };

        upsert.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var type = PageTypeX.Parse(parseResult.GetRequiredValue(typeOption));
            var title = parseResult.GetRequiredValue(titleOption);
            var summary = parseResult.GetValue(summaryOption) ?? "";
            var id = parseResult.GetValue(idOption);
            var sources = SplitCsv(parseResult.GetValue(sourcesOption));
            var tags = SplitCsv(parseResult.GetValue(tagsOption));
            var allowDangling = parseResult.GetValue(allowDanglingOption);
            var bodyFile = parseResult.GetValue(bodyFileOption);

            var body = ResolveBody(ctx, bodyFile);

            var vault = ctx.ResolveVault();
            var cfg = ctx.LoadConfig();
            var req = new UpsertRequest(type, title, id, summary, sources, tags, body, allowDangling);

            var service = new PageService();
            var result = service.Upsert(vault, cfg, req);
            ctx.EmitOk(result);
            return 0;
        }));

        return upsert;
    }

    private static string ResolveBody(CommandContext ctx, string? bodyFile)
    {
        if (!string.IsNullOrEmpty(bodyFile))
        {
            if (!File.Exists(bodyFile))
                throw new ValidationException("body-file-not-found", $"--body-file '{bodyFile}' does not exist", bodyFile);
            return File.ReadAllText(bodyFile);
        }
        return ctx.In.ReadToEnd();
    }

    private static string[] SplitCsv(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

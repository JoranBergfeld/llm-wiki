using System.CommandLine;
using System.IO;
using System.Linq;
using Spectre.Console;
using Wiki.Core;
using Wiki.Docs;
using Wiki.State;

namespace Wiki.Cli.Commands;

// `wiki issues ...` command group (spec §12): read/resolve access to the
// occurrence-merging issues store. Nothing in THIS task files issues from a
// real check (that's lint, a later M3 task) - `list`/`show`/`resolve` are
// enough to exercise the store end-to-end and give the agent a way to work a
// backlog once something else starts writing to it.
public static class IssuesCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var issues = new Command("issues", "Query and resolve lint findings tracked in .wiki/issues.json");
        issues.Add(BuildList(vaultOption, jsonOption, stdout, stdin));
        issues.Add(BuildShow(vaultOption, jsonOption, stdout, stdin));
        issues.Add(BuildResolve(vaultOption, jsonOption, stdout, stdin));
        return issues;
    }

    private static Command BuildList(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var kindOption = new Option<string?>("--kind")
        {
            Description = "Filter to a single issue kind (orphan | dangling-link | stale | coverage-gap | index-drift | oversize | rename-drift | needs-review-backlog | pending-backlog | review-rejected)",
        };
        var statusOption = new Option<string?>("--status")
        {
            Description = "Filter to a single status: open | resolved",
        };

        var list = new Command("list", "List issues, optionally filtered by --kind and/or --status")
        {
            kindOption,
            statusOption,
        };

        list.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var kindRaw = parseResult.GetValue(kindOption);
            var kind = string.IsNullOrEmpty(kindRaw) ? (IssueKind?)null : IssueKindX.Parse(kindRaw);
            var status = ValidateStatus(parseResult.GetValue(statusOption));

            var vault = ctx.ResolveVault();
            var store = new Issues();
            store.Load(vault);
            var results = store.List(kind, status).Select(ToData).ToArray();

            if (ctx.Json)
            {
                ctx.EmitOk(results);
            }
            else
            {
                RenderListTable(ctx.Out, results);
            }
            return 0;
        }));

        return list;
    }

    private static Command BuildShow(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Issue id (ULID) to look up",
        };

        var show = new Command("show", "Show a single issue")
        {
            idArgument,
        };

        show.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);

            var vault = ctx.ResolveVault();
            var store = new Issues();
            store.Load(vault);
            var issue = store.Get(id) ?? throw new ValidationException("not-found", $"no issue found for id '{id}'");

            if (ctx.Json)
            {
                ctx.EmitOk(ToData(issue));
            }
            else
            {
                RenderShowPanel(ctx.Out, issue);
            }
            return 0;
        }));

        return show;
    }

    private static Command BuildResolve(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Issue id (ULID) to mark resolved",
        };
        var noteOption = new Option<string?>("--note")
        {
            Description = "Optional free-text resolution note",
        };

        var resolve = new Command("resolve", "Mark an issue resolved")
        {
            idArgument,
            noteOption,
        };

        resolve.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);
            var note = parseResult.GetValue(noteOption);

            var vault = ctx.ResolveVault();
            var store = new Issues();
            store.Load(vault);
            store.Resolve(id, note);
            store.Save(vault);

            var utcIso = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
            LogFile.Append(vault, utcIso, "issue-resolve", id, note ?? "(no note)");

            var resolved = store.Get(id)!;
            if (ctx.Json)
            {
                ctx.EmitOk(ToData(resolved));
            }
            else
            {
                var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(ctx.Out) });
                console.MarkupLine($"[green]OK[/] resolved {Markup.Escape(resolved.Id)}");
            }
            return 0;
        }));

        return resolve;
    }

    // `--status` is a raw string (Issue.Status isn't an enum), so unlike the
    // sibling `--kind` option there's no Parse() to lean on. Validate it here
    // against the closed lifecycle set before it reaches Issues.List's `!=`
    // filter - otherwise a typo like `--status opne` matches nothing and
    // returns a clean-looking empty list at exit 0, which is a real footgun
    // for `wiki issues list` being the reflect loop's ground-truth read
    // surface. null/omitted still means "no filter" (all statuses). Code
    // `invalid-issue-status` mirrors the enum-style `invalid-issue-kind`.
    private static string? ValidateStatus(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;
        if (raw != "open" && raw != "resolved")
            throw new ValidationException("invalid-issue-status", $"unknown issue status '{raw}'; expected 'open' or 'resolved'");
        return raw;
    }

    private static IssueData ToData(Issue e) => new()
    {
        Id = e.Id,
        Kind = IssueKindX.ToWire(e.Kind),
        Subject = e.Subject,
        Detail = e.Detail,
        FirstSeen = e.FirstSeen,
        LastSeen = e.LastSeen,
        Occurrences = e.Occurrences,
        Status = e.Status,
        ResolveNote = e.ResolveNote,
    };

    private static void RenderListTable(TextWriter output, System.Collections.Generic.IReadOnlyList<IssueData> rows)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var table = new Table();
        table.AddColumn("Id");
        table.AddColumn("Kind");
        table.AddColumn("Subject");
        table.AddColumn("Status");
        table.AddColumn("Occurrences");
        table.AddColumn("LastSeen");

        foreach (var r in rows)
        {
            table.AddRow(
                Markup.Escape(r.Id),
                Markup.Escape(r.Kind),
                Markup.Escape(r.Subject),
                Markup.Escape(r.Status),
                r.Occurrences.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Markup.Escape(r.LastSeen));
        }

        console.Write(table);
        console.MarkupLine($"[grey]{rows.Count} issue(s)[/]");
    }

    private static void RenderShowPanel(TextWriter output, Issue issue)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var body = new System.Text.StringBuilder();
        body.Append("[bold]id[/]: ").AppendLine(Markup.Escape(issue.Id));
        body.Append("[bold]kind[/]: ").AppendLine(Markup.Escape(IssueKindX.ToWire(issue.Kind)));
        body.Append("[bold]subject[/]: ").AppendLine(Markup.Escape(issue.Subject));
        body.Append("[bold]status[/]: ").AppendLine(Markup.Escape(issue.Status));
        body.Append("[bold]first_seen[/]: ").AppendLine(Markup.Escape(issue.FirstSeen));
        body.Append("[bold]last_seen[/]: ").AppendLine(Markup.Escape(issue.LastSeen));
        body.Append("[bold]occurrences[/]: ").AppendLine(issue.Occurrences.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (issue.ResolveNote is not null)
            body.Append("[bold]resolve_note[/]: ").AppendLine(Markup.Escape(issue.ResolveNote));
        body.AppendLine().Append(Markup.Escape(issue.Detail));

        var panel = new Panel(body.ToString().TrimEnd('\n'))
        {
            Header = new PanelHeader(Markup.Escape(issue.Id)),
        };
        console.Write(panel);
    }
}

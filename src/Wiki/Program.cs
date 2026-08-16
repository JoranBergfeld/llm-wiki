using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using Wiki.Cli;
using Wiki.Cli.Commands;
using Wiki.Core;
using Wiki.Json;

namespace Wiki;

public static class App
{
    // UTF-8, no BOM. One instance reused for stdin and stdout so there is
    // exactly one answer to "what encoding does this CLI speak".
    // encoderShouldEmitUTF8Identifier:false keeps a BOM off the front of the
    // --json envelope (strict JSON parsers reject it). throwOnInvalidBytes is
    // deliberately false on the READ side: stdin is the agent's hot path and
    // a decode exception there would surface as a bare io-error with no
    // useful code; malformed input instead lands as U+FFFD and gets rejected
    // by the frontmatter/scalar guards that already exist. (`source add`
    // takes the strict path - see SourceService.ReadTextFile.)
    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Real process entrypoint delegates here.
    //
    // The console's encoding is NOT inherited (issue #6). On Windows the
    // standard streams default to the console code page, so a body piped in
    // on --stdin containing an accented name, a curly quote or CJK would be
    // decoded wrong and stored corrupted - the write succeeds and the damage
    // is silent and permanent - while the --json envelope on stdout would be
    // emitted in a non-UTF-8 encoding, which is a contract violation for
    // every consumer of it. The CLI is the only thing that writes to the
    // vault, so it is the thing that has to guarantee the encoding rather
    // than inherit whatever shell happened to invoke it.
    //
    // This lives in Main(string[]) and never in Main(string[], TextWriter,
    // TextReader): the in-proc test overload supplies its own streams and
    // must stay untouched, which is also what makes the process entrypoint
    // thin enough to reason about.
    public static int Main(string[] args)
    {
        // Best-effort, display only: makes a Windows console RENDER the UTF-8
        // bytes below correctly instead of as mojibake. The setter throws
        // when there is no attached console (redirected/service contexts), and
        // that failure is irrelevant to correctness - the explicit streams
        // below already carry the right bytes - so it is swallowed.
        try { System.Console.OutputEncoding = Utf8NoBom; } catch { }

        var (stdout, stdin) = OpenStandardStreams();
        using var _out = stdout;
        using var _in = stdin;
        try
        {
            return Main(args, stdout, stdin);
        }
        finally
        {
            stdout.Flush();
        }
    }

    // Correctness half of the encoding fix: explicit UTF-8 readers/writers
    // over the RAW standard streams, rather than Console.In/Console.Out and
    // whatever code page they inherited. Console.InputEncoding is deliberately
    // never set - its setter is the one that throws hardest on redirected
    // stdin, and wrapping the raw stream here makes it unnecessary.
    //
    // Public so a test can assert the encoding contract (UTF-8, no preamble)
    // directly; the in-proc Main overload takes its own streams and so can
    // never exercise this path.
    public static (StreamWriter Out, StreamReader In) OpenStandardStreams()
        => (new StreamWriter(System.Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = false },
            new StreamReader(System.Console.OpenStandardInput(), Utf8NoBom));

    public static int Main(string[] args, TextWriter stdout, TextReader stdin)
    {
        var (root, jsonOption) = BuildRootCommand(stdout, stdin);
        var parseResult = root.Parse(args);

        // Whether failures render as JSON or as Spectre (amendment P). Taken
        // from the parsed result when the parse succeeded; on a parse error
        // there is nothing trustworthy to read the option off, so fall back
        // to scanning argv directly - a user who typed --json still gets JSON
        // back even when what they typed was otherwise garbage.
        var json = parseResult.Errors.Count > 0 ? WantsJson(args) : parseResult.GetValue(jsonOption);

        // Risk #1: System.CommandLine's built-in parse-error handling (unknown
        // command, missing required argument, ...) prints usage text straight
        // to InvocationConfiguration.Output and returns 1 - it never produces
        // our JSON envelope. Intercept before invoking so garbage input still
        // yields {"ok":false,"errors":[{"code":"unknown-command",...}]}.
        if (parseResult.Errors.Count > 0)
        {
            var message = string.Join("; ", parseResult.Errors.Select(e => e.Message));
            var badCommand = args.Length > 0 ? args[0] : "";
            OutputMode.EmitFailure(stdout, json, new WikiError
            {
                Code = "unknown-command",
                Message = string.IsNullOrEmpty(message) ? $"unknown command '{badCommand}'" : message,
            });
            return 1;
        }

        try
        {
            // EnableDefaultExceptionHandler=false: let ValidationException /
            // StateConflictException / anything else propagate out of the
            // command action so the catch clauses below map them onto our
            // envelope + exit code contract, instead of System.CommandLine's
            // default handler swallowing them and printing its own text.
            var invocationConfig = new InvocationConfiguration
            {
                Output = stdout,
                Error = stdout,
                EnableDefaultExceptionHandler = false,
            };
            return parseResult.Invoke(invocationConfig);
        }
        catch (ValidationException vex)
        {
            OutputMode.EmitFailure(stdout, json, new WikiError { Code = vex.Code, Message = vex.Message, Path = vex.Path });
            return 1;
        }
        catch (StateConflictException scex)
        {
            OutputMode.EmitFailure(stdout, json, new WikiError { Code = scex.Code, Message = scex.Message, Path = scex.Path });
            return 3;
        }
        catch (Exception ex)
        {
            OutputMode.EmitFailure(stdout, json, new WikiError { Code = "io-error", Message = ex.Message });
            return 2;
        }
    }

    // argv fallback for the parse-error path. Matches the bare flag and the
    // `--json=true` / `--json:true` forms System.CommandLine also accepts.
    private static bool WantsJson(string[] args)
    {
        foreach (var a in args)
        {
            if (a == "--json" || a.StartsWith("--json=", StringComparison.Ordinal) || a.StartsWith("--json:", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Builds the full command tree fresh on every call (cheap; System.CommandLine
    // objects aren't meant to be reused across parses with different in/out
    // streams). --vault and --json are Recursive so every subcommand - however
    // deep - inherits them without redeclaring anything.
    //
    // Extension point for later tasks: add more `root.Add(XCommand.Build(...))`
    // calls here as command groups land. Nothing else in this method needs to
    // change.
    private static (RootCommand Root, Option<bool> JsonOption) BuildRootCommand(TextWriter stdout, TextReader stdin)
    {
        var vaultOption = new Option<string?>("--vault")
        {
            Recursive = true,
            Description = "Path to the vault root (overrides WIKI_VAULT and directory auto-detection)",
        };
        var jsonOption = new Option<bool>("--json")
        {
            Recursive = true,
            Description = "Emit a machine-readable JSON envelope instead of human-readable output",
        };

        var root = new RootCommand("wiki - a CLI-maintained, LLM-legible knowledge base")
        {
            vaultOption,
            jsonOption,
        };

        root.Add(InitCommand.Build(vaultOption, jsonOption, stdout, stdin));
        root.Add(PageCommand.Build(vaultOption, jsonOption, stdout, stdin));
        root.Add(ReindexCommand.Build(vaultOption, jsonOption, stdout, stdin));
        root.Add(SourceCommand.Build(vaultOption, jsonOption, stdout, stdin));
        root.Add(IngestCommand.Build(vaultOption, jsonOption, stdout, stdin));
        root.Add(SearchCommand.Build(vaultOption, jsonOption, stdout, stdin));
        root.Add(IndexCommand.Build(vaultOption, jsonOption, stdout, stdin));
        root.Add(IssuesCommand.Build(vaultOption, jsonOption, stdout, stdin));
        root.Add(LintCommand.Build(vaultOption, jsonOption, stdout, stdin));
        root.Add(ReviewCommand.Build(vaultOption, jsonOption, stdout, stdin));
        root.Add(SchemaCommand.Build(vaultOption, jsonOption, stdout, stdin));
        root.Add(CategoryCommand.Build(vaultOption, jsonOption, stdout, stdin));
        root.Add(EvalCommand.Build(vaultOption, jsonOption, stdout, stdin));
        root.Add(AuditCommand.Build(vaultOption, jsonOption, stdout, stdin));
        root.Add(LinksCommand.Build(vaultOption, jsonOption, stdout, stdin));

        return (root, jsonOption);
    }
}

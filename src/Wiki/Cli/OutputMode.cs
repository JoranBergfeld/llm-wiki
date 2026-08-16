using Spectre.Console;

namespace Wiki.Cli;

public static class OutputMode
{
    // Emit the envelope as JSON. This is the `--json` agent interface; it is
    // byte-for-byte unchanged by amendment P.
    public static void Emit(System.IO.TextWriter w, Wiki.Json.Envelope env)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(env,
            Wiki.Json.WikiJsonContext.Default.Envelope);
        w.WriteLine(json);
    }

    // The one failure-output path (amendment P). §8 says human-facing output
    // uses Spectre rendering, and the success paths did - but every failure
    // emitted the raw envelope regardless of the flag, so an interactive user
    // got `{"v":1,"ok":false,...}` with '-escaped quotes staring back at them.
    //
    // Presentation only: the envelope, the error codes and the exit codes are
    // identical in both modes, and `--json` output is exactly what it was.
    public static void EmitFailure(System.IO.TextWriter w, bool json, Wiki.Json.WikiError error)
    {
        if (json)
        {
            Emit(w, Wiki.Json.Envelope.Failure(error));
            return;
        }

        // Local console instance writing straight to `w` - never the
        // process-global AnsiConsole.Console, so this stays safe under
        // parallel in-proc test execution (same rule CommandContext follows).
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(w) });
        console.MarkupLine($"[red]ERROR[/] [grey]{Markup.Escape(error.Code)}[/] {Markup.Escape(error.Message)}");
        if (!string.IsNullOrEmpty(error.Path))
            console.MarkupLine($"  [grey]path:[/] {Markup.Escape(error.Path)}");
    }
}

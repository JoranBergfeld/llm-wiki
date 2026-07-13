using System.IO;
using Spectre.Console;
using Wiki.Core;
using Wiki.Json;

namespace Wiki.Cli;

// Per-invocation bag handed to every command handler: the parsed global
// options plus the in/out streams App.Main was called with (so commands stay
// in-proc testable - never touch System.Console directly).
public sealed class CommandContext
{
    public string? VaultFlag { get; init; }
    public bool Json { get; init; }
    public required TextWriter Out { get; init; }
    public required TextReader In { get; init; }

    public Vault ResolveVault()
        => Vault.Resolve(VaultFlag, System.Environment.GetEnvironmentVariable, System.IO.Directory.GetCurrentDirectory());

    public VaultConfig LoadConfig() => VaultConfig.Load(ResolveVault().ConfigPath);

    // Success envelope: JSON when --json, otherwise a concise Spectre line.
    // A local console instance writes straight to Out - never touches the
    // process-global AnsiConsole.Console, so this stays safe under parallel
    // in-proc test execution.
    public void EmitOk(object? data)
    {
        if (Json)
        {
            OutputMode.Emit(Out, Envelope.Success(data));
            return;
        }

        var message = data is IHumanRenderable r ? r.HumanSummary() : "ok";
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Out),
        });
        console.MarkupLine($"[green]OK[/] {Markup.Escape(message)}");
    }

    public void EmitError(string code, string message, string? path = null)
    {
        OutputMode.Emit(Out, Envelope.Failure(new WikiError { Code = code, Message = message, Path = path }));
    }
}

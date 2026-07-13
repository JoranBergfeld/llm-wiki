using Wiki.Json;
using Wiki.Cli;
namespace Wiki;

public static class App
{
    // Real process entrypoint delegates here.
    public static int Main(string[] args) => Main(args, System.Console.Out, System.Console.In);

    public static int Main(string[] args, System.IO.TextWriter stdout, System.IO.TextReader stdin)
    {
        try
        {
            // Task 8+ replaces this stub with the System.CommandLine tree.
            var cmd = args.Length > 0 ? args[0] : "";
            var env = Envelope.Failure(new WikiError { Code = "unknown-command",
                Message = $"unknown command '{cmd}'" });
            OutputMode.Emit(stdout, env);
            return 1;
        }
        catch (System.Exception ex)
        {
            OutputMode.Emit(stdout, Envelope.Failure(new WikiError { Code = "io-error", Message = ex.Message }));
            return 2;
        }
    }
}

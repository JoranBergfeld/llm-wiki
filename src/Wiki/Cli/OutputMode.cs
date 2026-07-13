namespace Wiki.Cli;
public static class OutputMode
{
    // Emit the envelope as JSON. Human rendering (Spectre) is added per-command later.
    public static void Emit(System.IO.TextWriter w, Wiki.Json.Envelope env)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(env,
            Wiki.Json.WikiJsonContext.Default.Envelope);
        w.WriteLine(json);
    }
}

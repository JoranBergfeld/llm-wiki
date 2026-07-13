using System.Text.Json;
namespace Wiki.Tests.Support;

public sealed class TempVault : System.IDisposable
{
    public string Path { get; }
    public TempVault()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wiki-test-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path);
    }
    public CliResult Run(params string[] args)
    {
        var full = new System.Collections.Generic.List<string>(args);
        if (!full.Contains("--vault")) { full.Add("--vault"); full.Add(Path); }
        var sw = new System.IO.StringWriter();
        var exit = Wiki.App.Main(full.ToArray(), sw, new System.IO.StringReader(""));
        var line = sw.ToString().Trim().Split('\n')[^1];
        var env = JsonSerializer.Deserialize(line, Wiki.Json.WikiJsonContext.Default.Envelope)!;
        return new CliResult(exit, env, sw.ToString());
    }
    public CliResult RunStdin(string stdin, params string[] args)
    {
        var full = new System.Collections.Generic.List<string>(args);
        if (!full.Contains("--vault")) { full.Add("--vault"); full.Add(Path); }
        var sw = new System.IO.StringWriter();
        var exit = Wiki.App.Main(full.ToArray(), sw, new System.IO.StringReader(stdin));
        var line = sw.ToString().Trim().Split('\n')[^1];
        var env = JsonSerializer.Deserialize(line, Wiki.Json.WikiJsonContext.Default.Envelope)!;
        return new CliResult(exit, env, sw.ToString());
    }
    public void Dispose() { try { System.IO.Directory.Delete(Path, true); } catch { } }
}

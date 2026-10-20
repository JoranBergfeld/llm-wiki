using System.Diagnostics;
using System.Reflection;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.E2E;

public class VersionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task VersionOnly_ReturnsPlainText_WithoutReadingOrMutatingVault(
        bool invalidConfig, bool invalidEnvironment)
    {
        using var directory = new TempVault();
        if (invalidConfig)
        {
            File.WriteAllText(Path.Combine(directory.Path, "wiki.yaml"), "not valid vault configuration");
            File.WriteAllText(Path.Combine(directory.Path, "AGENTS.md"), "No policy approval has been granted.");
            Directory.CreateDirectory(Path.Combine(directory.Path, ".wiki"));
            File.WriteAllText(Path.Combine(directory.Path, ".wiki", "ledger.json"), "not valid ledger JSON");
        }
        Directory.CreateDirectory(Path.Combine(directory.Path, "inbox"));
        File.WriteAllText(Path.Combine(directory.Path, "inbox", "queue.json"), "[\"pending-source.md\"]");
        var before = Snapshot(directory.Path);
        var directoriesBefore = Directory.GetDirectories(directory.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = directory.Path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        start.ArgumentList.Add(typeof(App).Assembly.Location);
        start.ArgumentList.Add("--version");
        start.Environment.Remove("WIKI_VAULT");
        if (invalidEnvironment)
            start.Environment["WIKI_VAULT"] = Path.Combine(directory.Path, "missing-vault");

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr);
            var version = (await stdout).Trim();
            Assert.False(string.IsNullOrWhiteSpace(version));
            Assert.Equal(
                typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
                version);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        var after = Snapshot(directory.Path);
        Assert.Equal(directoriesBefore, Directory.GetDirectories(directory.Path, "*", SearchOption.AllDirectories).Order());
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var path in before.Keys)
            Assert.Equal(before[path], after[path]);
    }

    private static Dictionary<string, byte[]> Snapshot(string root)
        => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes);
}

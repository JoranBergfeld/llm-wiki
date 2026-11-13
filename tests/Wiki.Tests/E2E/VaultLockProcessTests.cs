using System.Diagnostics;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.E2E;

// Issue #46 across real processes: the lock is an OS file handle, so it
// excludes a separate `wiki` process just as it does an in-process command,
// and dies with the process that held it.
public class VaultLockProcessTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static ProcessStartInfo Wiki(TempVault tv, params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = tv.Path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        start.ArgumentList.Add(typeof(App).Assembly.Location);
        foreach (var a in args)
            start.ArgumentList.Add(a);
        start.ArgumentList.Add("--vault");
        start.ArgumentList.Add(tv.Path);
        start.Environment.Remove("WIKI_VAULT");
        start.Environment.Remove("WIKI_LOCK_TIMEOUT");
        return start;
    }

    // Polls until some other holder has the vault: a reader that may not wait
    // at all is refused with vault-busy.
    private static void WaitUntilVaultIsHeld(TempVault tv)
    {
        var clock = Stopwatch.StartNew();
        while (tv.Run("page", "list", "--json", "--lock-timeout", "0").ExitCode != 5)
        {
            Assert.True(clock.Elapsed < Wait, "the other process never took the vault lock");
            Thread.Sleep(50);
        }
    }

    [Fact]
    public async Task KillingTheProcessHoldingTheLock_ReleasesIt()
    {
        using var tv = new TempVault();
        tv.Run("init", tv.Path, "--name", "t", "--json");

        // A writer whose stdin never closes: it takes the exclusive lock and
        // then waits for its body forever.
        using var holder = Process.Start(Wiki(tv,
            "page", "upsert", "--type", "entity", "--title", "Stuck", "--summary", "s", "--stdin", "--json"))!;
        try
        {
            WaitUntilVaultIsHeld(tv);
            holder.Kill(entireProcessTree: true);
            await holder.WaitForExitAsync(new CancellationTokenSource(Wait).Token);

            var writer = tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "After",
                "--summary", "s", "--stdin", "--json", "--lock-timeout", "10");
            Assert.Equal(0, writer.ExitCode);
            Assert.False(File.Exists(Path.Combine(tv.Path, "wiki", "entities", "stuck.md")));
        }
        finally
        {
            if (!holder.HasExited)
                holder.Kill(entireProcessTree: true);
        }
    }

    [Theory]
    [InlineData("0.2", 5, "vault-busy")]
    [InlineData("soon", 1, "invalid-lock-timeout")]
    public async Task LockTimeout_IsConfigurableThroughTheEnvironment(string value, int exit, string code)
    {
        using var tv = new TempVault();
        tv.Run("init", tv.Path, "--name", "t", "--json");
        var stdin = new HeldStdin("Body.");
        var held = Task.Run(() => tv.RunWith(stdin, TextWriter.Null,
            "page", "upsert", "--type", "entity", "--title", "Held", "--summary", "s", "--stdin", "--json"));
        Assert.True(stdin.WaitUntilEntered(Wait));
        try
        {
            var start = Wiki(tv, "page", "list", "--json");
            start.Environment["WIKI_LOCK_TIMEOUT"] = value;
            using var reader = Process.Start(start)!;
            reader.StandardInput.Close();
            var stdout = reader.StandardOutput.ReadToEndAsync();
            // Well under the 10 s default: the variable, not the default, decided.
            await reader.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(8)).Token);

            Assert.Equal(exit, reader.ExitCode);
            Assert.Contains($"\"code\":\"{code}\"", await stdout);
        }
        finally
        {
            stdin.Release();
        }
        Assert.Equal(0, (await held).ExitCode);
    }
}

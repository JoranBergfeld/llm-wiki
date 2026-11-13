using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #46: one vault-wide lock. Readers share it, a writer holds it
// exclusively for one CLI step, and a command that cannot get it within the
// lock timeout fails with the retryable `vault-busy` error (exit 5).
//
// Commands are parked mid-step through the real CLI seam: a writer by a stdin
// that blocks (`page upsert --stdin` reads its body inside the step), a reader
// by a stdout that blocks (a read command writes its result inside the step).
public class VaultLockTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static TempVault NewVault()
    {
        var tv = new TempVault();
        tv.Run("init", tv.Path, "--name", "t", "--json");
        return tv;
    }

    // Starts `page upsert --stdin` for `title` and returns once it is inside
    // its step, i.e. holding the lock.
    private static (Task<CliResult> Run, HeldStdin Stdin) StartHeldWriter(TempVault tv, string title)
    {
        var stdin = new HeldStdin("Body.");
        var run = Task.Run(() => tv.RunWith(stdin, TextWriter.Null,
            "page", "upsert", "--type", "entity", "--title", title, "--summary", "s", "--stdin", "--json"));
        Assert.True(stdin.WaitUntilEntered(Wait), "writer never entered its step");
        return (run, stdin);
    }

    // Every file and its bytes. A file a held command keeps open exclusively
    // (on Windows, the lock itself) cannot be read and is recorded as present
    // but unreadable; its key still takes part in the comparison.
    private static Dictionary<string, byte[]> Snapshot(string root)
        => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(root, p), p =>
            {
                try { return File.ReadAllBytes(p); }
                catch (IOException) { return []; }
            });

    private static CliResult Upsert(TempVault tv, string title, params string[] extra)
        => tv.RunStdin("Body.", ["page", "upsert", "--type", "entity", "--title", title, "--summary", "s", "--stdin", "--json", .. extra]);

    // Starts `page list` and returns once it is inside its step, writing its
    // result, i.e. holding the lock as a reader.
    private static (Task<CliResult> Run, HeldStdout Stdout) StartHeldReader(TempVault tv)
    {
        var stdout = new HeldStdout();
        var run = Task.Run(() => tv.RunWith(TextReader.Null, stdout, "page", "list", "--json"));
        Assert.True(stdout.WaitUntilEntered(Wait), "reader never entered its step");
        return (run, stdout);
    }

    [Fact]
    public async Task TwoReaders_BothProceed()
    {
        using var tv = NewVault();
        var (held, stdout) = StartHeldReader(tv);
        try
        {
            // Zero timeout: it gets the lock at once or fails. It must get it.
            var second = tv.Run("page", "list", "--json", "--lock-timeout", "0");
            Assert.Equal(0, second.ExitCode);
            Assert.True(second.Envelope.Ok);
        }
        finally
        {
            stdout.Release();
        }
        Assert.Equal(0, (await held).ExitCode);
    }

    [Fact]
    public async Task Reader_BlocksWriter()
    {
        using var tv = NewVault();
        var (held, stdout) = StartHeldReader(tv);
        try
        {
            var writer = Upsert(tv, "Blocked", "--lock-timeout", "0.2");
            Assert.Equal(5, writer.ExitCode);
            Assert.Equal("vault-busy", Assert.Single(writer.Envelope.Errors).Code);
        }
        finally
        {
            stdout.Release();
        }
        Assert.Equal(0, (await held).ExitCode);
        Assert.False(File.Exists(Path.Combine(tv.Path, "wiki", "entities", "blocked.md")));
    }

    [Fact]
    public async Task Writer_BlocksReadersAndWriters_UntilItsStepEnds()
    {
        using var tv = NewVault();
        var (held, stdin) = StartHeldWriter(tv, "First");
        Task<CliResult> waitingReader, waitingWriter;
        try
        {
            var impatient = tv.Run("page", "list", "--json", "--lock-timeout", "0.2");
            Assert.Equal(5, impatient.ExitCode);
            Assert.Equal("vault-busy", Assert.Single(impatient.Envelope.Errors).Code);

            waitingReader = Task.Run(() => tv.Run("page", "list", "--json", "--lock-timeout", "20"));
            waitingWriter = Task.Run(() => Upsert(tv, "Second", "--lock-timeout", "20"));
            await Task.Delay(300);
            Assert.False(waitingReader.IsCompleted, "reader ran while the writer held the vault");
            Assert.False(waitingWriter.IsCompleted, "writer ran while another writer held the vault");
        }
        finally
        {
            stdin.Release();
        }
        Assert.Equal(0, (await held).ExitCode);

        // Both waiters run once the writer's step is over; the reader sees
        // the whole first write.
        var reader = await waitingReader;
        Assert.Equal(0, reader.ExitCode);
        Assert.Contains("First", reader.Stdout);
        Assert.Equal(0, (await waitingWriter).ExitCode);
    }

    [Fact]
    public async Task Timeout_NeverCancelsAWriteAlreadyUnderWay()
    {
        using var tv = NewVault();
        var stdin = new HeldStdin("Body.");
        var run = Task.Run(() => tv.RunWith(stdin, TextWriter.Null,
            "page", "upsert", "--type", "entity", "--title", "Slow", "--summary", "s", "--stdin", "--json",
            "--lock-timeout", "0.1"));
        Assert.True(stdin.WaitUntilEntered(Wait));
        // Hold the step well past its own lock timeout.
        await Task.Delay(500);
        stdin.Release();

        Assert.Equal(0, (await run).ExitCode);
        Assert.True(File.Exists(Path.Combine(tv.Path, "wiki", "entities", "slow.md")));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("soon")]
    [InlineData("NaN")]
    public void InvalidLockTimeout_IsRejected(string value)
    {
        using var tv = NewVault();
        var r = tv.Run("page", "list", "--json", "--lock-timeout", value);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("invalid-lock-timeout", Assert.Single(r.Envelope.Errors).Code);
    }

    [Fact]
    public async Task SecondWriter_PastTimeout_GetsVaultBusy_AndVaultIsUntouched()
    {
        using var tv = NewVault();
        var (held, stdin) = StartHeldWriter(tv, "First");
        try
        {
            var before = Snapshot(tv.Path);
            var second = Upsert(tv, "Second", "--lock-timeout", "0.2");

            Assert.Equal(5, second.ExitCode);
            Assert.False(second.Envelope.Ok);
            Assert.Equal("vault-busy", Assert.Single(second.Envelope.Errors).Code);
            var after = Snapshot(tv.Path);
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var path in before.Keys)
                Assert.Equal(before[path], after[path]);
        }
        finally
        {
            stdin.Release();
        }
        Assert.Equal(0, (await held).ExitCode);
    }
}

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Wiki.Core;

namespace Wiki.Cli;

// The vault-wide lock (issue #46, design in #36). Readers share it, a writer
// holds it exclusively, and it covers exactly one CLI step: it is the
// OUTERMOST boundary step, so everything a later step does (recovery, the
// audit append and rotation) runs while it is held.
//
// Mechanism: an open handle on `.wiki/lock`, so the OS owns the lock and a
// crashed or killed process releases it - there is no flag or PID file that
// can get stuck. The file's content is never read or written.
//   - Windows: share modes. A reader opens for Read with FileShare.Read, a
//     writer for Write with FileShare.None; Windows refuses any open the
//     existing handles' share modes do not allow.
//   - Linux/macOS: .NET emulates FileShare with flock(2) on the descriptor:
//     FileShare.None takes LOCK_EX, a read-only open with any other share
//     takes LOCK_SH, both non-blocking. flock belongs to the open file
//     description, not the process, so two opens inside ONE process conflict
//     as well - which is what the in-process MCP server and the parallel
//     in-process test suite need. (fcntl range locks, which
//     FileStream.Lock uses on Unix, are per process and would not.)
//     Setting DOTNET_SYSTEM_IO_DISABLEFILELOCKING turns the emulation, and
//     with it this lock, off.
//
// Waiting: acquisition polls until the lock timeout (`--lock-timeout`, then
// WIKI_LOCK_TIMEOUT, default 10 s), then fails with the retryable vault-busy
// error before the command has read or written anything. The timeout only
// bounds the wait; once the lock is held the step runs to completion.
//
// The lock lives in `.wiki/`, which nothing deletes (reindex rebuilds the
// stores in it but never removes the directory). Guarantee: local storage
// only; network filesystems are outside it (spec, issue #41).
public static class VaultLock
{
    public const string TimeoutOption = "--lock-timeout";
    public const string TimeoutEnv = "WIKI_LOCK_TIMEOUT";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static int Step(CommandInvocation invocation, Func<int> next)
    {
        Vault vault;
        try
        {
            vault = invocation.Context.ResolveVault();
        }
        catch (ValidationException)
        {
            // No vault to lock. Either `init`, which runs before the vault
            // exists (its target holds no wiki.yaml yet), or a command that
            // is about to fail with this same no-vault error itself.
            return next();
        }

        var timeout = ResolveTimeout(invocation.ParseResult.GetValue<string?>(TimeoutOption),
            Environment.GetEnvironmentVariable(TimeoutEnv));
        using var handle = Acquire(vault, exclusive: invocation.Kind == CommandKind.Mutate, timeout);
        return next();
    }

    // The exclusive lock outside the mutation pipeline, with the invocation's
    // timeout: a reader that must put an interrupted write's before-images
    // back takes it for that moment only (WriteRecovery.BeforeRead, #47).
    internal static IDisposable AcquireExclusive(CommandInvocation invocation, Vault vault)
        => Acquire(vault, exclusive: true, ResolveTimeout(invocation.ParseResult.GetValue<string?>(TimeoutOption),
            Environment.GetEnvironmentVariable(TimeoutEnv)));

    // Seconds, invariant culture, fractions allowed, 0 meaning "do not wait".
    internal static TimeSpan ResolveTimeout(string? flag, string? env)
    {
        if (!string.IsNullOrEmpty(flag))
            return Parse(flag, TimeoutOption);
        if (!string.IsNullOrEmpty(env))
            return Parse(env, TimeoutEnv);
        return DefaultTimeout;
    }

    private static TimeSpan Parse(string value, string source)
    {
        if (double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) && seconds <= int.MaxValue / 1000.0)
            return TimeSpan.FromMilliseconds(seconds * 1000.0);
        throw new ValidationException("invalid-lock-timeout",
            $"{source} must be a non-negative number of seconds, got '{value}'");
    }

    private static FileStream Acquire(Vault vault, bool exclusive, TimeSpan timeout)
    {
        // init creates both; this covers vaults made before the lock existed
        // and a `.wiki/` deleted by hand.
        Directory.CreateDirectory(vault.StateDir);
        var path = vault.LockPath;
        var clock = Stopwatch.StartNew();
        var delay = 5;
        while (true)
        {
            try
            {
                return exclusive
                    ? new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None)
                    : new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
            }
            catch (IOException ex) when (IsContention(ex))
            {
                var left = timeout - clock.Elapsed;
                if (left <= TimeSpan.Zero)
                {
                    throw new VaultBusyException(
                        $"the vault is busy: another wiki command held its lock for longer than the {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s lock timeout; nothing was changed, retry the command (raise the wait with {TimeoutOption} or {TimeoutEnv})",
                        vault.Root);
                }
                Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(delay, Math.Max(1, left.TotalMilliseconds))));
                delay = Math.Min(delay * 2, 100);
            }
        }
    }

    // Only "someone else holds it" is worth waiting out; any other IO failure
    // (permissions, a missing drive, EIO, a full disk creating the lock file)
    // propagates at once as an IO error rather than turning into vault-busy.
    // Windows reports ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION
    // (33) in the low word of HResult. On Unix .NET throws a refused flock as
    // a plain IOException whose HResult is the raw errno, EWOULDBLOCK: 11 on
    // Linux, 35 on macOS and the BSDs. Matching the type alone would also
    // match every other errno .NET maps to a plain IOException.
    internal static bool IsContention(IOException ex)
    {
        if (OperatingSystem.IsWindows())
        {
            var code = ex.HResult & 0xFFFF;
            return code is 32 or 33;
        }
        if (ex.GetType() != typeof(IOException))
            return false;
        var wouldBlock = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() ? 11 : 35;
        return ex.HResult == wouldBlock;
    }
}

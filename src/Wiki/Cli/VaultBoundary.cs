using System;
using System.CommandLine;

namespace Wiki.Cli;

// What a boundary step knows about the command it is wrapping. Name is the
// catalog path ("page upsert"); Context carries the parsed --vault/--json and
// the streams, so a step can resolve the vault itself.
public sealed class CommandInvocation
{
    public required string Name { get; init; }
    public required CommandKind Kind { get; init; }
    public required CommandContext Context { get; init; }
    public required ParseResult ParseResult { get; init; }
}

// One layer of a boundary. Code before `next()` runs before the command,
// code after it runs once the command returned its exit code, and a
// try/finally around it runs either way. Exceptions from the command
// (ValidationException, StateConflictException, IO) propagate through every
// step unchanged and are mapped onto exit codes by App.Main, as before.
public delegate int BoundaryStep(CommandInvocation invocation, Func<int> next);

// The mutation boundary and the read boundary (issue #42). Every command runs
// inside exactly one of them, chosen by its CommandCatalog classification;
// CommandBinding.Bind is the only caller, so no command can bypass it.
//
// The cross-cutting concerns plug in as steps here, in one place, instead of
// command by command. Order of MutationSteps, outermost first:
//   1. vault lock (#46):      VaultLock.Step, outermost in BOTH pipelines:
//                             shared for reads, exclusive for mutations. It
//                             wraps the audit step, so the audit tail read,
//                             the command and the audit append (and
//                             rotation, #45) happen under one hold.
//   2. write recovery (#47):  replays or rolls back an interrupted write
//                             before next(); needs the lock held, so it goes
//                             between the lock and the audit step.
//   3. audit log (#44):       AuditStep - opens the log before next() (fail
//                             closed), appends one chained line after it.
// Note `init` is Mutate but runs before a vault exists: a step that needs
// one must tolerate ctx.ResolveVault() failing for that command.
//
// The arrays are static and immutable on purpose: tests run App.Main
// in-process and in parallel, so a step must hold no per-process state and
// derive everything (vault path, clock) from the invocation.
public static class VaultBoundary
{
    // Ordered outermost first. The vault lock stays outermost: every later
    // step (write recovery #47, the audit append and rotation #44/#45) must
    // run while it is held, so new steps go AFTER it. Recovery (#47) slots
    // in between the lock and the audit step.
    private static readonly BoundaryStep[] MutationSteps = [VaultLock.Step, Audit.AuditStep.Run];
    private static readonly BoundaryStep[] ReadSteps = [VaultLock.Step];

    public static int Run(CommandInvocation invocation, Func<int> command)
    {
        var steps = invocation.Kind == CommandKind.Mutate ? MutationSteps : ReadSteps;
        return RunFrom(steps, 0, invocation, command);
    }

    private static int RunFrom(BoundaryStep[] steps, int index, CommandInvocation invocation, Func<int> command)
        => index == steps.Length
            ? command()
            : steps[index](invocation, () => RunFrom(steps, index + 1, invocation, command));
}

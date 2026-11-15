using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Wiki.Cli;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #42: every command declares whether it reads or mutates the vault, in
// one place (CommandCatalog), so the vault lock, the audit log and write
// recovery attach to the mutation boundary once instead of command by command.
// These tests walk the real command tree App.Main parses against, so a new
// command that forgets to classify itself fails here rather than silently
// slipping past the boundary.
public class CommandClassificationTests
{
    [Fact]
    public void EveryInvocableCommand_IsClassified()
    {
        var unclassified = InvocableCommandPaths()
            .Where(path => CommandCatalog.Classify(path) is null)
            .ToArray();

        Assert.True(unclassified.Length == 0,
            "commands missing from CommandCatalog (classify each as Read or Mutate): " + string.Join(", ", unclassified));
    }

    [Fact]
    public void Catalog_HasNoEntriesForCommandsThatDoNotExist()
    {
        var tree = InvocableCommandPaths().ToHashSet();
        var stale = CommandCatalog.Entries.Keys.Where(path => !tree.Contains(path)).ToArray();

        Assert.True(stale.Length == 0, "CommandCatalog entries with no matching command: " + string.Join(", ", stale));
    }

    [Theory]
    [InlineData("init", CommandKind.Mutate)]
    [InlineData("page upsert", CommandKind.Mutate)]
    [InlineData("lint", CommandKind.Mutate)]
    [InlineData("links check", CommandKind.Mutate)]
    [InlineData("page show", CommandKind.Read)]
    [InlineData("search", CommandKind.Read)]
    [InlineData("audit next", CommandKind.Read)]
    public void KnownCommands_AreClassifiedByWhetherTheyCanWrite(string path, CommandKind expected)
        => Assert.Equal(expected, CommandCatalog.Classify(path));

    // Every command that has an action - the ones a user can actually run.
    // Group commands (`page`, `source`, ...) have none: invoking one bare is a
    // parse error that never reaches a boundary.
    private static IEnumerable<string> InvocableCommandPaths()
    {
        var root = App.CommandTree();
        foreach (var path in Walk(root, prefix: null))
            yield return path;
    }

    private static IEnumerable<string> Walk(Command command, string? prefix)
    {
        foreach (var sub in command.Subcommands)
        {
            var path = prefix is null ? sub.Name : prefix + " " + sub.Name;
            if (sub.Action is not null)
                yield return path;
            foreach (var deeper in Walk(sub, path))
                yield return deeper;
        }
    }
}

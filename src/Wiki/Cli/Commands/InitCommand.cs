using System.CommandLine;
using System.Collections.Generic;
using System.IO;
using Wiki.Cli;
using Wiki.Core;

namespace Wiki.Cli.Commands;

// `wiki init <path> [--name X] [--review-gate]`: scaffolds a brand-new vault
// at <path> - wiki.yaml, AGENTS.md, the raw/ and wiki/ directory layout, and
// empty index.md/log.md. Refuses (state conflict, exit 3) if wiki.yaml
// already exists there; never overwrites an existing vault.
public static class InitCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "Directory to scaffold the new vault into",
        };
        var nameOption = new Option<string?>("--name")
        {
            Description = "Vault display name (defaults to the target directory's name)",
        };
        var reviewGateOption = new Option<bool>("--review-gate")
        {
            Description = "New pages land as pending-review until approved",
        };

        var command = new Command("init", "Scaffold a new vault: wiki.yaml, AGENTS.md, directory layout, empty index/log")
        {
            pathArgument,
            nameOption,
            reviewGateOption,
        };

        command.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var path = parseResult.GetRequiredValue(pathArgument);
            var name = parseResult.GetValue(nameOption) ?? DeriveName(path);
            var reviewGate = parseResult.GetValue(reviewGateOption);

            var result = Run(path, name, reviewGate);
            ctx.EmitOk(result);
            return 0;
        }));
        return command;
    }

    private static string DeriveName(string path)
    {
        var full = Path.GetFullPath(path.TrimEnd('/', '\\'));
        var name = Path.GetFileName(full);
        return string.IsNullOrEmpty(name) ? full : name;
    }

    internal static InitResult Run(string path, string name, bool reviewGate)
    {
        var vault = Vault.Resolve(path, _ => null, Directory.GetCurrentDirectory());

        if (File.Exists(vault.ConfigPath))
        {
            throw new StateConflictException(
                "vault-exists",
                $"'{vault.ConfigPath}' already exists; refusing to re-init an existing vault",
                vault.ConfigPath);
        }

        var created = new List<string>();

        Directory.CreateDirectory(vault.RawDir);
        created.Add(vault.RawDir);
        var assetsDir = Path.Combine(vault.RawDir, "assets");
        Directory.CreateDirectory(assetsDir);
        created.Add(assetsDir);

        Directory.CreateDirectory(vault.PageDir(PageType.Summary));
        created.Add(vault.PageDir(PageType.Summary));
        Directory.CreateDirectory(vault.PageDir(PageType.Entity));
        created.Add(vault.PageDir(PageType.Entity));
        Directory.CreateDirectory(vault.PageDir(PageType.Concept));
        created.Add(vault.PageDir(PageType.Concept));

        Directory.CreateDirectory(vault.StateDir);
        created.Add(vault.StateDir);

        AtomicFile.Write(vault.IndexPath, "");
        created.Add(vault.IndexPath);
        AtomicFile.Write(vault.LogPath, "");
        created.Add(vault.LogPath);

        var yaml = Wiki.Templates.Templates.WikiYaml
            .Replace("{{name}}", name)
            .Replace("{{review_gate}}", reviewGate ? "true" : "false");
        AtomicFile.Write(vault.ConfigPath, yaml);
        created.Add(vault.ConfigPath);

        AtomicFile.Write(vault.AgentsPath, Wiki.Templates.Templates.AgentsMd);
        created.Add(vault.AgentsPath);

        return new InitResult(vault.Root, name, reviewGate, created.ToArray());
    }
}

public sealed record InitResult(string Vault, string Name, bool ReviewGate, string[] Created) : IHumanRenderable
{
    public string HumanSummary() => $"Initialized vault \"{Name}\" at {Vault} ({Created.Length} paths created)";
}

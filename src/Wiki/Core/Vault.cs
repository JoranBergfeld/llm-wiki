namespace Wiki.Core;

// The path model for a vault: a directory rooted at wiki.yaml, holding raw
// sources, generated wiki pages, and CLI state. Resolve() finds the root;
// everything else composes off Root.
public sealed class Vault
{
    public string Root { get; }
    public string RawDir { get; }
    public string WikiDir { get; }
    public string StateDir { get; }
    public string ConfigPath { get; }
    public string IndexPath { get; }
    public string LogPath { get; }
    public string AgentsPath { get; }

    private Vault(string root)
    {
        Root = root;
        RawDir = System.IO.Path.Combine(root, "raw");
        WikiDir = System.IO.Path.Combine(root, "wiki");
        StateDir = System.IO.Path.Combine(root, ".wiki");
        ConfigPath = System.IO.Path.Combine(root, "wiki.yaml");
        IndexPath = System.IO.Path.Combine(WikiDir, "index.md");
        LogPath = System.IO.Path.Combine(WikiDir, "log.md");
        AgentsPath = System.IO.Path.Combine(root, "AGENTS.md");
    }

    public string PageDir(PageType t) => t switch
    {
        PageType.Summary => System.IO.Path.Combine(WikiDir, "summaries"),
        PageType.Entity => System.IO.Path.Combine(WikiDir, "entities"),
        PageType.Concept => System.IO.Path.Combine(WikiDir, "concepts"),
        PageType.Overview => WikiDir,
        _ => throw new ValidationException("invalid-page-type", $"unknown PageType '{t}'"),
    };

    public static Vault Resolve(string? flag, System.Func<string, string?> env, string cwd)
    {
        if (!string.IsNullOrEmpty(flag))
        {
            return new Vault(System.IO.Path.GetFullPath(flag));
        }

        var fromEnv = env("WIKI_VAULT");
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return new Vault(System.IO.Path.GetFullPath(fromEnv));
        }

        var dir = new System.IO.DirectoryInfo(System.IO.Path.GetFullPath(cwd));
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "wiki.yaml");
            if (System.IO.File.Exists(candidate))
            {
                return new Vault(dir.FullName);
            }
            dir = dir.Parent;
        }

        throw new ValidationException("no-vault", "no wiki.yaml found walking up from cwd; pass --vault or set WIKI_VAULT");
    }
}

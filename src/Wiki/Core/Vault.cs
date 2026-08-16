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

    // The human-owned golden-question file (issue #11 part A). Sits next to
    // wiki.yaml at the vault root, and is optional - `wiki eval` is the only
    // thing that reads it, and its absence is that command's problem, not
    // every command's.
    public string EvalPath { get; }

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
        EvalPath = System.IO.Path.Combine(root, "eval.yaml");
    }

    // The one way to turn an absolute path inside this vault into the
    // forward-slashed, vault-relative form everything on disk stores
    // (idmap.json keys, `path` fields in the JSON envelope, search hits).
    //
    // Centralised (issue #5) because the `.Replace('\\', '/')` that makes it
    // portable was previously repeated at six call sites: one of them
    // forgetting it would emit a Windows-only path into a vault that is
    // explicitly meant to be shared across machines through git, and nothing
    // would fail loudly - the idmap would just stop resolving on the other OS.
    // A separator convention is a property of the vault format, so it belongs
    // on the type that models the vault.
    public string RelativePath(string fullPath)
        => System.IO.Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    public string PageDir(PageType t) => t switch
    {
        PageType.Summary => System.IO.Path.Combine(WikiDir, "summaries"),
        PageType.Entity => System.IO.Path.Combine(WikiDir, "entities"),
        PageType.Concept => System.IO.Path.Combine(WikiDir, "concepts"),
        PageType.Overview => WikiDir,
        _ => throw new ValidationException("invalid-page-type", $"unknown PageType '{t}'"),
    };

    // Spec §8 resolution order: `--vault` flag → `WIKI_VAULT` → walk up from
    // CWD for a `wiki.yaml`.
    //
    // Amendment M: all three branches must land on a directory that actually
    // holds a `wiki.yaml`. The walk-up branch does so by construction, but
    // the two explicit branches used to accept any string, so
    // `wiki page list --vault ./typo` returned `{"ok":true,"data":[]}` with
    // exit 0 - "this path is not a vault" was indistinguishable from "this
    // vault is empty" for the agent that is supposed to trust `ok`. Every
    // page/source enumerator treats a missing directory as zero results, so
    // nothing downstream would ever have caught it.
    //
    // Resolve() is the USER-INPUT entry point and always validates. Callers
    // that already know their root - `wiki init`, which is the command that
    // creates the wiki.yaml in the first place, and unit tests that just want
    // the path model - use At() below instead of asking for a validation
    // opt-out here.
    public static Vault Resolve(string? flag, System.Func<string, string?> env, string cwd)
    {
        if (!string.IsNullOrEmpty(flag))
        {
            return Explicit(flag, "--vault");
        }

        var fromEnv = env("WIKI_VAULT");
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return Explicit(fromEnv, "WIKI_VAULT");
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

    // Names the source (`--vault` / `WIKI_VAULT`) in the error so a caller
    // who set the env var months ago doesn't hunt for a flag they never
    // passed, and reports the resolved absolute path so a relative input
    // that landed somewhere unexpected is obvious.
    // The path model rooted at `root`, with no check that a vault lives
    // there. For callers holding a root they already trust: `wiki init`
    // (which is about to write the wiki.yaml) and tests exercising the state
    // stores. User input goes through Resolve(), never here.
    public static Vault At(string root) => new(System.IO.Path.GetFullPath(root));

    private static Vault Explicit(string path, string source)
    {
        var root = System.IO.Path.GetFullPath(path);
        if (!System.IO.File.Exists(System.IO.Path.Combine(root, "wiki.yaml")))
        {
            throw new ValidationException("no-vault",
                $"{source} points at '{root}', which has no wiki.yaml; create one with 'wiki init {path}' or point at an existing vault",
                root);
        }
        return new Vault(root);
    }
}

using System.IO;
using System.Reflection;

namespace Wiki.Templates;

// Loads the vault-scaffold templates from the embedded .txt resources in this
// folder. GetManifestResourceStream is a plain name lookup against the
// assembly's resource table - no reflection over types/members, so it's
// AOT-safe (unlike attribute/reflection-based (de)serialization, which this
// codebase avoids everywhere).
public static class Templates
{
    private const string WikiYamlResourceName = "Wiki.Templates.wiki-yaml.txt";
    private const string AgentsMdResourceName = "Wiki.Templates.agents-md.txt";

    public static string WikiYaml => ReadResource(WikiYamlResourceName);
    public static string AgentsMd => ReadResource(AgentsMdResourceName);

    private static string ReadResource(string name)
    {
        var asm = typeof(Templates).Assembly;
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new System.InvalidOperationException($"embedded template resource '{name}' not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

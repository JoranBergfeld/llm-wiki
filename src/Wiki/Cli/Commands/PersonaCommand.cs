using System.CommandLine;
using System.IO;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki persona ...` (spec §8, amendment AB): the owner's persona in
// PERSONA.md. A command group from the start because distill, propose and
// the onboarding interview (`persona interview guide|record`) join it in
// later tickets - each as one more Build* method added in Build() below.
public static class PersonaCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var persona = new Command("persona", "Read the owner's persona (PERSONA.md: values, principles, tone of voice, boundaries)");
        persona.Add(BuildRead(vaultOption, jsonOption, stdout, stdin));
        return persona;
    }

    private static Command BuildRead(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var read = new Command("read", "Print the persona; a vault without PERSONA.md has an empty persona");

        read.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var vault = ctx.ResolveVault();
            var view = new PersonaService().Read(vault);
            ctx.EmitOk(view);
            return 0;
        }));

        return read;
    }
}

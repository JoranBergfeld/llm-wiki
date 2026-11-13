using System.Collections.Generic;
using System.Globalization;

namespace Wiki.Mcp;

// One CLI invocation an intent tool runs, built ONLY from typed tool
// arguments (issue #37, argument injection): option values go in as
// `--opt=value` so a value can never be read as another option, positional
// values go after `--` so a value like `--vault=x` or `@file` stays a plain
// value. The adapter adds the bound `--vault=<abs>` and `--json` itself; a
// tool cannot set either. There is deliberately no way to append raw argv.
public sealed class CliStep
{
    private readonly List<string> _options = [];
    private readonly List<string> _positionals = [];

    // `key` names this step's data in a successful tool result
    // (data.<key>); `command` is the command path, e.g. ("page", "show").
    public CliStep(string key, params string[] command)
    {
        Key = key;
        Command = command;
    }

    public string Key { get; }
    public string[] Command { get; }
    public string CommandPath => string.Join(" ", Command);
    public string? StdinText { get; private set; }

    // True when this step's data is appended to an array under its key
    // instead of being the key's value. See Collect().
    public bool Collected { get; private set; }

    // Omitted when null, so the CLI's own default applies.
    public CliStep Option(string name, string? value)
    {
        if (value is not null)
            _options.Add(name + "=" + value);
        return this;
    }

    public CliStep Option(string name, int? value)
        => Option(name, value?.ToString(CultureInfo.InvariantCulture));

    // A boolean switch: present when true, absent otherwise.
    public CliStep Flag(string name, bool on)
    {
        if (on)
            _options.Add(name);
        return this;
    }

    public CliStep Positional(string value)
    {
        _positionals.Add(value);
        return this;
    }

    // Body text handed to the command on stdin (the tool also sets the
    // command's own `--stdin` flag), e.g. `page upsert` for write_page.
    public CliStep Stdin(string text)
    {
        StdinText = text;
        return this;
    }

    // For a tool that runs the same command once per item (repair_vault's
    // `page set-status` per page): every step with this key appends its data
    // to the array data.<key> in the order the steps ran, so the key is an
    // array whether one item or many were given.
    public CliStep Collect()
    {
        Collected = true;
        return this;
    }

    internal string[] ToArgv(string vaultRoot)
    {
        var argv = new List<string>(Command) { "--vault=" + vaultRoot, "--json" };
        argv.AddRange(_options);
        argv.Add("--");
        argv.AddRange(_positionals);
        return [.. argv];
    }
}

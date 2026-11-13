using System;
using Wiki.Core;

namespace Wiki.Cli;

// Persona auto-accept (issue #51, decided in #32): an environment variable,
// off by default, that accepts a persona changeset at `wiki persona propose`
// time instead of queueing it for `wiki review`. It covers every section,
// Boundaries included. It only skips the human review: the changeset is
// validated exactly as before (lint fails closed), and the accept is audited
// with `decidedVia=auto-accept`.
//
// Read per command from the process environment, so a manual `wiki` run and
// the MCP server (which runs the same command in-process) behave the same.
//
// Values: 1/true/yes/on turn it on; unset, empty, 0/false/no/off leave it
// off (case-insensitive). Anything else is refused before the command reads
// or writes anything, so a typo never silently decides the persona's review
// mode either way.
public static class PersonaAutoAccept
{
    public const string Env = "WIKI_PERSONA_AUTO_ACCEPT";
    public const string DecidedVia = "auto-accept";

    public static bool Enabled() => Parse(Environment.GetEnvironmentVariable(Env));

    internal static bool Parse(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0)
            return false;
        switch (v.ToLowerInvariant())
        {
            case "1": case "true": case "yes": case "on":
                return true;
            case "0": case "false": case "no": case "off":
                return false;
        }
        throw new ValidationException("invalid-persona-auto-accept",
            $"{Env} must be one of 1, true, yes, on (enabled) or 0, false, no, off (disabled), got '{value}'");
    }
}

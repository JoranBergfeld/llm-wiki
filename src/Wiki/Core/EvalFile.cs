using System.Collections.Generic;
using System.Globalization;

namespace Wiki.Core;

// One golden question: what someone asks, and the page slugs that MUST be
// among the candidates the router surfaces for it.
//
// "Must include", never "exactly this set" - expectations that named an exact
// set would break every time a page was added, which would make the eval file
// a maintenance tax instead of a regression detector.
public sealed record EvalQuestion(string Ask, string[] Expect);

// `eval.yaml` at the vault root, next to `wiki.yaml` (issue #11 part A).
//
// Human-owned, and the CLI never writes it. Two reasons, both load-bearing:
// the "CLI is the only writer to the vault" invariant means the agent has no
// route to author it anyway, and letting the model write its own exam defeats
// the point of having one. If agent-proposed questions are ever wanted they
// route through a propose/approve channel like everything else, not through a
// direct write.
//
// Shape - deliberately the same two-line list-item form `wiki.yaml`'s
// `categories:` block already uses, parsed by the same hand-rolled approach
// (no YamlDotNet, no reflection, AOT-clean):
//
//   version: 1
//   questions:
//     - ask: "What did Contoso ship in Q2?"
//       expect: contoso, contoso-platform-review-summary
//
// `expect` is a comma-separated slug list rather than a nested YAML sequence
// because a nested sequence would need a third level of hand-written
// indentation handling for no expressive gain.
public sealed class EvalFile
{
    public required int Version { get; init; }
    public required List<EvalQuestion> Questions { get; init; }

    public static EvalFile Load(string path)
    {
        string text;
        try
        {
            text = System.IO.File.ReadAllText(path);
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
        {
            throw new ValidationException("eval-file", $"cannot read eval file '{path}': {ex.Message}", path);
        }

        var rawLines = text.Replace("\r\n", "\n").Split('\n');
        var lines = new string[rawLines.Length];
        for (var i = 0; i < rawLines.Length; i++)
            lines[i] = VaultConfig.StripInlineComment(rawLines[i]);

        string? version = null;
        var questions = new List<EvalQuestion>();

        var li = 0;
        while (li < lines.Length)
        {
            var line = lines[li];
            var trimmed = line.Trim();

            if (trimmed.Length == 0) { li++; continue; }

            if (TryTopLevelScalar(line, "version", out var v)) { version = v; li++; continue; }

            if (trimmed == "questions:")
            {
                li++;
                while (li < lines.Length && IsListItemStart(lines[li]))
                {
                    var itemLine = lines[li].Trim();
                    var afterDash = itemLine[1..].TrimStart();
                    if (!TryScalarLine(afterDash, "ask", out var ask))
                        throw new ValidationException("eval-file", $"malformed question entry: '{itemLine}'", path);
                    li++;

                    if (li >= lines.Length || !TryScalarLine(lines[li].Trim(), "expect", out var expectRaw))
                        throw new ValidationException("eval-file", $"question '{ask}' is missing 'expect'", path);
                    li++;

                    var expect = expectRaw.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
                    if (expect.Length == 0)
                        throw new ValidationException("eval-file", $"question '{ask}' has an empty 'expect' list", path);
                    if (string.IsNullOrWhiteSpace(ask))
                        throw new ValidationException("eval-file", "a question has an empty 'ask'", path);

                    questions.Add(new EvalQuestion(ask, expect));
                }
                continue;
            }

            throw new ValidationException("eval-file", $"unrecognized eval line: '{line}'", path);
        }

        if (version is null)
            throw new ValidationException("eval-file", "missing required key 'version'", path);
        if (!int.TryParse(version, NumberStyles.Integer, CultureInfo.InvariantCulture, out var versionNum))
            throw new ValidationException("eval-file", $"'version' must be an integer, got '{version}'", path);
        if (versionNum != 1)
            throw new ValidationException("eval-file", $"unsupported eval file version '{versionNum}'; only version 1 is supported", path);
        if (questions.Count == 0)
            throw new ValidationException("eval-file", "eval file declares no questions", path);

        return new EvalFile { Version = versionNum, Questions = questions };
    }

    private static bool TryTopLevelScalar(string line, string key, out string value)
    {
        value = "";
        if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            return false;
        return TryScalarLine(line.Trim(), key, out value);
    }

    private static bool TryScalarLine(string trimmedLine, string key, out string value)
    {
        value = "";
        var prefix = key + ":";
        if (!trimmedLine.StartsWith(prefix, System.StringComparison.Ordinal))
            return false;
        value = Unquote(trimmedLine[prefix.Length..].Trim());
        return true;
    }

    private static bool IsListItemStart(string line)
    {
        var trimmed = line.TrimStart();
        return line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && trimmed.StartsWith("- ", System.StringComparison.Ordinal);
    }

    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}

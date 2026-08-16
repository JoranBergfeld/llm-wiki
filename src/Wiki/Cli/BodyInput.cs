using System.IO;
using System.Text;
using Wiki.Core;

namespace Wiki.Cli;

// Where a command's body text comes from: stdin (`--stdin`) or a file
// (`--body-file <path>`). Spec §8: "Page bodies always arrive via stdin
// (--stdin) or --body-file <path>; never as shell arguments".
//
// Why --body-file exists at all (issue #7): `echo "…" | wiki page upsert
// --stdin` is a SHELL contract wearing a CLI contract's clothes. The binary
// ships for win-x64 alongside the Unix targets, but in PowerShell `echo` is
// Write-Output and pipes objects rather than bytes, multi-line bodies need
// here-strings whose terminator must sit at column 0, and `$`, backtick and
// `"` are all live characters. Writing a 300-line markdown body through a
// shell pipe is the most fragile operation in the workflow and also the one
// the agent performs most often. --body-file lets the agent write a temp file
// with its own (already cross-platform, already correctly-encoded)
// file-writing tool and pass a path instead.
//
// This does not weaken "the CLI is the only thing that writes to the vault":
// a temp file outside the vault is INPUT, exactly like the file `wiki source
// add` already accepts.
//
// Both sources decode as UTF-8 explicitly rather than inheriting a platform
// default (issue #6). Stdin's encoding is fixed at the process entrypoint
// (App.Main); the file read is pinned here.
public static class BodyInput
{
    // BOM-stripping (detectEncodingFromByteOrderMarks) is on by design: an
    // agent's file-writing tool may well emit a UTF-8 BOM, and a stray U+FEFF
    // at the top of a page body would end up inside the stored markdown.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // `stdinFlag` is the parsed `--stdin` value. It is not required for stdin
    // to be read - resolution falls back to stdin whenever --body-file is
    // absent, which is what the shipped examples have always relied on - but
    // passing BOTH flags is a contradiction about where the body lives, so it
    // is rejected rather than silently resolved in favour of one of them.
    public static string Resolve(CommandContext ctx, string? bodyFile, bool stdinFlag)
    {
        var hasBodyFile = !string.IsNullOrEmpty(bodyFile);

        if (hasBodyFile && stdinFlag)
            throw new ValidationException(
                "body-source-conflict",
                "--stdin and --body-file are mutually exclusive; pass exactly one");

        if (!hasBodyFile)
            return ctx.In.ReadToEnd();

        if (!File.Exists(bodyFile))
            throw new ValidationException("body-file-not-found", $"--body-file '{bodyFile}' does not exist", bodyFile);

        return File.ReadAllText(bodyFile!, Utf8);
    }
}

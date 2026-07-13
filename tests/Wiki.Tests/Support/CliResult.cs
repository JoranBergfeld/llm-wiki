namespace Wiki.Tests.Support;

public sealed record CliResult(int ExitCode, Wiki.Json.Envelope Envelope, string Stdout);

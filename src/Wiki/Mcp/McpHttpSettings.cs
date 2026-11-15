using System;
using System.Collections.Generic;
using System.Linq;
using Wiki.Core;

namespace Wiki.Mcp;

// Server config of `wiki mcp --http` (issue #61; decisions #29, #34), fixed at
// startup like the vault binding. Each setting is a flag, else an env var; the
// flag wins (the --vault / WIKI_VAULT precedence).
//
// - Listen URL: --listen / WIKI_MCP_LISTEN, default http://127.0.0.1:8765.
//   Plain http only: the slim builder has no TLS, which belongs on a reverse
//   proxy. Port 0 picks a free port (the startup line names it).
// - Bearer token: --token / WIKI_MCP_TOKEN. Optional and single (#34): set, a
//   request without exactly `Authorization: Bearer <token>` is refused 401.
//   Prefer the env var: argv is visible to other local users.
// - Allowed hosts: --allowed-hosts / WIKI_MCP_ALLOWED_HOSTS, comma-separated
//   or repeated (the flag replaces the env var, like --exclude-tools).
//   Default loopback only: localhost, 127.0.0.1, [::1]. `*` allows any Host
//   header, which gives up DNS-rebinding protection: an explicit opt-in.
public sealed class McpHttpSettings
{
    public const string HttpOption = "--http";
    public const string ListenOption = "--listen";
    public const string ListenEnv = "WIKI_MCP_LISTEN";
    public const string TokenOption = "--token";
    public const string TokenEnv = "WIKI_MCP_TOKEN";
    public const string AllowedHostsOption = "--allowed-hosts";
    public const string AllowedHostsEnv = "WIKI_MCP_ALLOWED_HOSTS";
    public const string DefaultListen = "http://127.0.0.1:8765";
    public const string EndpointPath = "/mcp";

    public static readonly IReadOnlyList<string> DefaultAllowedHosts = ["localhost", "127.0.0.1", "[::1]"];

    // Room for the JSON-RPC envelope and the other arguments around an
    // upload's base64 text.
    private const long EnvelopeAllowance = 64 * 1024;

    private McpHttpSettings(string listen, string? token, IReadOnlyList<string> allowedHosts, long maxBodyBytes)
    {
        Listen = listen;
        Token = token;
        AllowedHosts = allowedHosts;
        MaxBodyBytes = maxBodyBytes;
    }

    public string Listen { get; }
    public string? Token { get; }
    public IReadOnlyList<string> AllowedHosts { get; }
    public bool AnyHost => AllowedHosts.Contains("*");

    // The largest request body accepted before `upload-too-large` (413): the
    // ingest upload limit (IngestInbox.MaxUploadBytes, decoded bytes) doubled -
    // base64 is 4/3 of it and a JSON encoder may escape some of its
    // characters - plus 64 KiB for everything else in the request. An upload
    // just over the limit whose request still fits is refused by
    // ingest_source itself (a tool result, exitCode 1), exactly as over stdio.
    public long MaxBodyBytes { get; }

    public static McpHttpSettings Resolve(string? listenFlag, string? tokenFlag, string[]? allowedHostsFlag, IngestInbox inbox)
    {
        var listen = listenFlag ?? NullIfEmpty(Environment.GetEnvironmentVariable(ListenEnv)) ?? DefaultListen;
        if (!Uri.TryCreate(listen, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp
            || uri.AbsolutePath != "/" || uri.Query != "" || uri.UserInfo != "")
            throw new ValidationException("invalid-listen-url",
                $"{ListenOption}: '{listen}' must be a plain http://<host>:<port> URL (no path; TLS belongs on a reverse proxy)");

        var token = tokenFlag ?? Environment.GetEnvironmentVariable(TokenEnv);
        if (string.IsNullOrEmpty(token))
            token = null;
        else if (token.Any(char.IsWhiteSpace))
            throw new ValidationException("invalid-token", $"{TokenOption}: a bearer token cannot contain whitespace");

        var hosts = (allowedHostsFlag is null ? Split(Environment.GetEnvironmentVariable(AllowedHostsEnv)) : allowedHostsFlag.SelectMany(Split))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (hosts.Count == 0)
            hosts = [.. DefaultAllowedHosts];

        return new McpHttpSettings(listen, token, hosts, inbox.MaxUploadBytes * 2 + EnvelopeAllowance);
    }

    // Host names compare case-insensitively without the port; an IPv6
    // literal is kept bracketed, the way a Host header carries it.
    public bool IsAllowed(string host)
        => AnyHost || AllowedHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Split(string? value)
        => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Normalize(string host)
        => host.Contains(':') && !host.StartsWith('[') ? "[" + host + "]" : host;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

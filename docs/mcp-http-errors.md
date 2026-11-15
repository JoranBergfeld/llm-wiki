# MCP HTTP transport: error catalogue

`wiki mcp --http` (spec Appendix U, amendment AS) rejects some requests itself,
before they reach MCP. Each rejection is an [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457)
`application/problem+json` body, never a JSON-RPC error:

```json
HTTP/1.1 401 Unauthorized
Content-Type: application/problem+json
WWW-Authenticate: Bearer realm="wiki"

{
  "type": "https://github.com/JoranBergfeld/llm-wiki/blob/main/docs/mcp-http-errors.md#unauthorized",
  "title": "Missing or wrong bearer token",
  "status": 401,
  "detail": "this server requires 'Authorization: Bearer <token>' with its configured token",
  "instance": "/mcp",
  "code": "unauthorized"
}
```

- `type` and `code` are the machine handle. `type` resolves to the code's
  heading below. Branch on `code`.
- `title` and `detail` are for humans and may change.
- `instance` is the request path.
- Codes that carry data a client needs get their own extension members,
  listed with the code.

The checks run in the order below, and the first failure answers. So a request
for a foreign host learns nothing about whether the server wants a token.

Everything past these checks is MCP. Unknown tools and invalid arguments are
JSON-RPC errors, and a failing CLI step is a tool result with `isError`. See
spec amendment AF.

## `host-not-allowed`

**400 Bad Request.** The request's `Host` header names a host this server does
not answer for. This is protection against DNS rebinding: a web page cannot
reach a loopback server through a name the page controls.

| Extension | Type | Meaning |
|---|---|---|
| `host` | string | The host name that was refused, without the port. |

Fix: call the server by an allowed name, or allow the name with
`--allowed-hosts <name,...>` / `WIKI_MCP_ALLOWED_HOSTS`. The default is
loopback only: `localhost`, `127.0.0.1`, `[::1]`. `*` allows any host and gives
up the rebinding protection.

## `unauthorized`

**401 Unauthorized**, with `WWW-Authenticate: Bearer realm="wiki"`. The server
was started with a bearer token (`--token` / `WIKI_MCP_TOKEN`), and the request
has no `Authorization: Bearer <token>` header or the token is wrong. The
comparison is constant-time. Without a configured token, this code never
occurs and every request is accepted.

No extension members.

Fix: send the configured token. Tokens are not scoped, and there is no
per-client identity. The audit log records only that a token was used
(`via=http+token`).

## `upload-too-large`

**413 Content Too Large.** The request body is larger than the server
accepts. The limit is derived from the ingest upload limit
(`--max-upload-bytes` / `WIKI_MCP_MAX_UPLOAD_BYTES`, default 10 485 760 decoded
bytes): twice that, plus 64 KiB for the rest of the JSON-RPC request. Base64 is
4/3 of the decoded size, and a JSON encoder may escape some of its characters.
A body without a `Content-Length` is read up to the limit and refused there.

| Extension | Type | Meaning |
|---|---|---|
| `maxBodyBytes` | integer | The largest request body this server accepts, in bytes. |

An upload over `--max-upload-bytes` whose request still fits under
`maxBodyBytes` reaches `ingest_source`. The tool refuses it there with a tool
result (`upload-too-large`, `exitCode: 1`), the same result as over stdio.

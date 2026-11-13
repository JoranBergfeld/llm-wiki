using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Wiki.Mcp;

// The HTTP transport's own checks, in front of the MCP endpoint (issue #61,
// decision #30 "Errors"). Each rejection is an RFC 9457
// `application/problem+json` body with a `code` extension, never a JSON-RPC
// error: the request never reached MCP. In order:
//
// 1. Host header not allowed       -> 400 host-not-allowed (DNS rebinding)
// 2. Bearer token missing or wrong -> 401 unauthorized (only with a token set)
// 3. Request body over the limit   -> 413 upload-too-large
//
// The catalogue (docs/mcp-http-errors.md) documents each code; `type` is a
// URI to that code's anchor there.
public static class HttpGate
{
    public const string CatalogueUri = "https://github.com/JoranBergfeld/llm-wiki/blob/main/docs/mcp-http-errors.md";

    public static void UseHttpGate(this IApplicationBuilder app, McpHttpSettings settings)
    {
        var expected = settings.Token is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(settings.Token));
        app.Use(async (context, next) =>
        {
            var host = context.Request.Host.Host;
            if (!settings.IsAllowed(host))
            {
                await Problem(context, 400, "host-not-allowed", "Host not allowed",
                    $"this server does not answer for host '{host}'; allow it with {McpHttpSettings.AllowedHostsOption}",
                    w => w.WriteString("host", host));
                return;
            }

            if (expected is not null && !Authorized(context.Request, expected))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer realm=\"wiki\"";
                await Problem(context, 401, "unauthorized", "Missing or wrong bearer token",
                    "this server requires 'Authorization: Bearer <token>' with its configured token", null);
                return;
            }

            var limit = settings.MaxBodyBytes;
            if (context.Request.ContentLength > limit)
            {
                await TooLarge(context, limit);
                return;
            }

            // A body without a Content-Length (chunked) is read here, up to
            // the limit, before the endpoint sees it: the SDK turns a failed
            // body read into its own 400, so the limit cannot be enforced
            // from inside its read. Memory is bounded by the same limit.
            if (context.Request.ContentLength is null && HasBody(context.Request))
            {
                var buffered = await Buffer(context.Request.Body, limit, context.RequestAborted);
                if (buffered is null)
                {
                    await TooLarge(context, limit);
                    return;
                }
                context.Request.Body = buffered;
                context.Request.ContentLength = buffered.Length;
            }

            await next(context);
        });
    }

    private static bool HasBody(HttpRequest request)
        => request.Headers.TransferEncoding.Count > 0 || !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsDelete(request.Method));

    // The body, or null once it passes the limit (Kestrel's own limit, set
    // to the same value, surfaces as a 413 BadHttpRequestException).
    private static async Task<MemoryStream?> Buffer(Stream body, long limit, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        try
        {
            int n;
            while ((n = await body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + n > limit)
                    return null;
                buffer.Write(chunk, 0, n);
            }
        }
        catch (BadHttpRequestException bad) when (bad.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }
        buffer.Position = 0;
        return buffer;
    }

    // Constant time: both sides are hashed first, so neither the length nor
    // a common prefix of a wrong token shows in the comparison's timing.
    private static bool Authorized(HttpRequest request, byte[] expected)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(header[scheme.Length..].Trim()));
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    private static Task TooLarge(HttpContext context, long limit)
        => Problem(context, 413, "upload-too-large", "Request body too large",
            $"the request body is larger than this server's limit of {limit} bytes (twice {IngestInbox.MaxUploadOption}, plus 64 KiB)",
            w => w.WriteNumber("maxBodyBytes", limit));

    // Written by hand with Utf8JsonWriter: no reflection-based serializer
    // (AOT), and the extension members stay in one place per code.
    private static async Task Problem(HttpContext context, int status, string code, string title, string detail, Action<Utf8JsonWriter>? extensions)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", CatalogueUri + "#" + code);
            w.WriteString("title", title);
            w.WriteNumber("status", status);
            w.WriteString("detail", detail);
            w.WriteString("instance", context.Request.Path.Value ?? "/");
            w.WriteString("code", code);
            extensions?.Invoke(w);
            w.WriteEndObject();
        }
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        context.Response.ContentLength = buffer.Length;
        await context.Response.Body.WriteAsync(buffer.ToArray());
    }
}

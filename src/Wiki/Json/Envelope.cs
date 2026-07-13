using System.Text.Json.Serialization;

namespace Wiki.Json;

public sealed class WikiError
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Path { get; set; }
}

public sealed class Envelope
{
    public const int Version = 1;
    public int V { get; set; } = Version;
    public bool Ok { get; set; }

    // Envelope shape guarantees all four top-level keys on every response.
    // Override the global WhenWritingNull so "data" is emitted as null on failures.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? Data { get; set; }
    public WikiError[] Errors { get; set; } = System.Array.Empty<WikiError>();

    public static Envelope Success(object? data) => new() { Ok = true, Data = data };
    public static Envelope Failure(params WikiError[] errors) => new() { Ok = false, Errors = errors };
}

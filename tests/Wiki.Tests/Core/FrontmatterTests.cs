using Xunit;
using Wiki.Core;

namespace Wiki.Tests.Core;

public class FrontmatterTests
{
    const string Valid = """
        ---
        id: 01J9ZKM3E8W1R2X3Y4Z5A6B7C8
        type: entity
        title: "Contoso"
        status: active
        created: 2026-07-13
        updated: 2026-07-13
        summary: "The vendor under review"
        sources: [01J9ZKM1E8W1R2X3Y4Z5A6B7C8]
        tags: []
        ---
        Body about [[contoso-deal]].
        """;

    [Fact]
    public void Parse_RoundTrips_BytePreservingBody()
    {
        var doc = PageDoc.Parse(Valid);
        Assert.Equal(PageType.Entity, doc.Front.Type);
        Assert.Single(doc.Front.Sources);
        Assert.Contains("[[contoso-deal]]", doc.Body);
        Assert.Equal(Valid.Trim(), doc.Serialize().Trim()); // stable serialization
    }

    [Fact]
    public void Parse_UnknownKey_Throws()
    {
        var bad = Valid.Replace("tags: []", "tags: []\nbogus: 1");
        var ex = Assert.Throws<ValidationException>(() => PageDoc.Parse(bad));
        Assert.Equal("frontmatter-schema", ex.Code);
    }

    [Fact]
    public void Parse_MissingRequiredSummary_Throws()
    {
        var bad = Valid.Replace("summary: \"The vendor under review\"\n", "");
        Assert.Throws<ValidationException>(() => PageDoc.Parse(bad));
    }

    [Fact]
    public void Parse_MultipleTags_And_UnquotedBareScalars()
    {
        var text = Valid.Replace("tags: []", "tags: [alpha, beta]");
        var doc = PageDoc.Parse(text);
        Assert.Equal(new[] { "alpha", "beta" }, doc.Front.Tags);
        Assert.Equal("01J9ZKM3E8W1R2X3Y4Z5A6B7C8", doc.Front.Id);
        Assert.Equal("2026-07-13", doc.Front.Created);
    }

    [Fact]
    public void Parse_RoundTrips_BothSourcesAndTagsNonEmpty()
    {
        var text = Valid
            .Replace("sources: [01J9ZKM1E8W1R2X3Y4Z5A6B7C8]",
                     "sources: [01J9ZKM1E8W1R2X3Y4Z5A6B7C8, 01J9ZKM2E8W1R2X3Y4Z5A6B7C8]")
            .Replace("tags: []", "tags: [alpha, beta]");
        var doc = PageDoc.Parse(text);
        Assert.Equal(2, doc.Front.Sources.Length);
        Assert.Equal(new[] { "alpha", "beta" }, doc.Front.Tags);
        Assert.Equal(text.Trim(), doc.Serialize().Trim());
    }

    [Fact]
    public void Parse_MalformedLineNoColon_Throws()
    {
        var bad = Valid.Replace("tags: []", "tags: []\nnot-a-kv-pair");
        var ex = Assert.Throws<ValidationException>(() => PageDoc.Parse(bad));
        Assert.Equal("frontmatter-schema", ex.Code);
    }

    [Fact]
    public void Parse_BodyWithLiteralDelimiterLine_RoundTrips()
    {
        var body = "Intro line.\n---\nAfter a literal delimiter [[contoso-deal]].";
        var text = Valid.Replace("Body about [[contoso-deal]].", body);
        var doc = PageDoc.Parse(text);
        // The first '---' after the opening block is the real closing delimiter, so the
        // literal '---' inside the body must survive verbatim.
        Assert.Equal(body, doc.Body);
        Assert.Equal(PageType.Entity, doc.Front.Type);
        Assert.Equal(text.Trim(), doc.Serialize().Trim());
    }

    [Fact]
    public void Parse_InvalidId_Throws()
    {
        var bad = Valid.Replace("id: 01J9ZKM3E8W1R2X3Y4Z5A6B7C8", "id: not-a-ulid");
        var ex = Assert.Throws<ValidationException>(() => PageDoc.Parse(bad));
        Assert.Equal("frontmatter-schema", ex.Code);
    }

    [Fact]
    public void Parse_MissingClosingDelimiter_Throws()
    {
        var bad = "---\nid: 01J9ZKM3E8W1R2X3Y4Z5A6B7C8\n";
        Assert.Throws<ValidationException>(() => PageDoc.Parse(bad));
    }

    [Fact]
    public void Parse_MissingOpeningDelimiter_Throws()
    {
        Assert.Throws<ValidationException>(() => PageDoc.Parse("id: 01J9ZKM3E8W1R2X3Y4Z5A6B7C8\n---\n"));
    }

    // --- Source frontmatter ---

    const string ValidSource = """
        ---
        id: 01J9ZKM1E8W1R2X3Y4Z5A6B7C8
        type: source
        title: "Vendor security questionnaire"
        category: document
        added: 2026-07-13
        sha256: deadbeefcafef00d
        origin: https://example.com/contoso/vsq.pdf
        status: active
        ---
        Raw notes about the source.
        """;

    [Fact]
    public void SourceFrontmatter_Parse_RoundTrips()
    {
        var (scalars, lists, body) = Frontmatter.ReadBlock(ValidSource);
        var front = SourceFrontmatter.FromRaw(scalars, lists);

        Assert.Equal("01J9ZKM1E8W1R2X3Y4Z5A6B7C8", front.Id);
        Assert.Equal("Vendor security questionnaire", front.Title);
        Assert.Equal("document", front.Category);
        Assert.Equal(SourceStatus.Active, front.Status);
        Assert.Equal("Raw notes about the source.", body);

        var serialized = front.ToBlock() + "\n" + body;
        Assert.Equal(ValidSource.Trim(), serialized.Trim());
    }

    [Fact]
    public void SourceFrontmatter_WrongType_Throws()
    {
        var bad = ValidSource.Replace("type: source", "type: page");
        var (scalars, lists, _) = Frontmatter.ReadBlock(bad);
        var ex = Assert.Throws<ValidationException>(() => SourceFrontmatter.FromRaw(scalars, lists));
        Assert.Equal("frontmatter-schema", ex.Code);
    }

    [Fact]
    public void SourceFrontmatter_UnknownKey_Throws()
    {
        var bad = ValidSource.Replace("status: active", "status: active\nbogus: 1");
        var (scalars, lists, _) = Frontmatter.ReadBlock(bad);
        var ex = Assert.Throws<ValidationException>(() => SourceFrontmatter.FromRaw(scalars, lists));
        Assert.Equal("frontmatter-schema", ex.Code);
    }
}

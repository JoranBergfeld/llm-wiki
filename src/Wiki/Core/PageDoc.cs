namespace Wiki.Core;

// A wiki page file: closed-schema frontmatter plus its markdown body. Parse
// and Serialize are exact inverses for well-formed input — same fixed key
// order and quoting on the way out, body preserved verbatim.
public sealed record PageDoc(PageFrontmatter Front, string Body)
{
    public static PageDoc Parse(string fileText)
    {
        var (scalars, lists, body) = Frontmatter.ReadBlock(fileText);
        var front = PageFrontmatter.FromRaw(scalars, lists);
        return new PageDoc(front, body);
    }

    public string Serialize() => Front.ToBlock() + "\n" + Body;
}

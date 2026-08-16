namespace Wiki.Core;

// Guards for values that get written into a single-line, double-quoted slot -
// page/source frontmatter scalars (`title: "…"`) and wiki.yaml category
// descriptions. Neither writer escapes anything, so an embedded '"' or a
// newline would produce a file that no longer round-trips through the parser
// that has to read it back.
//
// Extracted because three services had grown a byte-identical copy of this
// loop (PageService, SourceService, CategoryService), differing only in the
// error code they raise.
public static class Scalar
{
    // `code` is the caller's, not a fixed constant: an agent branching on
    // errors[].code must not be told a wiki.yaml edit failed a *frontmatter*
    // rule, so CategoryService raises `invalid-description` where the
    // frontmatter writers raise `frontmatter-schema`.
    public static void GuardSingleLineQuotable(string value, string field, string code)
    {
        foreach (var c in value)
        {
            if (c == '"' || c == '\n' || c == '\r')
                throw new ValidationException(code, $"'{field}' may not contain quotes or newlines");
        }
    }
}

namespace Wiki.Core;

public static class Slug
{
    // Lowercase kebab: any run of non-[a-z0-9] collapses to a single '-',
    // leading/trailing '-' trimmed.
    public static string From(string title)
    {
        var sb = new System.Text.StringBuilder(title.Length);
        bool lastWasDash = false;
        foreach (var ch in title)
        {
            var lower = char.ToLowerInvariant(ch);
            var isAlnum = (lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9');
            if (isAlnum)
            {
                sb.Append(lower);
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }
        if (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        return sb.ToString();
    }

    // Appends -2, -3, ... until `exists` reports the candidate is free.
    public static string Ensure(string slug, System.Func<string, bool> exists)
    {
        if (!exists(slug)) return slug;
        var n = 2;
        string candidate;
        do
        {
            candidate = $"{slug}-{n}";
            n++;
        } while (exists(candidate));
        return candidate;
    }
}

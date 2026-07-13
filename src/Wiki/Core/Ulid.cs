namespace Wiki.Core;
public static class WikiUlid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32
    public static string New(long unixMs, System.ReadOnlySpan<byte> random)
    {
        System.Span<char> c = stackalloc char[26];
        long t = unixMs;
        for (int i = 9; i >= 0; i--) { c[i] = Alphabet[(int)(t & 31)]; t >>= 5; }
        // 80 bits of randomness → 16 base32 chars
        System.Span<byte> r = stackalloc byte[10];
        random[..10].CopyTo(r);
        int bit = 0;
        for (int i = 10; i < 26; i++)
        {
            int v = 0;
            for (int k = 0; k < 5; k++)
            {
                int b = bit + k;
                int val = (r[b / 8] >> (7 - b % 8)) & 1;
                v = (v << 1) | val;
            }
            c[i] = Alphabet[v]; bit += 5;
        }
        return new string(c);
    }
    public static bool IsValid(string s)
    {
        if (s is null || s.Length != 26) return false;
        foreach (var ch in s) if (Alphabet.IndexOf(ch) < 0) return false;
        return true;
    }
}

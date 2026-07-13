using Xunit;
using Wiki.Core;

namespace Wiki.Tests.Core;

public class UlidTests
{
    [Fact]
    public void New_Is26CrockfordChars_MonotonicTimePrefix()
    {
        var rnd = new byte[10];
        var a = WikiUlid.New(0, rnd);
        var b = WikiUlid.New(1, rnd);
        Assert.Equal(26, a.Length);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{26}$", a);
        Assert.True(string.CompareOrdinal(a, b) < 0); // later time sorts later
    }

    [Fact]
    public void New_GoldenVector_AllRandomBitsSet_AreAllZ()
    {
        // 80 random bits all set -> every 5-bit group = 31 = 'Z'.
        // Expected computed by hand from the Crockford alphabet, MSB-first.
        var rnd = new byte[10];
        for (int i = 0; i < rnd.Length; i++) rnd[i] = 0xFF;
        var ulid = WikiUlid.New(0, rnd);
        Assert.Equal("0000000000" + new string('Z', 16), ulid);
    }

    [Fact]
    public void New_GoldenVector_OnlyFirstRandomBitSet_PinsMsbFirstOrdering()
    {
        // Only the most significant bit of byte 0 is set (0x80).
        // First 5-bit group = 10000 = 16 = 'G', remaining groups all 0.
        // Expected computed by hand; this pins MSB-first ordering + byte0-bit0 position.
        var rnd = new byte[10];
        rnd[0] = 0x80;
        var ulid = WikiUlid.New(0, rnd);
        Assert.Equal("0000000000G" + new string('0', 15), ulid);
    }

    [Theory]
    [InlineData("01J9ZKM3E8W1R2X3Y4Z5A6B7C8", true)]
    [InlineData("not-a-ulid", false)]
    [InlineData("01J9ZKM3E8W1R2X3Y4Z5A6B7CI", false)] // I is not in the alphabet
    public void IsValid_ChecksLengthAndAlphabet(string s, bool expected)
        => Assert.Equal(expected, WikiUlid.IsValid(s));
}

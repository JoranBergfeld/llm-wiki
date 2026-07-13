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

    [Theory]
    [InlineData("01J9ZKM3E8W1R2X3Y4Z5A6B7C8", true)]
    [InlineData("not-a-ulid", false)]
    [InlineData("01J9ZKM3E8W1R2X3Y4Z5A6B7CI", false)] // I is not in the alphabet
    public void IsValid_ChecksLengthAndAlphabet(string s, bool expected)
        => Assert.Equal(expected, WikiUlid.IsValid(s));
}

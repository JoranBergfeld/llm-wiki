using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

public class EnvelopeTests
{
    [Fact]
    public void UnknownCommand_EmitsErrorEnvelope_Exit1()
    {
        using var v = new TempVault();
        var r = v.Run("nonesuch", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.False(r.Envelope.Ok);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-command");
    }
}

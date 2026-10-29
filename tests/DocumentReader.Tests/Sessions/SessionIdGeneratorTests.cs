using DocumentReader.Api.Sessions;

namespace DocumentReader.Tests.Sessions;

public sealed class SessionIdGeneratorTests
{
    [Fact]
    public void Create_ReturnsWellFormed43CharacterBase64UrlId()
    {
        var sessionId = SessionIdGenerator.Create();

        Assert.Equal(43, sessionId.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", sessionId);
        Assert.True(SessionIdGenerator.IsWellFormed(sessionId));
    }

    [Fact]
    public void Create_ReturnsDifferentIdsEachTime()
    {
        var sessionIds = Enumerable.Range(0, 1000).Select(_ => SessionIdGenerator.Create()).ToHashSet();

        Assert.Equal(1000, sessionIds.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 42 characters
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 44 characters
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")] // standard base64 character
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")] // padding
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA ")] // whitespace
    [InlineData("../../../../../../../../../../../etc/passwd")] // path characters
    public void IsWellFormed_RejectsInvalidValues(string? value)
    {
        Assert.False(SessionIdGenerator.IsWellFormed(value));
    }
}

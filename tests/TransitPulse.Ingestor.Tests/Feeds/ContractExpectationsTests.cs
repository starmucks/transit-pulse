namespace TransitPulse.Ingestor.Tests.Feeds;

public sealed class ContractExpectationsTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("null ")]
    [InlineData("null   ")]
    public void Bare_null_without_a_reason_fails_clearly(string line)
    {
        var error = Assert.Throws<FormatException>(() => ContractExpectations.IsDrop(line, out _));

        Assert.Contains("null <reason>", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_with_a_reason_is_a_drop()
    {
        Assert.True(ContractExpectations.IsDrop("null stale", out var reason));
        Assert.Equal("stale", reason);
    }
}

using System;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Tests.Protocol;

public sealed class SequenceTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("9007199254740993", 9007199254740993L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void DecimalStringsAreExactBeyondJavaScriptSafeInteger(string value, long expected)
    {
        var sequence = Sequence.Parse(value);
        Assert.Equal(value, sequence.Value);
        Assert.Equal(expected, sequence.ToInt64());
        Assert.Equal(sequence, Sequence.Parse(sequence.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("-0")]
    [InlineData("1e3")]
    [InlineData("1.0")]
    [InlineData(" 1")]
    [InlineData("1\n")]
    [InlineData("１")]
    [InlineData("9223372036854775808")]
    [InlineData("18446744073709551615")]
    public void AlternativeSpellingsAndOverflowAreRejected(string value)
    {
        Assert.False(Sequence.TryParse(value, out _));
        Assert.Throws<WireProtocolException>(() => Sequence.Parse(value));
    }

    [Fact]
    public void RecordOrdinalsDisallowZeroAndComparisonNeverUsesDouble()
    {
        Assert.False(Sequence.TryParse("0", out _, false));
        Assert.Throws<WireProtocolException>(() => Sequence.Parse("0", false));
        Assert.True(Sequence.Parse("9007199254740993").CompareTo(Sequence.Parse("9007199254740992")) > 0);
        Assert.Equal("0", default(Sequence).Value);
    }
}

using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class DurationParserTests
{
    [Theory]
    [InlineData(300, 300)]
    [InlineData("300", 300)]
    [InlineData("5m", 300)]
    [InlineData("7d", 604800)]
    [InlineData("48h", 172800)]
    [InlineData("1w", 604800)]
    [InlineData("-10", -10)]
    public void ParseToSeconds_AcceptsIntAndSuffixedStrings(object? value, int expected)
    {
        DurationParser.ParseToSeconds(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, 99)]
    [InlineData("", 99)]
    [InlineData("not-a-duration", 99)]
    [InlineData("5x", 99)]
    public void ParseToSeconds_ReturnsFallbackOnInvalid(object? value, int fallback)
    {
        DurationParser.ParseToSeconds(value, fallback).Should().Be(fallback);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData("10", 10)]
    [InlineData("5m", 5)]
    [InlineData("2h", 120)]
    [InlineData("1d", 1440)]
    [InlineData("1w", 10080)]
    public void ParseToMinutes_AcceptsIntAndSuffixedStrings(object? value, int expected)
    {
        DurationParser.ParseToMinutes(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, 42)]
    [InlineData("bad", 42)]
    public void ParseToMinutes_ReturnsFallbackOnInvalid(object? value, int fallback)
    {
        DurationParser.ParseToMinutes(value, fallback).Should().Be(fallback);
    }
}

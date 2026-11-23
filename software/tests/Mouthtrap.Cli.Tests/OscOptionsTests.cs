using System.Net;
using Mouthtrap.Cli;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Cli.Tests;

public sealed class OscOptionsTests
{
    [Fact]
    public void Parse_NoOption_ReturnsNoOutput()
    {
        // When
        var target = ResultAssert.Ok(OscOptions.Parse(null, false));

        // Then
        Assert.Null(target);
    }

    [Theory]
    [InlineData("127.0.0.1:9000", "127.0.0.1", 9000)]
    [InlineData("192.168.0.20:9000", "192.168.0.20", 9000)]
    [InlineData("[::1]:9000", "::1", 9000)]
    public void Parse_IpAndPort_SelectsUdp(string text, string address, int port)
    {
        // When
        var target = ResultAssert.Ok(OscOptions.Parse(text, false))!;

        // Then
        Assert.False(target.IsDryRun);
        Assert.Equal(new IPEndPoint(IPAddress.Parse(address), port), target.Endpoint);
    }

    [Fact]
    public void Parse_DryRun_HasNoEndpoint()
    {
        // When
        var target = ResultAssert.Ok(OscOptions.Parse(null, true))!;

        // Then
        Assert.True(target.IsDryRun);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:0")]
    [InlineData("localhost:9000")]
    [InlineData("127.0.0.1:70000")]
    [InlineData("")]
    public void Parse_NameMissingPortOrBadPort_Fails(string text)
    {
        // When / Then
        ResultAssert.Err(OscOptions.Parse(text, false));
    }

    [Fact]
    public void Parse_EndpointAndDryRun_Fails()
    {
        // When / Then
        ResultAssert.Err(OscOptions.Parse("127.0.0.1:9000", true));
    }

    [Fact]
    public void ParseKeys_OneOrSeveralKeys_ReturnsThemInOrder()
    {
        // When
        var one = ResultAssert.Ok(OscOptions.ParseKeys("JawOpen"));
        var several = ResultAssert.Ok(OscOptions.ParseKeys("LipPuckerUpperLeft, LipPuckerLowerRight"));

        // Then
        Assert.Equal([Mouthtrap.Core.UnifiedExpression.JawOpen], one);
        Assert.Equal([Mouthtrap.Core.UnifiedExpression.LipPuckerUpperLeft, Mouthtrap.Core.UnifiedExpression.LipPuckerLowerRight], several);
    }

    [Theory]
    [InlineData("")]
    [InlineData("JawOpen,")]
    [InlineData("jawOpen")]
    [InlineData("36")]
    [InlineData("-1")]
    [InlineData("MouthSmileLeft")]
    [InlineData("JawOpen,JawOpen")]
    public void ParseKeys_UnknownEmptyNumericOrRepeatedKey_Fails(string keys)
    {
        // When / Then
        ResultAssert.Err(OscOptions.ParseKeys(keys));
    }

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(.5, .5f)]
    [InlineData(1, 1f)]
    [InlineData(1.5, .5f)]
    [InlineData(2, 0f)]
    [InlineData(2.5, .5f)]
    public void Triangle_TimeWithinPeriod_RisesAndFallsOncePerPeriod(double seconds, float expected)
    {
        // When
        var value = OscOptions.Triangle(seconds, 2);

        // Then
        Assert.Equal(expected, value, 5);
    }
}

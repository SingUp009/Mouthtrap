using System.Net;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Core.Tests;

public sealed class DeadzoneTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(.15f)]
    [InlineData(.49f)]
    public void Create_ZeroToBelowHalf_KeepsTheValue(float value)
    {
        // When / Then
        Assert.Equal(value, ResultAssert.Ok(Deadzone.Create(value)).Value);
    }

    [Theory]
    [InlineData(-.01f)]
    [InlineData(.5f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Create_OutsideZeroToHalf_FailsWithItsName(float value)
    {
        // When / Then
        Assert.Equal(nameof(Deadzone), ResultAssert.Err(Deadzone.Create(value)).Name);
    }

    [Theory]
    [InlineData(.1f, 0f)]
    [InlineData(.2f, 0f)]
    [InlineData(.6f, .5f)]
    [InlineData(1f, 1f)]
    public void Apply_PointTwo_ZeroesUpToItAndStretchesTheRest(float value, float expected)
    {
        // Given
        var deadzone = ResultAssert.Ok(Deadzone.Create(.2f));

        // When / Then
        Assert.Equal(expected, deadzone.Apply(value), 5);
    }

    [Fact]
    public void Apply_None_KeepsTheValue()
    {
        // When / Then
        Assert.Equal(.01f, Deadzone.None.Apply(.01f));
    }
}

public sealed class StreamUrlTests
{
    [Theory]
    [InlineData("http://192.168.0.20/stream")]
    [InlineData("https://camera.example/stream")]
    public void Create_AbsoluteHttpUrl_KeepsTheAddress(string text)
    {
        // When / Then
        Assert.Equal(new Uri(text), ResultAssert.Ok(StreamUrl.Create(text)).Address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/stream")]
    [InlineData("ftp://camera/stream")]
    [InlineData("http://user:secret@camera/stream")]
    public void Create_NotAnHttpUrlOrWithCredentials_Fails(string? text)
    {
        // When / Then
        Assert.Equal(nameof(StreamUrl), ResultAssert.Err(StreamUrl.Create(text)).Name);
    }
}

public sealed class OscEndpointTests
{
    [Theory]
    [InlineData("127.0.0.1:9000", "127.0.0.1", 9000)]
    [InlineData("[::1]:9000", "::1", 9000)]
    public void Create_IpLiteralWithPort_KeepsTheAddress(string text, string address, int port)
    {
        // When / Then
        Assert.Equal(new IPEndPoint(IPAddress.Parse(address), port), ResultAssert.Ok(OscEndpoint.Create(text)).Address);
    }

    [Theory]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:70000")]
    [InlineData("localhost:9000")]
    [InlineData("9000")]
    [InlineData(null)]
    public void Create_NameMissingPortOrBadPort_Fails(string? text)
    {
        // When / Then
        Assert.Equal(nameof(OscEndpoint), ResultAssert.Err(OscEndpoint.Create(text)).Name);
    }
}

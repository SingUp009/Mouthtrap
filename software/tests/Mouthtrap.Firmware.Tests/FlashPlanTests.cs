using Mouthtrap.Firmware;
using Mouthtrap.Tests;

namespace Mouthtrap.Firmware.Tests;

public sealed class FlashPlanTests
{
    private const uint Flash = 8 * 1024 * 1024;
    private static readonly byte[] Data = [1, 2, 3];

    [Fact]
    public void Create_PartsInAnyOrder_OrdersThemByOffset()
    {
        // When
        var plan = ResultAssert.Ok(FlashPlan.Create([new(0x9000, Data, "settings"), new(0x0, Data, "bootloader")], Flash));

        // Then
        Assert.Equal(["bootloader", "settings"], plan.Parts.Select(part => part.Name));
        Assert.Equal(6, plan.TotalBytes);
    }

    [Theory]
    [InlineData(0x100u, "4 KiB boundary")]
    [InlineData(Flash - 2, "4 KiB boundary")]
    [InlineData(Flash - 0x1000 + 0x1000, "does not fit")]
    public void Create_PartAtABadOffset_ReturnsInvalidValue(uint offset, string message)
    {
        // When / Then
        Assert.Contains(message, ResultAssert.Err(FlashPlan.Create([new(offset, Data, "part")], Flash)).Message);
    }

    [Fact]
    public void Create_OverlappingParts_ReturnsInvalidValue()
    {
        // Given
        var large = new byte[0x2000];

        // When / Then
        Assert.Contains("overlaps", ResultAssert.Err(FlashPlan.Create([new(0x0, large, "first"), new(0x1000, Data, "second")], Flash)).Message);
    }

    [Fact]
    public void Create_EmptyPart_ReturnsInvalidValue()
    {
        // When / Then
        Assert.Contains("empty", ResultAssert.Err(FlashPlan.Create([new(0x0, Array.Empty<byte>(), "empty part")], Flash)).Message);
    }

    [Fact]
    public void Create_NoParts_ReturnsInvalidValue()
    {
        // When / Then
        Assert.Contains("nothing", ResultAssert.Err(FlashPlan.Create([], Flash)).Message);
    }

    [Fact]
    public void With_SettingsOverTheApp_ReturnsInvalidValue()
    {
        // Given
        var plan = ResultAssert.Ok(FlashPlan.Create([new(0x8000, new byte[0x2000], "partition table")], Flash));

        // When / Then
        Assert.Contains("overlaps", ResultAssert.Err(plan.With(new(0x9000, Data, "settings"))).Message);
    }
}

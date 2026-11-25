using Mouthtrap.Core;
using Xunit;

namespace Mouthtrap.Core.Tests;

public sealed class ExpectedDeviceConfirmationTests
{
    private static readonly DeviceReport Xiao = new("34:85:18:aa:bb:cc", "ESP32-S3 (QFN56) (revision v0.2)", 8 << 20, 8 << 20, []);

    [Theory]
    [InlineData("34:85:18:aa:bb:cc")]
    [InlineData("34:85:18:AA:BB:CC")]
    [InlineData(" 34:85:18:aa:bb:cc ")]
    public async Task ConfirmAsync_TheExpectedDevice_ConfirmsItAndKeepsTheReport(string identity)
    {
        // Given
        var confirmation = new ExpectedDeviceConfirmation(identity);

        // When
        var confirmed = await confirmation.ConfirmAsync(Xiao, TestContext.Current.CancellationToken);

        // Then
        Assert.True(confirmed);
        Assert.Same(Xiao, confirmation.Reported);
    }

    [Fact]
    public async Task ConfirmAsync_AnotherDevice_Declines()
    {
        // Given
        var confirmation = new ExpectedDeviceConfirmation("34:85:18:aa:bb:cd");

        // When / Then
        Assert.False(await confirmation.ConfirmAsync(Xiao, TestContext.Current.CancellationToken));
        Assert.Same(Xiao, confirmation.Reported);
    }

    [Fact]
    public async Task ConfirmAsync_TheExpectedDeviceWithAProblem_Declines()
    {
        // Given
        var confirmation = new ExpectedDeviceConfirmation(Xiao.Identity);
        var unsuitable = Xiao with { Problems = [new(DeviceProblemKind.MemoryMissing, "No PSRAM.")] };

        // When / Then
        Assert.False(await confirmation.ConfirmAsync(unsuitable, TestContext.Current.CancellationToken));
    }
}

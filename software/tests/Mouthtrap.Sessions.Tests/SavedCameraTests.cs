using FlashCap;
using FlashCap.Utilities;
using Mouthtrap.Core;
using Mouthtrap.Sessions;
using Xunit;

namespace Mouthtrap.Sessions.Tests;

public sealed class SavedCameraTests
{
    private static readonly CameraModeSettings Saved = new(1280, 720, "JPEG", 30);

    [Theory]
    [InlineData(1280, 720, 30, 1)]
    [InlineData(1280, 720, 2997, 100)]
    public void Matches_SameSizeFormatAndCloseRate_IsTrue(int width, int height, int numerator, int denominator)
    {
        // Given
        var mode = new VideoCharacteristics(PixelFormats.JPEG, width, height, new Fraction(numerator, denominator));

        // When / Then
        Assert.True(SavedCamera.Matches(mode, Saved));
    }

    [Theory]
    [InlineData(PixelFormats.JPEG, 1920, 1080, 30)]
    [InlineData(PixelFormats.YUYV, 1280, 720, 30)]
    [InlineData(PixelFormats.JPEG, 1280, 720, 15)]
    public void Matches_DifferentSizeFormatOrRate_IsFalse(PixelFormats format, int width, int height, int fps)
    {
        // Given
        var mode = new VideoCharacteristics(format, width, height, fps);

        // When / Then
        Assert.False(SavedCamera.Matches(mode, Saved));
    }
}

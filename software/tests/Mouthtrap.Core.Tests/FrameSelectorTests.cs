using Mouthtrap.Core;
using Xunit;

namespace Mouthtrap.Core.Tests;

public sealed class FrameSelectorTests
{
    private static readonly Rgb24FrameImage Red = new([255, 0, 0], 1, 1, 3);
    private static readonly Rgb24FrameImage Blue = new([0, 0, 255], 1, 1, 3);
    private static readonly TrackingControlState First = new(PreprocessSettings.Default, 0);
    private static readonly TrackingControlState Second = new(PreprocessSettings.Default, 1);

    [Fact]
    public void Select_NothingArrivedOrHeld_ReturnsNull()
    {
        // Given
        var selector = new FrameSelector();

        // When / Then
        Assert.Null(selector.Select(null, 0, First));
    }

    [Fact]
    public void Select_FirstFrame_SelectsItAsANewRevision()
    {
        // Given
        var selector = new FrameSelector();

        // When
        var selected = selector.Select(Red, 10, First);

        // Then
        Assert.Equal(new FrameSelection(Red, 10, First, SettingsChanged: true), selected);
    }

    [Fact]
    public void Select_NewFrameWithSameSettings_SelectsItWithoutSettingsChange()
    {
        // Given
        var selector = new FrameSelector();
        selector.Select(Red, 10, First);

        // When
        var selected = selector.Select(Blue, 20, First);

        // Then
        Assert.Equal(new FrameSelection(Blue, 20, First, SettingsChanged: false), selected);
    }

    [Fact]
    public void Select_NothingArrivedAndSettingsUnchanged_ReturnsNull()
    {
        // Given
        var selector = new FrameSelector();
        selector.Select(Red, 10, First);

        // When / Then
        Assert.Null(selector.Select(null, 0, First));
    }

    [Fact]
    public void Select_SettingsChangedWithoutFrame_ReselectsHeldFrameOnce()
    {
        // Given
        var selector = new FrameSelector();
        selector.Select(Red, 10, First);

        // When
        var reselected = selector.Select(null, 0, Second);

        // Then
        Assert.Equal(new FrameSelection(Red, 10, Second, SettingsChanged: true), reselected);
        Assert.Null(selector.Select(null, 0, Second));
    }

    [Fact]
    public void Select_SettingsChangedBeforeFirstFrame_ReturnsNull()
    {
        // Given
        var selector = new FrameSelector();

        // When / Then
        Assert.Null(selector.Select(null, 0, Second));
    }
}

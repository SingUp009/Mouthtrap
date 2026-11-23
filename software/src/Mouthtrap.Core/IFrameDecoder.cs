using Moderato.Results;

namespace Mouthtrap.Core;

public interface IFrameDecoder
{
    // Called serially on the tracking thread, never the UI thread. An Rgb24FrameImage may be returned as is; an image
    // that cannot be decoded is an InputDataInvalid.
    Result<Rgb24FrameImage, InputDataInvalid> ToRgb24(FrameImage image);
}

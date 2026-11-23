using System.IO;
using System.Windows.Media.Imaging;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Inputs;

// WIC decoding through frozen WPF imaging objects; usable from thread-pool threads.
public sealed class FrameImageDecoder : IFrameDecoder
{
    public static FrameImageDecoder Instance { get; } = new();

    private FrameImageDecoder() { }

    // A frame the codecs cannot decode, or one beyond the size limit, is an expected data error from the input.
    // FrameImage has no other cases, so an unknown one is a contract violation.
    public Result<Rgb24FrameImage, InputDataInvalid> ToRgb24(FrameImage frame) => frame switch
    {
        Rgb24FrameImage rgb => new Ok<Rgb24FrameImage>(rgb),
        EncodedFrameImage encoded => Decode(encoded).AndThen(MediaFrames.ToRgb24),
        _ => throw new NotSupportedException("Unknown frame image format.")
    };

    // WIC reports undecodable data as NotSupportedException or FileFormatException while the image initializes.
    private static Result<BitmapSource, InputDataInvalid> Decode(EncodedFrameImage frame)
    {
        try
        {
            using var stream = new MemoryStream(frame.Data, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return new Ok<BitmapSource>(image);
        }
        catch (Exception exception) when (exception is NotSupportedException or FileFormatException)
        {
            return new Err<InputDataInvalid>(new(exception.Message));
        }
    }
}

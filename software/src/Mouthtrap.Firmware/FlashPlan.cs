using System.Buffers.Binary;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Firmware;

// One file to write at Offset. Name says what it is, for progress and errors.
public sealed record FlashPart(uint Offset, ReadOnlyMemory<byte> Data, string Name);

// What to write to a board, checked before anything is opened: sector-aligned parts that do not overlap and fit the
// flash, and ESP images built for the ESP32-S3.
public sealed record FlashPlan
{
    public const uint SectorSize = 0x1000;
    private const byte ImageMagic = 0xe9;

    private FlashPlan(FlashPart[] parts, uint flashSize)
    {
        Parts = parts;
        FlashSize = flashSize;
    }

    public IReadOnlyList<FlashPart> Parts { get; }
    public uint FlashSize { get; }
    public long TotalBytes => Parts.Sum(part => (long)part.Data.Length);

    public static Result<FlashPlan, InvalidValue> Create(IEnumerable<FlashPart> parts, uint flashSize)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var ordered = parts.OrderBy(part => part.Offset).ToArray();
        if (ordered.Length == 0) return Invalid("There is nothing to write.");
        if (flashSize is 0 || flashSize % SectorSize != 0) return Invalid("The flash size must be a whole number of 4 KiB sectors.");
        uint end = 0;
        foreach (var part in ordered)
        {
            if (part.Data.IsEmpty) return Invalid($"{part.Name} is empty.");
            if (part.Offset % SectorSize != 0) return Invalid($"{part.Name} must start on a 4 KiB boundary, not at 0x{part.Offset:x}.");
            if (part.Offset < end) return Invalid($"{part.Name} at 0x{part.Offset:x} overlaps the part before it.");
            if ((ulong)part.Offset + (ulong)part.Data.Length > flashSize) return Invalid($"{part.Name} does not fit the {flashSize / 1024 / 1024} MB flash.");
            if (!ForEsp32S3(part.Data.Span)) return Invalid($"{part.Name} is not an ESP32-S3 image.");
            end = part.Offset + (uint)part.Data.Length;
        }
        return new Ok<FlashPlan>(new(ordered, flashSize));
    }

    // The same plan with one more part, such as the Wi-Fi settings.
    public Result<FlashPlan, InvalidValue> With(FlashPart part) => Create([.. Parts, part], FlashSize);

    // An ESP image (the bootloader and the app) names its chip in the extended header; other data is not checked.
    private static bool ForEsp32S3(ReadOnlySpan<byte> data) =>
        data[0] != ImageMagic || (data.Length >= 24 && BinaryPrimitives.ReadUInt16LittleEndian(data[12..]) == RomLoader.Esp32S3ChipId);

    private static Err<InvalidValue> Invalid(string message) => new(new(nameof(FlashPlan), message));
}
